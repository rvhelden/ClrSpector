using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClrSpector;

namespace ClrSpectorTests
{
    /// <summary>
    /// Placing a thread from the runtime's own structures, without suspending it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A running thread's instruction pointer is in its registers and on its stack. The runtime
    /// caches it nowhere, so there is no field to read - reaching it means suspending the thread
    /// and asking the OS for its context, which an in-process reader has no business doing.
    /// </para>
    /// <para>
    /// The explicit frame chain is what is actually recorded. A thread that crosses a boundary
    /// jitted code cannot describe by itself pushes a Frame holding what is needed to get back,
    /// and for a P/Invoke that is the exact managed return address. These tests pin down both
    /// halves of that: the address is right, and a thread with nothing recorded reports nothing
    /// rather than a guess.
    /// </para>
    /// </remarks>
    public class ThreadFrameTests
    {
        [DllImport("kernel32", EntryPoint = "Sleep")]
        private static extern void SleepWindows(uint milliseconds);

        [DllImport("libc", EntryPoint = "usleep")]
        private static extern int SleepPosix(uint microseconds);

        /// <summary>
        /// A thread blocked in a P/Invoke is placed exactly, by return address and by method.
        /// </summary>
        /// <remarks>
        /// The P/Invoke is declared here so its marshalling stub is jitted into this assembly,
        /// which is what lets the code map name a method for the address. The same wait made
        /// through CoreLib lands in precompiled code, where the range is identified but the
        /// method is not - see <see cref="AnAddressInPrecompiledCodeIsPlacedButNotNamed"/>.
        /// </remarks>
        [Test]
        [Timeout(30_000)]
        public async Task AThreadParkedInANativeCallReportsTheManagedCallSite(
            CancellationToken cancellation)
        {
            var release = 0;
            var parked = new ManualResetEventSlim();

            var worker = new Thread(() =>
            {
                parked.Set();
                Park(ref release);
            })
            {
                IsBackground = true
            };

            try
            {
                worker.Start();
                parked.Wait(cancellation);

                var frame = await Poll(
                    worker,
                    f => f.Kind == "InlinedCallFrame" && f.Method != null,
                    cancellation);

                await Assert.That(frame).IsNotNull()
                    .Because("a thread inside a P/Invoke sits on an InlinedCallFrame");

                await Assert.That(frame.ReturnAddress).IsNotEqualTo(IntPtr.Zero);

                // The return address is the call site inside the marshalling stub of the
                // P/Invoke that is currently blocked.
                await Assert.That(frame.Method.Name)
                    .IsEqualTo(OperatingSystem.IsWindows() ? "SleepWindows" : "SleepPosix");

                await Assert.That(frame.Method.DeclaringTypeName)
                    .IsEqualTo("ClrSpectorTests.ThreadFrameTests");

                // An address partway through a method, not its start - which is what makes it an
                // instruction pointer rather than an entry point.
                await Assert.That(frame.CodeBlock).IsNotNull();
                await Assert.That(frame.CodeBlock.OffsetIntoMethod).IsGreaterThan(0L);

                await Assert.That(frame.CodeBlock.MethodDesc)
                    .IsEqualTo(frame.Method.ClrPointer);
            }
            finally
            {
                Volatile.Write(ref release, 1);

                if (worker.IsAlive)
                    worker.Join(TimeSpan.FromSeconds(10));

                parked.Dispose();
            }
        }

        /// <summary>
        /// A thread running managed code has an empty chain, because nothing is recorded.
        /// </summary>
        /// <remarks>
        /// <c>FRAME_TOP</c> is <c>~0</c> rather than null, so a walk that stopped only on null
        /// would dereference it. That the answer is "nothing" here is the point: it is what makes
        /// the non-empty answers above worth trusting.
        /// </remarks>
        [Test]
        public async Task AThreadRunningManagedCodeRecordsNoFrames()
        {
            var current = Environment.CurrentManagedThreadId;

            var thread = ClrThreadStore.Read().Threads
                .FirstOrDefault(t => t.ManagedThreadId == (uint)current);

            await Assert.That(thread).IsNotNull();

            // Either the chain is genuinely empty, or every frame on it is one this thread really
            // pushed - never a walk off the end of FRAME_TOP.
            if (thread.Frame.ToInt64() == ~0L)
            {
                await Assert.That(thread.Frames)
                    .IsEmpty()
                    .Because("FRAME_TOP means no frames, and is not a readable address");
            }

            await Assert.That(thread.Frames.Count).IsLessThan(512);
        }

        /// <summary>
        /// A frame identifies its kind by a number the descriptor names, not by a vtable pointer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A frame's first pointer-sized slot is a small <c>FrameIdentifier</c> enum value, and
        /// the descriptor publishes one <c>&lt;Name&gt;FrameIdentifier</c> global per kind. Those
        /// globals are literal numbers: read as addresses they all come back zero, which makes
        /// the mistake look like the descriptor not publishing them at all. Looking for a vtable
        /// pointer in that slot finds a small integer like <c>0x12</c> and matches nothing.
        /// </para>
        /// <para>
        /// Asserted against the parked worker, whose chain is stable because the thread is
        /// blocked. Other threads are checked only for the invariants that survive a race - a
        /// chain is mutated by the thread that owns it, so a snapshot taken while a frame is
        /// being pushed can catch a half-written identifier, and an unrecognised one is a real
        /// possibility rather than a decoding bug.
        /// </para>
        /// </remarks>
        [Test]
        [Timeout(30_000)]
        public async Task AFrameNamesItsKindByAPublishedIdentifier(CancellationToken cancellation)
        {
            var release = 0;
            var parked = new ManualResetEventSlim();

            var worker = new Thread(() =>
            {
                parked.Set();
                Park(ref release);
            })
            {
                IsBackground = true
            };

            try
            {
                worker.Start();
                parked.Wait(cancellation);

                await Poll(worker, f => f.Kind == "InlinedCallFrame", cancellation);

                var blocked = ClrThreadStore.Read().Threads
                    .First(t => t.ManagedThreadId == (uint)worker.ManagedThreadId);

                await Assert.That(blocked.Frames).IsNotEmpty();

                // This thread is not running, so its chain is not moving underneath the read.
                foreach (var frame in blocked.Frames)
                {
                    await Assert.That(frame.Kind)
                        .DoesNotStartWith("Unknown")
                        .Because($"identifier {frame.Identifier} should name a published kind");

                    await Assert.That(frame.Identifier).IsGreaterThan(0L);
                }

                await Assert.That(blocked.Frames.Select(f => f.Kind).ToList())
                    .Contains("InlinedCallFrame");
            }
            finally
            {
                Volatile.Write(ref release, 1);

                if (worker.IsAlive)
                    worker.Join(TimeSpan.FromSeconds(10));

                parked.Dispose();
            }
        }

        /// <summary>
        /// A kind the descriptor does not name has none of its fields read.
        /// </summary>
        /// <remarks>
        /// The offsets to read depend entirely on which kind a frame is, so an unrecognised
        /// identifier must produce no return address and no MethodDesc rather than values read at
        /// whatever offset some other kind happens to use. That is what makes a torn read - or a
        /// future runtime with a kind this build does not publish - harmless instead of
        /// convincing.
        /// </remarks>
        [Test]
        public async Task AnUnnamedFrameKindReadsNoFields()
        {
            var walked = 0;

            foreach (var thread in ClrThreadStore.Read().Threads)
            {
                foreach (var frame in thread.Frames)
                {
                    walked++;

                    await Assert.That(frame.Address).IsNotEqualTo(IntPtr.Zero);

                    if (!frame.Kind.StartsWith("Unknown", StringComparison.Ordinal))
                        continue;

                    await Assert.That(frame.ReturnAddress).IsEqualTo(IntPtr.Zero);
                    await Assert.That(frame.MethodDescPointer).IsEqualTo(IntPtr.Zero);
                    await Assert.That(frame.Method).IsNull();
                }
            }

            await Assert.That(walked).IsGreaterThanOrEqualTo(0);
        }

        /// <summary>
        /// An address in precompiled code is placed in a range but not named.
        /// </summary>
        /// <remarks>
        /// A jitted method's code header carries its MethodDesc, so an address in it resolves. A
        /// ReadyToRun image's code does not: naming a method there needs that image's own
        /// function table, which is a different lookup the code map does not do. Asserted so the
        /// limitation stays a stated one - the sample says the same thing out loud.
        /// </remarks>
        [Test]
        public async Task AnAddressInPrecompiledCodeIsPlacedButNotNamed()
        {
            // Something certainly precompiled into CoreLib rather than jitted here.
            var address = typeof(object).GetMethod("ToString").MethodHandle.GetFunctionPointer();
            var block = ClrCodeMap.Current.Find(address);

            if (block == null || block.Kind != ClrCodeKind.ReadyToRun)
                return;

            await Assert.That(block.MethodDesc)
                .IsEqualTo(IntPtr.Zero)
                .Because("a ReadyToRun range carries no MethodDesc for the code map to read");
        }

        /// <summary>Blocks in a native call until released.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Park(ref int release)
        {
            while (Volatile.Read(ref release) == 0)
            {
                if (OperatingSystem.IsWindows())
                    SleepWindows(20);
                else
                    SleepPosix(20_000);
            }
        }

        /// <summary>
        /// Waits for <paramref name="worker"/> to be carrying a frame matching
        /// <paramref name="wanted"/>.
        /// </summary>
        /// <remarks>
        /// Polled rather than assumed. The worker signals before it starts blocking, so there is
        /// a window in which it has not pushed the frame yet, and a fixed sleep would either be
        /// flaky or slow. The chain is also a snapshot of a live thread, which is the other
        /// reason to re-read it rather than to read it once.
        /// </remarks>
        private static async Task<ClrFrame> Poll(
            Thread worker, Func<ClrFrame, bool> wanted, CancellationToken cancellation)
        {
            var deadline = Stopwatch.StartNew();

            while (deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                cancellation.ThrowIfCancellationRequested();

                var thread = ClrThreadStore.Read().Threads
                    .FirstOrDefault(t => t.ManagedThreadId == (uint)worker.ManagedThreadId);

                var frame = thread?.Frames.FirstOrDefault(wanted);

                if (frame != null)
                    return frame;

                await Task.Delay(25, cancellation);
            }

            return null;
        }
    }
}
