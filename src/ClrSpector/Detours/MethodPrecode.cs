using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ClrSpector.Detours
{
    /// <summary>
    /// A method's precode: the stub that stands in for its entry point and forwards to wherever
    /// the real code currently lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The precode exists because the real code moves - the method may not be jitted yet, and
    /// tiered compilation may replace it later - so callers need one stable address to call.
    /// </para>
    /// <para>
    /// On x64 a FixupPrecode is a single rip-relative jump, <c>jmp qword [rip+disp32]</c>
    /// (<c>ff 25 disp32</c>), which forwards through one pointer-sized <see cref="DispatchSlot"/>.
    /// That slot is the whole reason the method body never needs to be located: redirecting the
    /// method is one reversible store into it.
    /// </para>
    /// <para>
    /// Note that a <b>virtual</b> call does not come through here at all - it reads the target
    /// from the receiver's MethodTable vtable instead. See <see cref="MethodVtable"/>.
    /// </para>
    /// </remarks>
    public sealed unsafe class MethodPrecode
    {
        /// <summary>Opcode bytes for <c>jmp qword [rip+disp32]</c>.</summary>
        private const byte JmpOpcode0 = 0xFF;
        private const byte JmpOpcode1 = 0x25;

        /// <summary>
        /// Length of the rip-relative jump. A property of the x86-64 instruction encoding: the
        /// displacement is relative to the address of the *next* instruction.
        /// </summary>
        internal const int JmpInstructionLength = 6;

        /// <summary>A floor for the capture, for a runtime that publishes no precode sizes.</summary>
        private const int MinimumPreviewLength = 16;

        /// <summary>
        /// How many entry-point bytes are captured for inspection: enough to hold whichever
        /// precode template this runtime publishes, so a precode can be matched against the
        /// runtime's own bytes rather than against opcodes written down here.
        /// </summary>
        private static int PreviewLength
        {
            get
            {
                var machine = Machine;
                var longest = Math.Max((int)(machine.StubPrecodeSize ?? 0), (int)(machine.FixupStubPrecodeSize ?? 0));

                return Math.Max(MinimumPreviewLength, longest);
            }
        }

        private MethodPrecode(MethodBase method, IntPtr entryPoint, byte[] bytes)
        {
            this.Method = method;
            this.EntryPoint = entryPoint;
            this.EntryPointBytes = bytes;
        }

        public MethodBase Method { get; }

        /// <summary>The method's stable entry point - the precode, not the method body.</summary>
        public IntPtr EntryPoint { get; }

        /// <summary>The first bytes at the entry point, for inspection and diagnostics.</summary>
        public byte[] EntryPointBytes { get; }

        /// <summary>
        /// Whether the entry point matches the FixupPrecode template this runtime published.
        /// </summary>
        /// <remarks>
        /// This asks the runtime what its own precodes look like rather than testing for an
        /// opcode, so it stays right on a machine whose precodes are built differently. The
        /// varying positions - the embedded addresses - are excluded by the runtime's companion
        /// "ignored bytes" mask.
        /// </remarks>
        public bool IsFixupPrecode => Machine.IsFixupPrecode(this.EntryPointBytes);

        /// <summary>Whether the entry point matches this runtime's StubPrecode template.</summary>
        public bool IsStubPrecode => Machine.IsStubPrecode(this.EntryPointBytes);

        /// <summary>Whether the entry point is the rip-relative jump shape this library decodes.</summary>
        public bool IsRipRelativeJump =>
            this.EntryPointBytes.Length >= 2
            && this.EntryPointBytes[0] == JmpOpcode0
            && this.EntryPointBytes[1] == JmpOpcode1;

        /// <summary>Whether a single dispatch slot could be located behind this precode.</summary>
        public bool HasDispatchSlot => this.DispatchSlot != IntPtr.Zero;

        /// <summary>
        /// The pointer-sized slot the precode jumps through. Writing here redirects every
        /// non-virtual call to the method.
        /// </summary>
        public IntPtr DispatchSlot { get; private set; }

        /// <summary>The address the dispatch slot currently points at.</summary>
        public IntPtr DispatchTarget =>
            this.HasDispatchSlot ? *(IntPtr*)this.DispatchSlot : IntPtr.Zero;

        /// <summary>The runtime's precode constants for this machine.</summary>
        public static PrecodeMachineInfo Machine => PrecodeMachineInfo.Current;

        /// <summary>
        /// Reads <paramref name="method"/>'s precode. The method is jitted first, since an entry
        /// point is meaningless until it has one.
        /// </summary>
        public static MethodPrecode Of(MethodBase method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));

            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            var entryPoint = method.MethodHandle.GetFunctionPointer();

            var captureLength = PreviewLength;
            var bytes = new byte[captureLength];
            if (entryPoint != IntPtr.Zero)
            {
                var code = (byte*)entryPoint;
                for (var i = 0; i < captureLength; i++)
                    bytes[i] = code[i];
            }

            var precode = new MethodPrecode(method, entryPoint, bytes);

            if (precode.IsRipRelativeJump)
            {
                var displacement = BitConverter.ToInt32(bytes, 2);
                precode.DispatchSlot = (IntPtr)((byte*)entryPoint + JmpInstructionLength + displacement);
            }

            return precode;
        }

        /// <summary>The decoded jump, when the entry point is one.</summary>
        public string Disassembly
        {
            get
            {
                if (!this.IsRipRelativeJump)
                    return "<not a rip-relative jump>";

                var displacement = BitConverter.ToInt32(this.EntryPointBytes, 2);

                return $"jmp qword [rip{displacement:+#;-#;+0}]";
            }
        }

        public string HexBytes => string.Join(" ", this.EntryPointBytes.Select(b => b.ToString("x2")));

        public override string ToString()
        {
            var kind = this.IsFixupPrecode ? "fixup" : this.IsStubPrecode ? "stub" : "unrecognised";

            return $"{this.Method.DeclaringType?.Name}.{this.Method.Name} {kind} " +
                   $"entryPoint=0x{this.EntryPoint.ToInt64():x} [{this.HexBytes}] " +
                   $"{this.Disassembly} slot=0x{this.DispatchSlot.ToInt64():x} " +
                   $"-> 0x{this.DispatchTarget.ToInt64():x}";
        }
    }
}
