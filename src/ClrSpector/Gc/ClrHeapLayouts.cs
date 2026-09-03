using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// The offsets, globals and rules a heap walk needs, resolved once instead of per object.
    /// </summary>
    /// <remarks>
    /// Every decoder in this project looks its layout up in <see cref="ContractDescriptor"/> at
    /// the top of its Create method, which is fine when a call decodes one structure. A heap walk
    /// decodes millions, so the dictionary lookups are hoisted here and the walk reads plain
    /// fields. The offsets still come from the descriptor - nothing is hardcoded.
    /// </remarks>
    public sealed class ClrHeapLayouts
    {
        /// <summary>
        /// The smallest object the GC will place, in pointers. Three covers the MethodTable
        /// pointer plus the two words a free-list entry needs, which is why a nominally smaller
        /// object is still padded to this.
        /// </summary>
        private const int MinimumObjectPointers = 3;

        /// <summary>Page granularity for the readability cache: 4 KiB everywhere this runs.</summary>
        private const int PageShift = 12;

        private const int PageSize = 1 << PageShift;

        private readonly HashSet<long> readablePages = new HashSet<long>();
        private readonly HashSet<long> unreadablePages = new HashSet<long>();
        private readonly HashSet<long> readableMethodTables = new HashSet<long>();
        private readonly HashSet<long> unreadableMethodTables = new HashSet<long>();
        private readonly long methodTableProbeSize;

        public ClrHeapLayouts(ContractDescriptor runtime, ContractDescriptor gc)
        {
            this.Runtime = runtime;
            this.Gc = gc;

            var objectLayout = runtime.GetDataType("Object");
            var arrayLayout = runtime.GetDataType("Array");

            this.MethodTablePointerOffset = objectLayout["m_pMethTab"];
            this.ComponentCountOffset = arrayLayout["m_NumComponents"];

            // A byte-sized mask published as a literal; the GC stores mark and pin state there.
            this.MethodTableUnmask = (long)runtime.Globals.Number("ObjectToMethodTableUnmask");

            this.FreeObjectMethodTable = runtime.Globals.Dereference("FreeObjectMethodTable");

            // The GC only ever hands out addresses inside this pair, so they bound every
            // plausible object address and every plausible segment.
            this.LowestAddress = runtime.Globals.Dereference("GCLowestAddress").ToInt64();
            this.HighestAddress = runtime.Globals.Dereference("GCHighestAddress").ToInt64();

            // A walk sizes millions of objects, and sizing needs only these two MethodTable
            // fields - not the full decode, which also resolves the EEClass and so is both far
            // more work and not always possible for every MethodTable on the heap.
            var methodTableLayout = runtime.GetDataType("MethodTable");
            this.MethodTableFlagsOffset = methodTableLayout["MTFlags"];
            this.MethodTableBaseSizeOffset = methodTableLayout["BaseSize"];

            // Probing the whole structure, not just the two fields read, so a MethodTable that
            // straddles the end of a mapping is rejected rather than half-read.
            this.methodTableProbeSize = methodTableLayout.RequiredSize;

            this.Generation = gc.GetDataType("Generation");
            this.Segment = gc.GetDataType("HeapSegment");
            this.TotalGenerationCount = (int)gc.Globals.Number("TotalGenerationCount");

            this.ObjectAlignment = IntPtr.Size;
            this.MinimumObjectSize = MinimumObjectPointers * IntPtr.Size;
        }

        public ContractDescriptor Runtime { get; }

        public ContractDescriptor Gc { get; }

        public int MethodTablePointerOffset { get; }

        public int ComponentCountOffset { get; }

        public int MethodTableFlagsOffset { get; }

        public int MethodTableBaseSizeOffset { get; }

        public long MethodTableUnmask { get; }

        public IntPtr FreeObjectMethodTable { get; }

        public long LowestAddress { get; }

        public long HighestAddress { get; }

        public DataType Generation { get; }

        public DataType Segment { get; }

        /// <summary>How many generations the generation table holds - 5 on .NET 11 (SOH, LOH, POH).</summary>
        public int TotalGenerationCount { get; }

        public int ObjectAlignment { get; }

        public int MinimumObjectSize { get; }

        /// <summary>
        /// Rounds a computed object size up the way the allocator does.
        /// </summary>
        /// <remarks>
        /// The descriptor publishes no global for either rule - .NET 11 removed
        /// <c>ObjectHeaderSize</c>, and <c>ArrayBaseSize</c> is an array's base, not a minimum -
        /// so both are derived from the pointer size and then checked against reality: the tests
        /// compare the size computed here against independently known object sizes, and the walk
        /// itself would desynchronise within a few objects if either rule were wrong.
        /// </remarks>
        public long AlignObjectSize(long size)
        {
            var alignment = this.ObjectAlignment;
            var aligned = (size + alignment - 1) & ~((long)alignment - 1);
            return aligned < this.MinimumObjectSize ? this.MinimumObjectSize : aligned;
        }

        /// <summary>True when <paramref name="address"/> is inside the GC's address range.</summary>
        public bool IsPlausibleHeapAddress(long address)
        {
            return address >= this.LowestAddress && address < this.HighestAddress;
        }

        /// <summary>
        /// True when an object header can safely be read at <paramref name="address"/>.
        /// </summary>
        /// <remarks>
        /// The range a segment reports is not a guarantee that the memory is still mapped. A
        /// collection can hand regions back and decommit them while a walk is in progress, and
        /// reading a decommitted page is an access violation - unrecoverable, and fatal to the
        /// process rather than to the walk. So the page is checked before the read.
        ///
        /// The answers are memoised per page, so this costs one system call per four kilobytes
        /// of heap walked rather than one per object.
        /// </remarks>
        public bool IsReadableObjectHeader(long address)
        {
            // Only the first few bytes are read here - the MethodTable pointer, and the element
            // count for an array - so the probe covers a minimum object's worth.
            if (!this.IsReadablePage(address))
                return false;

            var last = address + this.MinimumObjectSize - 1;
            return (address >> PageShift) == (last >> PageShift) || this.IsReadablePage(last);
        }

        private bool IsReadablePage(long address)
        {
            var page = address >> PageShift;

            if (this.readablePages.Contains(page))
                return true;

            if (this.unreadablePages.Contains(page))
                return false;

            var readable = ProcessMemoryRegions.IsReadable(new IntPtr(page << PageShift), PageSize);
            if (readable)
                this.readablePages.Add(page);
            else
                this.unreadablePages.Add(page);

            return readable;
        }

        /// <summary>
        /// True when <paramref name="methodTable"/> can safely be read as a MethodTable.
        /// </summary>
        /// <remarks>
        /// A MethodTable lives in a loader heap, not on the GC heap, so it cannot be checked
        /// against the GC's address range - the only bound available. What can be checked is that
        /// it is a pointer-aligned address whose memory is actually mapped, which is what makes
        /// following it safe: an access violation here would take the process down rather than
        /// raise an exception.
        ///
        /// The answers are memoised per address. A process has orders of magnitude fewer live
        /// MethodTables than objects, so after the first few hundred objects this is a set
        /// lookup, and the system call it guards is never made per object.
        /// </remarks>
        public bool IsReadableMethodTable(IntPtr methodTable)
        {
            var address = methodTable.ToInt64();

            if (address == 0 || (address & (this.ObjectAlignment - 1)) != 0)
                return false;

            if (this.readableMethodTables.Contains(address))
                return true;

            if (this.unreadableMethodTables.Contains(address))
                return false;

            var readable = ProcessMemoryRegions.IsReadable(methodTable, this.methodTableProbeSize);
            if (readable)
                this.readableMethodTables.Add(address);
            else
                this.unreadableMethodTables.Add(address);

            return readable;
        }
    }
}