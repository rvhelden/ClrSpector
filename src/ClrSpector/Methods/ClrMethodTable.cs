using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// The hot half of a managed type (a MethodTable), plus the methods it owns.
    /// </summary>
    /// <remarks>
    /// Every field offset comes from the runtime's contract descriptor. Methods are enumerated
    /// by walking the EEClass's MethodDescChunk list, which depends only on published data
    /// layout - not on generated code - so it needs no precode or opcode decoding.
    /// </remarks>
    public unsafe class ClrMethodTable
    {
        /// <summary>Set when the low word of MTFlags holds a component size instead of flags.</summary>
        internal const uint HasComponentSizeFlag = 0x80000000;

        /// <summary>The component size, when <see cref="HasComponentSizeFlag"/> is set.</summary>
        internal const uint ComponentSizeMask = 0x0000FFFF;

        /// <summary>Selects the type's category from the high word of MTFlags.</summary>
        private const uint CategoryMask = 0x000F0000;

        private const uint CategoryClass = 0x00000000;
        private const uint CategoryValueType = 0x00040000;
        private const uint CategoryArray = 0x00080000;
        private const uint CategoryInterface = 0x000C0000;

        /// <summary>
        /// Distinguishes the four top-level categories. Sub-categories live in the low bits of
        /// the category field: enums and primitives are sub-categories of value type, and a
        /// single-dimension array is a sub-category of array, so category equality alone would
        /// misclassify them.
        /// </summary>
        private const uint CategoryGroupMask = 0x000C0000;

        /// <summary>MethodTable.EEClassOrCanonMT carries its discriminator in the low bit.</summary>
        private const long UnionTagMask = 1;

        /// <summary>The token range occupies the low bits of MethodDescChunk.FlagsAndTokenRange.</summary>
        private const ushort TokenRangeMask = 0x0FFF;

        public void* BasePointer { get; private set; }

        public uint Size { get; private set; }

        public uint Flags { get; private set; }

        public uint BaseSize { get; private set; }

        public uint Flags2 { get; private set; }

        public ushort NumberOfVirtuals { get; private set; }

        public ushort NumberOfInterfaces { get; private set; }

        public IntPtr ParentMethodTablePointer { get; private set; }

        public ClrMethodTable ParentMethodTable => this.ParentMethodTablePointer == IntPtr.Zero
            ? null
            : ClrMethodTable.Create(new MemoryReader(this.ParentMethodTablePointer));

        public IntPtr Module { get; private set; }

        public IntPtr AuxiliaryData { get; private set; }

        public IntPtr PerInstInfo { get; private set; }

        /// <summary>
        /// The EEClass for this type, or null when this MethodTable shares another's (generic
        /// instantiations point at a canonical MethodTable instead).
        /// </summary>
        public ClrEEClass EEClass { get; private set; }

        /// <summary>
        /// The canonical MethodTable for this type. Equal to this one for a canonical type.
        /// </summary>
        public IntPtr CanonMethodTablePointer { get; private set; }

        public bool IsCanonicalMethodTable => this.CanonMethodTablePointer == this.Address;

        public MethodTableUnionFieldKind UnionKind { get; private set; }

        public IntPtr Address => (IntPtr)this.BasePointer;

        /// <summary>Only meaningful for strings and arrays.</summary>
        public ushort ComponentSize { get; private set; }

        public bool HasComponentSize => (this.Flags & HasComponentSizeFlag) != 0;

        private uint Category => this.Flags & CategoryMask;

        public bool IsInterface => this.Category == CategoryInterface;

        /// <summary>True for both single-dimension and multi-dimension arrays.</summary>
        public bool IsArray => (this.Category & CategoryGroupMask) == CategoryArray;

        /// <summary>True for value types, including enums, nullables and primitives.</summary>
        public bool IsValueType => (this.Category & CategoryGroupMask) == CategoryValueType;

        public bool IsClass => this.Category == CategoryClass;

        /// <summary>The methods this type declares, in MethodDescChunk order.</summary>
        public List<ClrMethodDescription> Methods { get; private set; }

        public static ClrMethodTable Create(MemoryReader reader)
        {
            var descriptor = ContractDescriptor.Current;
            var layout = descriptor.GetDataType("MethodTable");

            var mt = new ClrMethodTable();
            mt.BasePointer = reader.BasePointer;
            mt.Size = layout.Size ?? 0;

            mt.Flags = reader.ReadUInt(layout["MTFlags"]);
            mt.BaseSize = reader.ReadUInt(layout["BaseSize"]);
            mt.Flags2 = reader.ReadUInt(layout["MTFlags2"]);
            mt.NumberOfVirtuals = reader.ReadUShort(layout["NumVirtuals"]);
            mt.NumberOfInterfaces = reader.ReadUShort(layout["NumInterfaces"]);
            mt.ParentMethodTablePointer = reader.ReadIntPtr(layout["ParentMethodTable"]);
            mt.Module = reader.ReadIntPtr(layout["Module"]);
            mt.AuxiliaryData = reader.ReadIntPtr(layout["AuxiliaryData"]);
            mt.PerInstInfo = reader.ReadIntPtr(layout["PerInstInfo"]);

            if (mt.HasComponentSize)
                mt.ComponentSize = (ushort)(mt.Flags & 0x0000FFFF);

            // EEClassOrCanonMT is a tagged union; the low bit says what the pointer means.
            var union = reader.ReadIntPtr(layout["EEClassOrCanonMT"]);
            mt.UnionKind = (MethodTableUnionFieldKind)(union.ToInt64() & UnionTagMask);
            var unionTarget = new IntPtr(union.ToInt64() & ~(long)UnionTagMask);

            switch (mt.UnionKind)
            {
                case MethodTableUnionFieldKind.EEClass:
                    mt.EEClass = ClrEEClass.Create(new MemoryReader(unionTarget));
                    mt.CanonMethodTablePointer = mt.Address;
                    break;

                case MethodTableUnionFieldKind.MethodTable:
                    // A shared instantiation: the EEClass hangs off the canonical MethodTable.
                    mt.CanonMethodTablePointer = unionTarget;
                    mt.EEClass = EEClassOf(unionTarget);
                    break;

                default:
                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"MethodTable at 0x{mt.Address.ToInt64():x} has an unrecognised EEClassOrCanonMT tag " +
                        $"({(int)mt.UnionKind}).");
            }

            mt.Methods = mt.EEClass == null
                ? new List<ClrMethodDescription>()
                : ReadMethods(mt.EEClass.MethodDescChunks, descriptor);

            return mt;
        }

        /// <summary>
        /// Reads only the EEClass pointer of another MethodTable, without decoding that whole
        /// MethodTable - which would recurse through the canonical chain.
        /// </summary>
        private static ClrEEClass EEClassOf(IntPtr methodTable)
        {
            if (methodTable == IntPtr.Zero)
                return null;

            var layout = ContractDescriptor.Current.GetDataType("MethodTable");
            var union = new MemoryReader(methodTable).ReadIntPtr(layout["EEClassOrCanonMT"]);

            // Only follow the union when it actually points at an EEClass.
            if ((union.ToInt64() & UnionTagMask) != (long)MethodTableUnionFieldKind.EEClass)
                return null;

            return ClrEEClass.Create(new MemoryReader(new IntPtr(union.ToInt64() & ~(long)UnionTagMask)));
        }

        /// <summary>
        /// Walks the MethodDescChunk list, reading each MethodDesc it contains.
        /// </summary>
        /// <remarks>
        /// A chunk stores biased values: the real method count is Count + 1 and the real chunk
        /// size is (Size + 1) * MethodDescAlignment. Each MethodDesc's own byte size varies with
        /// its classification, but every MethodDesc *within one chunk* is the same size, so the
        /// stride is the chunk's MethodDesc region divided by its method count.
        ///
        /// Up to .NET 10 the runtime published a MethodDescSizeTable global that mapped a
        /// classification to a size, and that is used when present. .NET 11 removed the global,
        /// so the stride is derived from the chunk instead. Either way every step is cross-checked
        /// against the MethodDesc's own ChunkIndex, which independently records where it sits -
        /// so a wrong step is caught rather than silently yielding a bogus method.
        /// </remarks>
        private static List<ClrMethodDescription> ReadMethods(IntPtr firstChunk, ContractDescriptor descriptor)
        {
            var methods = new List<ClrMethodDescription>();
            if (firstChunk == IntPtr.Zero)
                return methods;

            var chunkLayout = descriptor.GetDataType("MethodDescChunk");
            var chunkHeaderSize = (int)chunkLayout.RequiredSize;
            var alignment = (int)descriptor.Globals.Number("MethodDescAlignment");
            var tokenRemainderBits = (int)descriptor.Globals.Number("MethodDescTokenRemainderBitCount");
            var sizes = new MethodDescSizes(descriptor);

            var nextOffset = chunkLayout["Next"];
            var countOffset = chunkLayout["Count"];
            var tokenRangeOffset = chunkLayout["FlagsAndTokenRange"];

            var chunk = firstChunk;
            while (chunk != IntPtr.Zero)
            {
                var chunkReader = new MemoryReader(chunk);
                var count = chunkReader.ReadByte(countOffset) + 1;
                var tokenRange = (ushort)(chunkReader.ReadUShort(tokenRangeOffset) & TokenRangeMask);

                var offset = 0;
                for (var i = 0; i < count; i++)
                {
                    var methodReader = chunkReader.Offset(chunkHeaderSize + offset);
                    var method = ClrMethodDescription.Create(methodReader, tokenRange, tokenRemainderBits);

                    if (method.ChunkIndex * alignment != offset)
                        throw new ClrSpectorUnsupportedRuntimeException(
                            $"MethodDesc {i} in the chunk at 0x{chunk.ToInt64():x} reports ChunkIndex " +
                            $"{method.ChunkIndex} (offset {method.ChunkIndex * alignment}) but was read at " +
                            $"offset {offset}. The computed MethodDesc size is wrong.");

                    methods.Add(method);

                    offset += sizes.SizeOf(method.Flags);
                }

                chunk = chunkReader.ReadIntPtr(nextOffset);
            }

            return methods;
        }

        public override string ToString()
        {
            return $"MethodTable @0x{this.Address.ToInt64():x} baseSize={this.BaseSize} " +
                   $"virtuals={this.NumberOfVirtuals} interfaces={this.NumberOfInterfaces} " +
                   $"methods={this.Methods?.Count ?? 0}";
        }
    }
}