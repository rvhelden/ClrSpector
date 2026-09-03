using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// One GC generation and the segments its objects live in.
    /// </summary>
    /// <remarks>
    /// There are five entries on .NET 11, not three: gen0, gen1 and gen2 hold the small object
    /// heap, and the two beyond <c>MaxGeneration</c> are the large and pinned object heaps. The
    /// count comes from the descriptor's <c>TotalGenerationCount</c> rather than being assumed.
    /// </remarks>
    public sealed class ClrGeneration
    {
        private ClrGeneration()
        {
        }

        public IntPtr Address { get; private set; }

        /// <summary>The generation's index in the table.</summary>
        public int Number { get; private set; }

        /// <summary>The head of this generation's segment chain.</summary>
        public IntPtr StartSegment { get; private set; }

        public IReadOnlyList<ClrHeapSegment> Segments { get; private set; }

        /// <summary>True for the large object heap generation.</summary>
        public bool IsLargeObjectHeap { get; private set; }

        /// <summary>True for the pinned object heap generation.</summary>
        public bool IsPinnedObjectHeap { get; private set; }

        public long LiveBytes
        {
            get
            {
                long total = 0;
                foreach (var segment in this.Segments)
                    total += segment.LiveBytes;

                return total;
            }
        }

        /// <summary>
        /// Reads the whole generation table.
        /// </summary>
        /// <remarks>
        /// Workstation GC keeps one heap, and the descriptor's globals point straight at that
        /// heap's fields, so the generation table is the array at
        /// <c>GCHeapGenerationTable</c>'s own address. Server GC has one heap per core and
        /// publishes <c>Heaps</c>/<c>NumHeaps</c> plus a <c>GCHeap</c> type giving the offsets
        /// within each heap; that branch is not implemented yet and fails loudly.
        /// </remarks>
        internal static List<ClrGeneration> ReadAll(ClrHeapLayouts layouts)
        {
            var gc = layouts.Gc;

            if (gc.Globals.Has("Heaps") || gc.Globals.Has("NumHeaps"))
                throw new ClrSpectorUnsupportedRuntimeException(
                    "This process is running server GC, whose heaps are read through the " +
                    "Heaps/NumHeaps globals and the GCHeap type. Only workstation GC is " +
                    "implemented so far; run with DOTNET_gcServer=0 to inspect the heap.");

            // A table, not a pointer: the array is at the symbol's own address.
            var table = gc.Globals.Address("GCHeapGenerationTable");

            // MaxGeneration is an int-sized runtime variable, so it is read at the symbol's
            // address rather than dereferenced as a pointer - dereferencing yields nonsense.
            var maxGeneration = new MemoryReader(gc.Globals.Address("MaxGeneration")).ReadInt(0);

            var ephemeralSegment = new MemoryReader(gc.Globals.Address("GCHeapEphemeralHeapSegment")).ReadIntPtr(0);
            var allocAllocated = new MemoryReader(gc.Globals.Address("GCHeapAllocAllocated")).ReadIntPtr(0);

            var layout = layouts.Generation;
            var stride = (int)layout.RequiredSize;
            var startSegmentOffset = layout["StartSegment"];

            var generations = new List<ClrGeneration>(layouts.TotalGenerationCount);
            for (var number = 0; number < layouts.TotalGenerationCount; number++)
            {
                var reader = new MemoryReader(table).Offset(number * stride);

                var generation = new ClrGeneration();
                generation.Address = reader.Address;
                generation.Number = number;
                generation.StartSegment = reader.ReadIntPtr(startSegmentOffset);
                generation.IsLargeObjectHeap = number == maxGeneration + 1;
                generation.IsPinnedObjectHeap = number == maxGeneration + 2;
                generation.Segments = ClrHeapSegment.ReadChain(
                    generation.StartSegment, number, ephemeralSegment, allocAllocated, layouts);

                generations.Add(generation);
            }

            return generations;
        }

        public override string ToString()
        {
            var kind = this.IsLargeObjectHeap ? " (LOH)"
                : this.IsPinnedObjectHeap ? " (POH)"
                : string.Empty;

            return $"gen{this.Number}{kind} segments={this.Segments.Count} live={this.LiveBytes}";
        }
    }
}
