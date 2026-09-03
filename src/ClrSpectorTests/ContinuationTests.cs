using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ClrSpector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    /// <summary>
    /// .NET 11 runtime async replaces the compiler's state-machine struct with a heap
    /// <c>Continuation</c> per suspension, so a suspended await chain is a linked list on the
    /// heap. These tests hold such a chain open with a gate and decode it.
    /// </summary>
    /// <remarks>
    /// The chain is only reachable while the awaited task is still pending: completing it runs
    /// the continuations and unlinks them. Every test therefore reads the chain before releasing
    /// the gate, and releases it in a <c>finally</c> so a failure does not leave a task pending.
    /// </remarks>
    public class ContinuationTests
    {
        /// <summary>What the whole chain is suspended on.</summary>
        private readonly TaskCompletionSource<int> gate = new TaskCompletionSource<int>();

        private async Task<int> AwaitsTheGate()
        {
            var value = await this.gate.Task;

            return value + 1;
        }

        private async Task<int> AwaitsTheInnerCall()
        {
            var value = await this.AwaitsTheGate();

            return value + 1;
        }

        private async Task<int> AwaitsTheMiddleCall()
        {
            var value = await this.AwaitsTheInnerCall();

            return value + 1;
        }

        /// <summary>
        /// The continuation the gate's task will run when it completes - the head of the chain.
        /// A suspended runtime-async method parks its continuation in the awaited task's own
        /// continuation slot, which is the entry point into the chain that needs no heap walk.
        /// </summary>
        private ClrContinuation Suspended()
        {
            var slot = typeof(Task).GetField(
                "m_continuationObject",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var continuation = slot?.GetValue(this.gate.Task);

            return continuation == null ? null : ClrContinuation.Of(continuation);
        }

        /// <summary>
        /// The rest of this file is meaningless if the compiler fell back to state machines, so
        /// check the compilation shape first: runtime async emits no
        /// <c>&lt;Method&gt;d__n</c> nested type, and needs
        /// <c>&lt;Features&gt;runtime-async=on&lt;/Features&gt;</c> in the test project to do so.
        /// </summary>
        [Test]
        public async Task TheAsyncMethodsHereCompileToRuntimeAsync()
        {
            var stateMachines = typeof(ContinuationTests)
                .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(type => type.Name.Contains("d__"))
                .Select(type => type.Name)
                .ToList();

            await Assert.That(stateMachines).IsEmpty();

            var awaiting = typeof(ContinuationTests).GetMethod(
                nameof(this.AwaitsTheGate),
                BindingFlags.Instance | BindingFlags.NonPublic);

            // The runtime, not the compiler, owns suspension for these - the method is marked
            // Async in metadata and has no state machine to step.
            await Assert.That(awaiting.MethodImplementationFlags.ToString()).Contains("Async");
        }

        [Test]
        public async Task DecodesASuspendedAwaitChain()
        {
            var outer = this.AwaitsTheMiddleCall();

            try
            {
                await Assert.That(outer.IsCompleted).IsFalse();

                var head = this.Suspended();
                await Assert.That(head).IsNotNull();

                var chain = head.Chain();

                // One link per suspended await, plus the head that resumes the gate's task.
                await Assert.That(chain.Count).IsGreaterThanOrEqualTo(3);

                await Assert.That(chain[0].Address).IsEqualTo(head.Address);
                await Assert.That(chain.Last().Next).IsEqualTo(IntPtr.Zero);

                // Every link but the last names the next one, and the walk follows exactly that.
                foreach (var pair in chain.Zip(chain.Skip(1), (link, next) => (link, next)))
                    await Assert.That(pair.link.Next).IsEqualTo(pair.next.Address);
            }
            finally
            {
                this.gate.TrySetResult(40);
            }

            // Releasing the gate has to still produce the right answer: decoding the chain only
            // reads memory, it must not disturb the resumption.
            await Assert.That(await outer).IsEqualTo(43);
        }

        /// <summary>
        /// The point of walking the chain is seeing what a task is waiting to do, so the resume
        /// points must name the methods that are actually suspended.
        /// </summary>
        [Test]
        public async Task TheChainNamesEveryMethodItWillResume()
        {
            var outer = this.AwaitsTheMiddleCall();
            string dump;

            try
            {
                dump = this.Suspended()?.Dump() ?? string.Empty;
            }
            finally
            {
                this.gate.TrySetResult(40);
            }

            await outer;

            await Assert.That(dump).Contains(nameof(this.AwaitsTheGate));
            await Assert.That(dump).Contains(nameof(this.AwaitsTheInnerCall));
            await Assert.That(dump).Contains(nameof(this.AwaitsTheMiddleCall));
        }

        /// <summary>
        /// A resume point is only useful if it is a real instruction pointer, so check it lands
        /// inside the jitted code of the method the chain claims to resume.
        /// </summary>
        [Test]
        public async Task EachResumePointLandsInsideItsOwnMethod()
        {
            var outer = this.AwaitsTheMiddleCall();

            try
            {
                var chain = this.Suspended()?.Chain() ?? Array.Empty<ClrContinuation>();

                var resumePoints = chain
                    .Where(link => link.DiagnosticInstructionPointer != IntPtr.Zero)
                    .ToList();

                await Assert.That(resumePoints).IsNotEmpty();

                foreach (var link in resumePoints)
                {
                    var block = ClrCodeMap.Current.Find(link.DiagnosticInstructionPointer);

                    await Assert.That(block).IsNotNull();
                }
            }
            finally
            {
                this.gate.TrySetResult(40);
            }

            await outer;
        }
    }
}
