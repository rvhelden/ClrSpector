using System;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// Answers "which method is the code at this address part of?" - the reverse of every other
    /// lookup in this library.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything else here starts from a <see cref="System.Type"/> or a
    /// <see cref="System.Reflection.MethodBase"/> and finds the runtime's structures. This starts
    /// from a bare code address and finds the method, which is what turns a raw pointer -
    /// a dispatch slot's contents, a return address, a thunk's entry point - into something
    /// nameable.
    /// </para>
    /// <para>
    /// Two data structures do the work:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// A <b>range section map</b> partitions the address space across five levels of 256-entry
    /// tables on 64-bit (two levels on 32-bit), indexed by successive bytes of the address. The
    /// leaf holds a linked list of fragments, one of which covers the address.
    /// </description></item>
    /// <item><description>
    /// A <b>nibble map</b> then locates the start of the individual method within the code heap.
    /// Each 32-byte bucket of code gets one 4-bit nibble recording where in that bucket a method
    /// begins, so finding the enclosing method means reading the nibble for the address and, if
    /// it is empty or points past the address, searching backwards. A code block that fills a
    /// whole map unit is instead stored as a relative pointer, which is what the values above 8
    /// encode.
    /// </description></item>
    /// </list>
    /// <para>
    /// Once the method's code start is known, the code header sits one pointer behind it and
    /// holds the MethodDesc.
    /// </para>
    /// <para>
    /// Only jitted code is resolved. Ready-to-run and interpreter ranges are reported as such
    /// rather than guessed at, and an address in neither is simply not code.
    /// </para>
    /// </remarks>
    public sealed unsafe class ClrCodeMap
    {
        /// <summary>Range section flags, from the runtime's RangeSectionFlags.</summary>
        private const int CodeHeapFlag = 0x02;

        private const int RangeListFlag = 0x04;
        private const int InterpreterFlag = 0x08;

        /// <summary>Every level of the map is a 256-entry table indexed by one byte.</summary>
        private const int BitsPerLevel = 8;

        private const int EntriesPerLevel = 256;

        /// <summary>
        /// x64 addresses 57 bits and arm64 52, so the runtime ignores the top bits and maps the
        /// rest across five levels. A 32-bit target uses two.
        /// </summary>
        private const int MapLevels64 = 5;

        private const int MaxSetBit64 = 56;
        private const int MapLevels32 = 2;
        private const int MaxSetBit32 = 31;

        /// <summary>One nibble covers this many bytes of code.</summary>
        private const ulong BytesPerBucket = 8 * MapUnitSizeInBytes;

        private const int MapUnitSizeInBytes = sizeof(uint);
        private const ulong NibblesPerMapUnit = 2 * MapUnitSizeInBytes;
        private const uint NibbleMask = 0x0F;

        private static readonly Lazy<ClrCodeMap> current =
            new Lazy<ClrCodeMap>(Load, isThreadSafe: true);

        private readonly int mapLevels;
        private readonly int maxSetBit;
        private readonly byte stubCodeBlockLast;
        private readonly DataType rangeSectionMapLayout;
        private readonly DataType rangeSectionFragmentLayout;
        private readonly DataType rangeSectionLayout;
        private readonly DataType codeHeapListNodeLayout;
        private readonly DataType realCodeHeaderLayout;

        private ClrCodeMap(IntPtr topMap, ContractDescriptor descriptor)
        {
            this.Address = topMap;

            this.mapLevels = IntPtr.Size == 8 ? MapLevels64 : MapLevels32;
            this.maxSetBit = IntPtr.Size == 8 ? MaxSetBit64 : MaxSetBit32;

            this.rangeSectionMapLayout = descriptor.GetDataType("RangeSectionMap");
            this.rangeSectionFragmentLayout = descriptor.GetDataType("RangeSectionFragment");
            this.rangeSectionLayout = descriptor.GetDataType("RangeSection");
            this.codeHeapListNodeLayout = descriptor.GetDataType("CodeHeapListNode");
            this.realCodeHeaderLayout = descriptor.GetDataType("RealCodeHeader");

            // A code header pointer at or below this value is not a pointer at all - it is a
            // small integer naming a kind of stub.
            this.stubCodeBlockLast = descriptor.Globals.Has("StubCodeBlockLast")
                ? (byte)descriptor.Globals.Number("StubCodeBlockLast")
                : (byte)0;
        }

        /// <summary>The code map of the running process.</summary>
        public static ClrCodeMap Current => current.Value;

        /// <summary>The address of the runtime's top-level range section map.</summary>
        public IntPtr Address { get; }

        /// <summary>
        /// What the code at <paramref name="codeAddress"/> belongs to, or null when the address
        /// is not in any code range this runtime manages.
        /// </summary>
        /// <remarks>
        /// An address anywhere inside a method resolves to that method, not only its entry point,
        /// which is what makes this useful on a return address.
        /// </remarks>
        public ClrCodeBlock Find(IntPtr codeAddress)
        {
            if (codeAddress == IntPtr.Zero)
                return null;

            var section = this.FindRangeSection(codeAddress);
            if (section == IntPtr.Zero)
                return null;

            var sectionReader = new MemoryReader(section);
            var flags = sectionReader.ReadInt(this.rangeSectionLayout["Flags"]);

            // A range list covers the runtime's stub ranges - precodes and dispatch stubs -
            // rather than method bodies. Naming it beats returning nothing, since "what does
            // this slot point at" is usually asked of exactly such an address.
            if ((flags & RangeListFlag) != 0)
                return ClrCodeBlock.OtherKind(codeAddress, section, ClrCodeKind.Stub);

            var isInterpreter = (flags & InterpreterFlag) != 0;
            var r2rModule = this.rangeSectionLayout.HasField("R2RModule")
                ? sectionReader.ReadIntPtr(this.rangeSectionLayout["R2RModule"])
                : IntPtr.Zero;

            // Only the jit's own code heaps are decoded; the others are reported as what they are.
            if (isInterpreter)
                return ClrCodeBlock.OtherKind(codeAddress, section, ClrCodeKind.Interpreted);

            if (r2rModule != IntPtr.Zero)
                return ClrCodeBlock.OtherKind(codeAddress, section, ClrCodeKind.ReadyToRun);

            if ((flags & CodeHeapFlag) == 0)
                return ClrCodeBlock.OtherKind(codeAddress, section, ClrCodeKind.Unknown);

            var codeStart = this.FindMethodStart(sectionReader, codeAddress);
            if (codeStart == IntPtr.Zero)
                return null;

            // The code header sits one pointer behind the method's code.
            var headerIndirect = new MemoryReader(codeStart - IntPtr.Size).ReadIntPtr(0);

            if ((ulong)headerIndirect.ToInt64() <= this.stubCodeBlockLast)
                return ClrCodeBlock.Stub(codeAddress, section, codeStart, (int)headerIndirect.ToInt64());

            var methodDesc = new MemoryReader(headerIndirect).ReadIntPtr(this.realCodeHeaderLayout["MethodDesc"]);

            return ClrCodeBlock.Jitted(codeAddress, section, codeStart, headerIndirect, methodDesc);
        }

        /// <summary>
        /// The method containing <paramref name="codeAddress"/>, resolved back to reflection, or
        /// null when the address is not in jitted managed code.
        /// </summary>
        /// <remarks>
        /// The MethodDesc a code header names is the runtime's own handle for the method, which
        /// <see cref="System.Reflection.MethodBase.GetMethodFromHandle(RuntimeMethodHandle)"/>
        /// can turn back into a <see cref="System.Reflection.MethodBase"/>.
        /// </remarks>
        public System.Reflection.MethodBase FindMethod(IntPtr codeAddress)
        {
            return this.Find(codeAddress)?.ResolveMethod();
        }

        private static ClrCodeMap Load()
        {
            var descriptor = ContractDescriptor.Current;

            // The global names the map's own address, so the pointer-data value is the map -
            // dereferencing it would read the map's first field instead.
            var topMap = descriptor.Globals.Address("ExecutionManagerCodeRangeMapAddress");

            return new ClrCodeMap(topMap, descriptor);
        }

        /// <summary>
        /// Walks the levels of the range section map, then the fragment list at the leaf, to find
        /// the section covering this address.
        /// </summary>
        private IntPtr FindRangeSection(IntPtr codeAddress)
        {
            // TopLevelData is an inline array, so the top table *is* the field - its address,
            // not its contents. (The runtime's own reader marks it [FieldAddress].)
            var levelMap = this.Address + this.rangeSectionMapLayout["TopLevelData"];

            // Descend from the top level to the leaf, one byte of the address per level.
            for (var level = this.mapLevels; level > 1; level--)
            {
                if (levelMap == IntPtr.Zero)
                    return IntPtr.Zero;

                var slot = levelMap + this.IndexForLevel(codeAddress, level) * IntPtr.Size;

                // Interior entries steal the low bit for a collectible flag.
                levelMap = new IntPtr(new MemoryReader(slot).ReadIntPtr(0).ToInt64() & ~1L);
            }

            if (levelMap == IntPtr.Zero)
                return IntPtr.Zero;

            var leafSlot = levelMap + this.IndexForLevel(codeAddress, 1) * IntPtr.Size;
            var fragment = new IntPtr(new MemoryReader(leafSlot).ReadIntPtr(0).ToInt64() & ~1L);

            // One leaf covers a large span, so it can hold several fragments; find the one that
            // actually contains the address.
            var beginOffset = this.rangeSectionFragmentLayout["RangeBegin"];
            var endOffset = this.rangeSectionFragmentLayout["RangeEndOpen"];
            var nextOffset = this.rangeSectionFragmentLayout["Next"];
            var sectionOffset = this.rangeSectionFragmentLayout["RangeSection"];

            var address = (ulong)codeAddress.ToInt64();

            while (fragment != IntPtr.Zero)
            {
                var reader = new MemoryReader(fragment);
                var begin = (ulong)reader.ReadIntPtr(beginOffset).ToInt64();
                var end = (ulong)reader.ReadIntPtr(endOffset).ToInt64();

                if (address >= begin && address < end)
                {
                    var section = reader.ReadIntPtr(sectionOffset);

                    // A section queued for deletion no longer describes live code.
                    var pendingDelete = new MemoryReader(section)
                        .ReadIntPtr(this.rangeSectionLayout["NextForDelete"]);

                    return pendingDelete == IntPtr.Zero ? section : IntPtr.Zero;
                }

                fragment = reader.ReadIntPtr(nextOffset);
            }

            return IntPtr.Zero;
        }

        /// <summary>The index into one level of the map for this address.</summary>
        private int IndexForLevel(IntPtr codeAddress, int level)
        {
            var address = (ulong)codeAddress.ToInt64();

            var used = address >> (this.maxSetBit + 1 - this.mapLevels * BitsPerLevel);
            var shifted = used >> ((level - 1) * BitsPerLevel);

            return (int)(shifted & (EntriesPerLevel - 1));
        }

        /// <summary>
        /// The start of the method containing this address, via the code heap's nibble map.
        /// </summary>
        private IntPtr FindMethodStart(MemoryReader sectionReader, IntPtr codeAddress)
        {
            var heapList = sectionReader.ReadIntPtr(this.rangeSectionLayout["HeapList"]);
            if (heapList == IntPtr.Zero)
                return IntPtr.Zero;

            var heap = new MemoryReader(heapList);
            var start = (ulong)heap.ReadIntPtr(this.codeHeapListNodeLayout["StartAddress"]).ToInt64();
            var end = (ulong)heap.ReadIntPtr(this.codeHeapListNodeLayout["EndAddress"]).ToInt64();
            var address = (ulong)codeAddress.ToInt64();

            if (address < start || address > end)
                return IntPtr.Zero;

            var mapBase = heap.ReadIntPtr(this.codeHeapListNodeLayout["MapBase"]);
            var headerMap = heap.ReadIntPtr(this.codeHeapListNodeLayout["HeaderMap"]);

            return FindMethodStartInNibbleMap(mapBase, headerMap, codeAddress);
        }

        /// <summary>
        /// Reads the nibble map backwards from <paramref name="codeAddress"/> until it finds the
        /// bucket where a method begins.
        /// </summary>
        private static IntPtr FindMethodStartInNibbleMap(IntPtr mapBase, IntPtr mapStart, IntPtr codeAddress)
        {
            var relative = (ulong)codeAddress.ToInt64() - (ulong)mapBase.ToInt64();

            var mapIndex = relative / BytesPerBucket;
            var bucketByteIndex = (uint)((relative & (BytesPerBucket - 1)) / MapUnitSizeInBytes) + 1;

            var unit = ReadMapUnit(mapStart, mapIndex);

            if (IsPointer(unit))
                return DecodePointer(mapBase, unit);

            // Bring the nibble for this address into the low bits.
            unit >>= (int)NibbleShift(mapIndex);

            var nibble = unit & NibbleMask;

            // A method starts in this bucket - but only counts if it starts at or before the
            // address we are asking about.
            if (nibble != 0 && nibble <= bucketByteIndex)
                return AbsoluteAddress(mapBase, mapIndex, nibble);

            unit >>= 4;

            // Search backwards through the rest of this map unit.
            if (unit != 0)
            {
                mapIndex--;
                while ((unit & NibbleMask) == 0)
                {
                    unit >>= 4;
                    mapIndex--;
                }

                return AbsoluteAddress(mapBase, mapIndex, unit & NibbleMask);
            }

            // Nothing in this unit. If it was the first, the address cannot be in a method.
            if (mapIndex < NibblesPerMapUnit)
                return IntPtr.Zero;

            mapIndex = (mapIndex & ~(NibblesPerMapUnit - 1)) - 1;

            unit = ReadMapUnit(mapStart, mapIndex);
            if (unit == 0)
                return IntPtr.Zero;

            if (IsPointer(unit))
                return DecodePointer(mapBase, unit);

            while (mapIndex != 0 && (unit & NibbleMask) == 0)
            {
                unit >>= 4;
                mapIndex--;
            }

            return AbsoluteAddress(mapBase, mapIndex, unit & NibbleMask);
        }

        private static uint ReadMapUnit(IntPtr mapStart, ulong mapIndex)
        {
            var unitIndex = mapIndex / NibblesPerMapUnit;

            return new MemoryReader(mapStart).ReadUInt((int)(unitIndex * MapUnitSizeInBytes));
        }

        private static uint NibbleShift(ulong mapIndex)
        {
            // Nibble 0 sits in the most significant position, so the shift counts down.
            return 28 - (uint)(mapIndex & (NibblesPerMapUnit - 1)) * 4;
        }

        /// <summary>
        /// A map unit whose low nibble exceeds 8 is a relative pointer, not a nibble - written
        /// when a single code block covers the whole unit.
        /// </summary>
        private static bool IsPointer(uint unit) => (unit & NibbleMask) > 8;

        private static IntPtr DecodePointer(IntPtr mapBase, uint unit)
        {
            var nibble = unit & NibbleMask;
            var relative = (unit & ~NibbleMask) + ((nibble - 9) << 2);

            return mapBase + (int)relative;
        }

        private static IntPtr AbsoluteAddress(IntPtr mapBase, ulong mapIndex, uint nibble)
        {
            var offset = mapIndex * BytesPerBucket + (ulong)(nibble - 1) * MapUnitSizeInBytes;

            return new IntPtr(mapBase.ToInt64() + (long)offset);
        }
    }
}
