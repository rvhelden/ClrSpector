using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// One link in a suspended async method's continuation chain, as .NET 11's runtime async
    /// represents it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runtime async replaces the compiler's state-machine struct with a real heap object per
    /// suspension: a <c>Continuation</c> holding where to resume, what state to resume with, and
    /// the next continuation to run after it. A suspended <c>await</c> chain is therefore a
    /// linked list on the heap, and walking it shows what a task is actually waiting to do -
    /// something the compiler-generated state machines never made visible.
    /// </para>
    /// <para>
    /// <b>Unverified against a live instance.</b> The layout here is taken from the runtime's
    /// contract and cross-checked field by field against the managed
    /// <c>System.Runtime.CompilerServices.Continuation</c> type's own field offsets, but this
    /// preview runtime never produced a Continuation object to decode - its async methods still
    /// compile to state machines. Treat the reads as correct in shape and unproven in practice
    /// until a runtime that emits them is available.
    /// </para>
    /// </remarks>
    public sealed unsafe class ClrContinuation
    {
        private ClrContinuation()
        {
        }

        /// <summary>The Continuation object on the heap.</summary>
        public IntPtr Address { get; private set; }

        /// <summary>The next continuation to run, or zero at the end of the chain.</summary>
        public IntPtr Next { get; private set; }

        /// <summary>
        /// The resume info describing where execution picks up, or zero when there is none.
        /// </summary>
        public IntPtr ResumeInfo { get; private set; }

        /// <summary>The resumption state - which await inside the method to return to.</summary>
        public int State { get; private set; }

        /// <summary>
        /// The instruction pointer the resume info names, for diagnostics, or zero when the
        /// runtime does not publish one.
        /// </summary>
        public IntPtr DiagnosticInstructionPointer { get; private set; }

        /// <summary>
        /// Decodes the continuation at <paramref name="address"/>. No check is made that the
        /// address really holds one.
        /// </summary>
        public static ClrContinuation At(IntPtr address)
        {
            if (address == IntPtr.Zero)
                return null;

            var descriptor = ContractDescriptor.Current;
            var layout = descriptor.GetDataType("ContinuationObject");
            var reader = new MemoryReader(address);

            var continuation = new ClrContinuation
            {
                Address = address,
                Next = reader.ReadIntPtr(layout["Next"]),
                ResumeInfo = reader.ReadIntPtr(layout["ResumeInfo"]),
                State = reader.ReadInt(layout["State"])
            };

            if (continuation.ResumeInfo != IntPtr.Zero
                && descriptor.TryGetDataType("AsyncResumeInfo", out var resumeLayout)
                && resumeLayout.HasField("DiagnosticIP"))
            {
                continuation.DiagnosticInstructionPointer =
                    new MemoryReader(continuation.ResumeInfo).ReadIntPtr(resumeLayout["DiagnosticIP"]);
            }

            return continuation;
        }

        /// <summary>Decodes a continuation you already hold a reference to.</summary>
        public static ClrContinuation Of(object continuation)
        {
            if (continuation == null) throw new ArgumentNullException(nameof(continuation));

            return At(ClrHeapObject.AddressOf(continuation));
        }

        /// <summary>
        /// This continuation and everything queued behind it, in resume order.
        /// </summary>
        public IReadOnlyList<ClrContinuation> Chain()
        {
            var chain = new List<ClrContinuation>();
            var link = this;

            // A cycle would be a runtime bug, but a bounded walk beats hanging on one.
            while (link != null && chain.Count < 4096)
            {
                chain.Add(link);
                link = At(link.Next);
            }

            return chain;
        }

        /// <summary>The chain rendered one link per line, with each resume point named.</summary>
        public string Dump()
        {
            var text = new System.Text.StringBuilder();
            var map = ClrCodeMap.Current;

            foreach (var link in this.Chain())
            {
                var where = link.DiagnosticInstructionPointer == IntPtr.Zero
                    ? "<no resume ip>"
                    : map.Find(link.DiagnosticInstructionPointer)?.ToString()
                      ?? $"0x{link.DiagnosticInstructionPointer.ToInt64():x}";

                text.AppendLine($"   resume state={link.State} at {where}");
            }

            return text.ToString().TrimEnd();
        }

        public override string ToString()
        {
            return $"Continuation @0x{this.Address.ToInt64():x} state={this.State} " +
                   $"next=0x{this.Next.ToInt64():x} resumeInfo=0x{this.ResumeInfo.ToInt64():x}";
        }
    }
}
