using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// The allocation buffers inside the walked range that hold no objects yet.
    /// </summary>
    /// <remarks>
    /// The GC hands each thread a zeroed buffer to allocate into and only advances the segment's
    /// recorded end when it collects. The span from a buffer's current pointer to its limit is
    /// therefore inside the range a walk covers but holds no constructed objects - it is reserved
    /// space. This is the single most likely way for a heap walk to look almost right: bumping
    /// into a buffer reads zeroes, and scanning forward past them lands in the middle of whatever
    /// gets allocated there next, where a field reads as a MethodTable.
    ///
    /// Skipping the buffers precisely is what avoids both failure modes - the walk neither stops
    /// at the first gap (which loses most of gen0) nor guesses its way past one. The buffers are
    /// found by walking the runtime's thread list, since each thread has its own; the global
    /// context is included too, though it is normally empty.
    ///
    /// These are read once per walk. A thread can be handed a new buffer afterwards, but only
    /// beyond the end already recorded, so it falls outside the range being walked.
    /// </remarks>
    internal sealed class AllocationHoles
    {
        /// <summary>A thread list this long is taken as corrupt rather than followed.</summary>
        private const int MaximumThreads = 100_000;

        private readonly List<(long Start, long End)> holes;

        private AllocationHoles(List<(long Start, long End)> holes)
        {
            this.holes = holes;
        }

        public int Count => this.holes.Count;

        public static AllocationHoles Read(ClrHeapLayouts layouts)
        {
            var holes = new List<(long Start, long End)>();
            var runtime = layouts.Runtime;

            if (!runtime.TryGetDataType("GCAllocContext", out var contextLayout))
                return new AllocationHoles(holes);

            var pointerOffset = contextLayout["Pointer"];
            var limitOffset = contextLayout["Limit"];

            // The unusable span runs a little past Limit: the allocator keeps a minimum
            // object's worth of headroom beyond it, so that a buffer it abandons can always be
            // filled with a free-object filler. Those bytes are zero too, and stopping the skip
            // at Limit exactly would land the walk back in them.
            var padding = layouts.MinimumObjectSize;

            AddGlobalContext(runtime, pointerOffset, limitOffset, padding, holes);
            AddThreadContexts(runtime, pointerOffset, limitOffset, padding, holes);

            return new AllocationHoles(holes);
        }

        private static void AddGlobalContext(
            ContractDescriptor runtime, int pointerOffset, int limitOffset, int padding, List<(long, long)> holes)
        {
            if (!runtime.Globals.Has("GlobalAllocContext"))
                return;

            // A table global: the context structure sits at the symbol's own address.
            Add(new MemoryReader(runtime.Globals.Address("GlobalAllocContext")),
                pointerOffset, limitOffset, padding, holes);
        }

        /// <summary>
        /// Walks the runtime's thread list, collecting each thread's allocation buffer.
        /// </summary>
        /// <remarks>
        /// The list head at <c>ThreadStore.FirstThreadLink</c> is a Thread pointer, and each
        /// thread's successor is at its own <c>LinkNext</c> field - verified against the live
        /// runtime by checking that the walk reaches the calling thread and that its
        /// <c>OSId</c> matches the operating system's id for it.
        ///
        /// A thread that has not run managed code yet has no thread locals, and one being torn
        /// down may have unmapped them, so every hop is checked for readability before it is
        /// followed. Failing to read a thread costs at most a missed buffer, so it is skipped
        /// rather than raised - unlike a misread object, which would corrupt the whole walk.
        /// </remarks>
        private static void AddThreadContexts(
            ContractDescriptor runtime,
            int pointerOffset,
            int limitOffset,
            int padding,
            List<(long, long)> holes)
        {
            if (!runtime.Globals.Has("ThreadStore")
                || !runtime.TryGetDataType("ThreadStore", out var storeLayout)
                || !runtime.TryGetDataType("Thread", out var threadLayout)
                || !runtime.TryGetDataType("RuntimeThreadLocals", out var localsLayout)
                || !runtime.TryGetDataType("EEAllocContext", out var eeContextLayout))
            {
                return;
            }

            var store = runtime.Globals.Dereference("ThreadStore");
            if (!ProcessMemoryRegions.IsReadable(store, storeLayout["ThreadCount"] + sizeof(int)))
                return;

            var localsOffset = threadLayout["RuntimeThreadLocals"];
            var linkNextOffset = threadLayout["LinkNext"];

            // The descriptor publishes no size for Thread, so the probe covers exactly the two
            // fields read here. Using the absent size would make every probe zero-length, which
            // reads as unreadable and would silently collect no buffers at all.
            var threadProbeSize = Math.Max(localsOffset, linkNextOffset) + IntPtr.Size;
            var contextOffset = localsLayout["AllocContext"] + eeContextLayout["GCAllocationContext"];

            var thread = new MemoryReader(store).ReadIntPtr(storeLayout["FirstThreadLink"]);
            var seen = 0;

            while (thread != IntPtr.Zero && seen++ < MaximumThreads)
            {
                if (!ProcessMemoryRegions.IsReadable(thread, threadProbeSize))
                    break;

                var reader = new MemoryReader(thread);
                var locals = reader.ReadIntPtr(localsOffset);

                if (locals != IntPtr.Zero
                    && ProcessMemoryRegions.IsReadable(locals, contextOffset + limitOffset + IntPtr.Size))
                {
                    Add(new MemoryReader(locals).Offset(contextOffset),
                        pointerOffset, limitOffset, padding, holes);
                }

                var next = reader.ReadIntPtr(linkNextOffset);
                if (next == thread)
                    break;

                thread = next;
            }
        }

        private static void Add(
            MemoryReader context, int pointerOffset, int limitOffset, int padding, List<(long, long)> holes)
        {
            var pointer = context.ReadIntPtr(pointerOffset).ToInt64();
            var limit = context.ReadIntPtr(limitOffset).ToInt64();

            if (pointer != 0 && limit > pointer)
                holes.Add((pointer, limit + padding));
        }

        /// <summary>
        /// True when <paramref name="address"/> falls in a buffer, with the address to resume at.
        /// </summary>
        public bool TryGetHoleEnd(long address, out long resume)
        {
            foreach (var hole in this.holes)
            {
                if (address >= hole.Start && address < hole.End)
                {
                    resume = hole.End;
                    return true;
                }
            }

            resume = 0;
            return false;
        }
    }
}