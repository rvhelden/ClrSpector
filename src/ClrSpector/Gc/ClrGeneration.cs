using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// One GC generation and the segments its objects live in.
    /// </summary>
    /// <remarks>
    /// There are five entries per heap on .NET 11, not three: gen0, gen1 and gen2 hold the small
    /// object heap, and the two beyond <c>MaxGeneration</c> are the large and pinned object heaps.
    /// The count comes from the descriptor's <c>TotalGenerationCount</c> rather than being assumed.
    ///
    /// Server GC has one such table per heap, so the generations of a server process are these
    /// five repeated once per core. <see cref="HeapIndex"/> says which heap a generation is from.
    /// </remarks>
    public sealed class ClrGeneration
    {
        private ClrGeneration()
        {
        }

        public IntPtr Address { get; private set; }

        /// <summary>The generation's index in the table.</summary>
        public int Number { get; private set; }

        /// <summary>Which heap this generation belongs to. Always 0 under workstation GC.</summary>
        public int HeapIndex { get; private set; }

        /// <summary>
        /// The <c>gc_heap</c> this generation belongs to, or <see cref="IntPtr.Zero"/> under
        /// workstation GC, which keeps its state in globals rather than a heap structure.
        /// </summary>
        public IntPtr HeapAddress { get; private set; }

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
        /// Reads every generation of every heap.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both GC flavours keep the same structures; they differ in where those structures live,
        /// so only the route to the generation table differs:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <b>Workstation</b> has one heap, and the descriptor's globals point straight at that
        /// heap's fields - <c>GCHeapGenerationTable</c> <i>is</i> the table.
        /// </description></item>
        /// <item><description>
        /// <b>Server</b> has one heap per core. It publishes <c>Heaps</c>, a <c>gc_heap**</c>
        /// variable, and <c>NumHeaps</c>, plus a <c>GCHeap</c> type giving the offsets of the
        /// generation table, ephemeral segment and allocation pointer <i>within</i> each heap.
        /// The per-heap globals workstation uses are not published there at all, which is why
        /// this cannot be one code path with a different starting address.
        /// </description></item>
        /// </list>
        /// <para>
        /// The result is flattened across heaps, so a walk covers the whole process either way.
        /// </para>
        /// </remarks>
        internal static List<ClrGeneration> ReadAll(ClrHeapLayouts layouts)
        {
            return GcContractDescriptor.IsServer
                ? ReadServerHeaps(layouts)
                : ReadWorkstationHeap(layouts);
        }

        private static List<ClrGeneration> ReadWorkstationHeap(ClrHeapLayouts layouts)
        {
            var gc = layouts.Gc;

            // A table, not a pointer: the array is at the symbol's own address.
            var table = gc.Globals.Address("GCHeapGenerationTable");

            // MaxGeneration is an int-sized runtime variable, so it is read at the symbol's
            // address rather than dereferenced as a pointer - dereferencing yields nonsense.
            var maxGeneration = new MemoryReader(gc.Globals.Address("MaxGeneration")).ReadInt(0);

            var ephemeralSegment = new MemoryReader(gc.Globals.Address("GCHeapEphemeralHeapSegment")).ReadIntPtr(0);
            var allocAllocated = new MemoryReader(gc.Globals.Address("GCHeapAllocAllocated")).ReadIntPtr(0);

            return ReadOneHeap(layouts, 0, IntPtr.Zero, table, ephemeralSegment, allocAllocated, maxGeneration);
        }

        private static List<ClrGeneration> ReadServerHeaps(ClrHeapLayouts layouts)
        {
            var gc = layouts.Gc;
            var heapLayout = gc.GetDataType("GCHeap");

            var heapCount = new MemoryReader(gc.Globals.Address("NumHeaps")).ReadInt(0);

            // Heaps names a gc_heap** variable, so the symbol holds the array's address and the
            // array holds one gc_heap* per heap.
            var heapArray = new MemoryReader(gc.Globals.Address("Heaps")).ReadIntPtr(0);

            if (heapCount <= 0 || heapArray == IntPtr.Zero)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"This process reports server GC, but its heap array is " +
                    $"0x{heapArray.ToInt64():x} with NumHeaps={heapCount}. Without both there is " +
                    "no way to reach the per-heap generation tables.");

            var maxGeneration = new MemoryReader(gc.Globals.Address("MaxGeneration")).ReadInt(0);

            var generationTableOffset = heapLayout["GenerationTable"];
            var ephemeralOffset = heapLayout["EphemeralHeapSegment"];
            var allocAllocatedOffset = heapLayout["AllocAllocated"];

            var generations = new List<ClrGeneration>(heapCount * layouts.TotalGenerationCount);

            for (var index = 0; index < heapCount; index++)
            {
                var heap = new MemoryReader(heapArray).ReadIntPtr(index * IntPtr.Size);

                if (heap == IntPtr.Zero)
                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"Heap {index} of {heapCount} is null in the array at " +
                        $"0x{heapArray.ToInt64():x}, so the heap list cannot be trusted.");

                var heapReader = new MemoryReader(heap);

                generations.AddRange(ReadOneHeap(
                    layouts,
                    index,
                    heap,

                    // The generation table is embedded in the heap, so the field's address is it.
                    heap + generationTableOffset,
                    heapReader.ReadIntPtr(ephemeralOffset),
                    heapReader.ReadIntPtr(allocAllocatedOffset),
                    maxGeneration));
            }

            return generations;
        }

        /// <summary>
        /// Decodes one heap's generation table. The flavours differ only in where the table and
        /// the two per-heap pointers were found, so the decode itself is shared.
        /// </summary>
        private static List<ClrGeneration> ReadOneHeap(
            ClrHeapLayouts layouts,
            int heapIndex,
            IntPtr heapAddress,
            IntPtr table,
            IntPtr ephemeralSegment,
            IntPtr allocAllocated,
            int maxGeneration)
        {
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
                generation.HeapIndex = heapIndex;
                generation.HeapAddress = heapAddress;
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

            var heap = this.HeapAddress == IntPtr.Zero ? string.Empty : $" heap{this.HeapIndex}";

            return $"gen{this.Number}{kind}{heap} segments={this.Segments.Count} live={this.LiveBytes}";
        }
    }
}