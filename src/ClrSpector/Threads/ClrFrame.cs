using System;
using System.Collections.Generic;
using System.Linq;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// One entry in a thread's explicit frame chain, and whatever it records about where the
    /// thread was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A running thread's instruction pointer is not recorded anywhere a reader can see.</b>
    /// It lives in that thread's registers and on its stack, and the only ways to it are to
    /// suspend the thread and call <c>GetThreadContext</c>, or to be that thread. Nothing in the
    /// runtime's own structures caches it - so there is no field to find, and looking harder does
    /// not help.
    /// </para>
    /// <para>
    /// What the runtime does keep is this chain. Whenever a thread crosses a boundary the jitted
    /// code cannot describe on its own - a P/Invoke, a stub, a hijack for suspension, an
    /// exception dispatch - it pushes an explicit <c>Frame</c> onto a list hanging off
    /// <see cref="ClrThread.Frame"/>, and that frame records what was needed to get back:
    /// a return address, a MethodDesc, or a saved register context. That is the readable answer
    /// to "where is this thread", and for a thread parked in native code it is an exact one - the
    /// return address is the managed call site.
    /// </para>
    /// <para>
    /// A frame no longer identifies itself by C++ vtable. In .NET 11 the first pointer-sized slot
    /// is a small <c>FrameIdentifier</c> enum value, and the descriptor publishes one
    /// <c>&lt;Name&gt;FrameIdentifier</c> global per kind - so the kind is found by reading that
    /// integer and matching it, and the kind's name is then the name of the descriptor type
    /// describing its fields. Looking for a vtable pointer there finds a small integer like
    /// <c>0x12</c> and matches nothing.
    /// </para>
    /// </remarks>
    public sealed class ClrFrame
    {
        /// <summary>
        /// The end of the chain. <c>FRAME_TOP</c> is <c>~0</c>, not null - a thread that has
        /// pushed no frames at all has this as its <see cref="ClrThread.Frame"/>.
        /// </summary>
        internal const long FrameTop = ~0L;

        /// <summary>
        /// The subclasses of <c>FramedMethodFrame</c>, which record the MethodDesc they are
        /// standing in for.
        /// </summary>
        /// <remarks>
        /// The descriptor publishes <c>FramedMethodFrame</c>'s fields but not which frames derive
        /// from it, so the list is taken from the runtime's own declarations in
        /// <c>frames.h</c>. Reading <c>MethodDescPtr</c> off a frame that is not one of these
        /// would read whatever that frame keeps at the same offset.
        /// </remarks>
        private static readonly string[] MethodFrames =
        {
            "FramedMethodFrame",
            "PrestubMethodFrame",
            "StubDispatchFrame",
            "CallCountingHelperFrame",
            "ExternalMethodFrame",
            "DynamicHelperFrame",
            "InterpreterFrame"
        };

        private static readonly Dictionary<long, string> KindsByIdentifier = ReadKinds();

        /// <summary>The frame's own address.</summary>
        public IntPtr Address { get; private set; }

        /// <summary>The raw <c>FrameIdentifier</c> value in the frame's first slot.</summary>
        public long Identifier { get; private set; }

        /// <summary>
        /// The frame's kind, as the runtime names it - <c>InlinedCallFrame</c>,
        /// <c>StubDispatchFrame</c> and so on.
        /// </summary>
        /// <remarks>
        /// An identifier the descriptor does not name comes back as
        /// <c>Unknown(&lt;value&gt;)</c> rather than as a guess, and nothing else is read off
        /// such a frame - the offsets to read depend entirely on which kind it is, so reading
        /// them anyway would produce values from whatever that kind keeps at the same place.
        /// This is not only a forward-compatibility concern: a chain is mutated by the thread
        /// that owns it, so a snapshot taken while a frame is being pushed can catch a
        /// half-written identifier.
        /// </remarks>
        public string Kind { get; private set; }

        /// <summary>
        /// The managed return address this frame recorded, or zero when its kind records none.
        /// </summary>
        /// <remarks>
        /// This is the closest thing to an instruction pointer that is actually readable: for a
        /// thread sitting in a P/Invoke it is the address in the managed method that made the
        /// call.
        /// </remarks>
        public IntPtr ReturnAddress { get; private set; }

        /// <summary>
        /// The MethodDesc this frame stands in for, or zero when its kind records none.
        /// </summary>
        public IntPtr MethodDescPointer { get; private set; }

        /// <summary>
        /// True when the frame's kind saves a whole register context rather than a return address.
        /// </summary>
        /// <remarks>
        /// The saved context holds the instruction pointer, but its layout is the platform's
        /// <c>CONTEXT</c> structure, which the descriptor does not publish - so the frame is
        /// reported as carrying one rather than being decoded with hardcoded offsets.
        /// </remarks>
        public bool HasSavedContext { get; private set; }

        /// <summary>
        /// The method this frame points at: the one it stands in for, or the one containing
        /// <see cref="ReturnAddress"/>.
        /// </summary>
        public ClrMethodDescription Method
        {
            get
            {
                if (this.MethodDescPointer != IntPtr.Zero)
                    return this.SafeMethod(this.MethodDescPointer);

                var block = this.CodeBlock;

                return block == null || block.MethodDesc == IntPtr.Zero
                    ? null
                    : this.SafeMethod(block.MethodDesc);
            }
        }

        /// <summary>
        /// The jitted code containing <see cref="ReturnAddress"/>, which also gives the offset
        /// into the method.
        /// </summary>
        public ClrCodeBlock CodeBlock =>
            this.ReturnAddress == IntPtr.Zero ? null : ClrCodeMap.Current.Find(this.ReturnAddress);

        /// <summary>Reads the frame at <paramref name="address"/>.</summary>
        internal static ClrFrame At(IntPtr address)
        {
            var descriptor = ContractDescriptor.Current;
            var reader = new MemoryReader(address);

            var frame = new ClrFrame
            {
                Address = address,
                Identifier = reader.ReadIntPtr(0).ToInt64()
            };

            frame.Kind = KindsByIdentifier.TryGetValue(frame.Identifier, out var kind)
                ? kind
                : $"Unknown({frame.Identifier})";

            // The kind's name is the name of the descriptor type describing it, so the fields to
            // read come from the descriptor rather than from a table of offsets here.
            if (descriptor.TryGetDataType(frame.Kind, out var layout))
            {
                if (layout.HasField("ReturnAddress"))
                    frame.ReturnAddress = reader.ReadIntPtr(layout["ReturnAddress"]);
                else if (layout.HasField("CallerReturnAddress"))
                    frame.ReturnAddress = reader.ReadIntPtr(layout["CallerReturnAddress"]);

                frame.HasSavedContext =
                    layout.HasField("TargetContext") || layout.HasField("TargetContextPtr");
            }

            if (MethodFrames.Contains(frame.Kind)
                && descriptor.TryGetDataType("FramedMethodFrame", out var method)
                && method.HasField("MethodDescPtr"))
            {
                frame.MethodDescPointer = reader.ReadIntPtr(method["MethodDescPtr"]);
            }

            return frame;
        }

        /// <summary>
        /// The chain hanging off <paramref name="head"/>, nearest frame first.
        /// </summary>
        /// <remarks>
        /// Guarded the way every linked-list walk here is: a capped length, and a stop on a node
        /// that points at itself. The chain is per-thread and mutated by that thread as it runs,
        /// so a walk of another thread's chain is a snapshot that can already be stale - and a
        /// torn link is a real possibility rather than a corrupt heap.
        /// </remarks>
        internal static IReadOnlyList<ClrFrame> Chain(IntPtr head)
        {
            var frames = new List<ClrFrame>();

            if (!ContractDescriptor.Current.TryGetDataType("Frame", out var layout)
                || !layout.HasField("Next"))
            {
                return frames;
            }

            var address = head;

            while (address != IntPtr.Zero && address.ToInt64() != FrameTop && frames.Count < 512)
            {
                if (!ProcessMemoryRegions.IsReadable(address, layout["Next"] + IntPtr.Size))
                    break;

                frames.Add(At(address));

                var next = new MemoryReader(address).ReadIntPtr(layout["Next"]);

                if (next == address)
                    break;

                address = next;
            }

            return frames;
        }

        /// <summary>
        /// A MethodDesc that a frame points at, or null when it will not decode.
        /// </summary>
        /// <remarks>
        /// A frame read from a thread that is running can hold a stale pointer, so this is
        /// tolerant by design: a frame is still worth reporting when the method it names is not.
        /// </remarks>
        private ClrMethodDescription SafeMethod(IntPtr pointer)
        {
            try
            {
                return ClrMethodDescription.At(pointer);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The <c>FrameIdentifier</c> value of every frame kind the descriptor names.
        /// </summary>
        /// <remarks>
        /// Every such global is a literal small integer - a <c>FrameIdentifier</c> enum member -
        /// so these are read as numbers. Reading one as an address gives zero, which is what makes
        /// the mistake look like "the descriptor does not publish these".
        /// </remarks>
        private static Dictionary<long, string> ReadKinds()
        {
            var kinds = new Dictionary<long, string>();
            var descriptor = ContractDescriptor.Current;

            foreach (var name in descriptor.Globals.Names)
            {
                if (!name.EndsWith("FrameIdentifier", StringComparison.Ordinal))
                    continue;

                try
                {
                    kinds[(long)descriptor.Globals.Number(name)] =
                        name.Substring(0, name.Length - "Identifier".Length);
                }
                catch (Exception)
                {
                    // A global that will not read as a number simply names no kind here.
                }
            }

            return kinds;
        }

        public override string ToString()
        {
            var where = this.Method == null
                ? string.Empty
                : $" in {this.Method.DeclaringTypeName}::{this.Method.Name}";

            var offset = this.CodeBlock?.OffsetIntoMethod;

            if (offset > 0)
                where += $"+0x{offset:x}";

            var ip = this.ReturnAddress == IntPtr.Zero
                ? string.Empty
                : $" ip=0x{this.ReturnAddress.ToInt64():x}";

            var context = this.HasSavedContext ? " (saves a register context)" : string.Empty;

            return $"{this.Kind}{ip}{where}{context}";
        }
    }
}
