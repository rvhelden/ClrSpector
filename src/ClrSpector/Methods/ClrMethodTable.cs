using System;
using System.Collections.Generic;
using System.Linq;
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

        private const uint CategoryNullable = 0x00050000;
        private const uint CategoryPrimitive = 0x00060000;
        private const uint CategoryTruePrimitive = 0x00070000;

        /// <summary>
        /// Groups the primitive sub-categories, so a true primitive still reads as a primitive.
        /// </summary>
        private const uint CategoryElementTypeMask = 0x000E0000;

        private const uint CollectibleFlag = 0x00200000;
        private const uint RequiresAlign8Flag = 0x00800000;
        private const uint ContainsGcPointersFlag = 0x01000000;
        private const uint ContainsGenericVariablesFlag = 0x20000000;

        /// <summary>
        /// The low word of MTFlags, which only holds flags when it is not holding a component
        /// size. See <see cref="LowFlagsAre"/>.
        /// </summary>
        private const uint GenericsMask = 0x00000030;

        private const uint GenericsNonGeneric = 0x00000000;
        private const uint GenericsSharedInstantiation = 0x00000020;
        private const uint GenericsTypicalInstantiation = 0x00000030;

        private const uint IsByRefLikeFlag = 0x00001000;

        /// <summary>
        /// The low-word value the runtime substitutes for a string or array, whose own low word
        /// is a component size rather than flags.
        /// </summary>
        private const uint StringArrayLowFlags = GenericsNonGeneric;

        /// <summary>A string's component size: one UTF-16 code unit.</summary>
        private const uint StringComponentSize = 2;

        /// <summary>MethodTable.EEClassOrCanonMT carries its discriminator in the low bit.</summary>
        private const long UnionTagMask = 1;

        /// <summary>
        /// How many bits an ECMA-335 row id occupies - the low three bytes of a token.
        /// </summary>
        private const int RowIdBitCount = 24;

        /// <summary>All row-id bits set; the rest of a token is the table index.</summary>
        private const uint RowIdMask = (1u << RowIdBitCount) - 1;

        /// <summary>
        /// MTFlags2 holds the type's TypeDef row id above its flag bits, so the rid is the whole
        /// field shifted down.
        /// </summary>
        private const int TypeDefRidShift = 8;

        /// <summary>mdtTypeDef - the metadata table that type tokens live in.</summary>
        private const uint TypeDefTokenType = 0x02000000;

        private List<ClrFieldDescription> fields;

        private List<ClrInterfaceImplementation> declaredInterfaces;

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

        /// <summary>
        /// The module this type's runtime structures were allocated from, which is not always the
        /// module that declares it.
        /// </summary>
        /// <remarks>
        /// A constructed generic has to live somewhere: <c>List&lt;MyType&gt;</c> is declared by
        /// neither CoreLib nor your assembly alone, so the runtime picks a loader module for it -
        /// usually the one that makes the whole instantiation collectible together. That is why
        /// unloading an assembly can free MethodTables whose TypeDef row lives in CoreLib.
        /// </remarks>
        public IntPtr LoaderModule
        {
            get
            {
                if (this.AuxiliaryData == IntPtr.Zero)
                    return IntPtr.Zero;

                var descriptor = ContractDescriptor.Current;

                return descriptor.TryGetDataType("MethodTableAuxiliaryData", out var layout)
                       && layout.HasField("LoaderModule")
                    ? new MemoryReader(this.AuxiliaryData).ReadIntPtr(layout["LoaderModule"])
                    : IntPtr.Zero;
            }
        }

        /// <summary>
        /// How many generic dictionaries this type has - one for itself plus one per generic
        /// ancestor - or zero for a non-generic type.
        /// </summary>
        /// <remarks>
        /// A generic type's dictionary count and type-parameter count live in a GenericsDictInfo
        /// stored immediately <b>before the PerInstInfo array</b> - not inside the MethodTable,
        /// and not before the MethodTable either - so it is reached by stepping back from
        /// PerInstInfo. Measured: the eight bytes there decode as (1 dictionary, 1 type argument)
        /// for <c>List&lt;T&gt;</c> and (1, 2) for <c>Dictionary&lt;K,V&gt;</c>.
        ///
        /// The descriptor publishes the two field offsets but no size for the structure, so the
        /// size is derived from the last field rather than assumed - see
        /// <see cref="GenericsDictInfoSize"/>.
        /// </remarks>
        public ushort GenericDictionaryCount => this.GenericsDictInfo("NumDicts");

        /// <summary>How many type parameters this type declares, or zero when it is not generic.</summary>
        public ushort GenericTypeArgumentCount => this.GenericsDictInfo("NumTypeArgs");

        /// <summary>
        /// This type's actual type arguments, as type handles, or an empty array when it is not
        /// a constructed generic.
        /// </summary>
        /// <remarks>
        /// <para>
        /// PerInstInfo is an array of dictionary pointers, one per generic type in the
        /// inheritance chain, and the last is this type's own. A dictionary begins with its type
        /// arguments, so the first <see cref="GenericTypeArgumentCount"/> entries of that last
        /// dictionary are the instantiation. Measured: <c>List&lt;string&gt;</c> yields
        /// System.String, and <c>Dictionary&lt;string,int&gt;</c> yields System.String and
        /// System.Int32.
        /// </para>
        /// <para>
        /// Not every entry is a MethodTable. An open definition like <c>List&lt;T&gt;</c> has
        /// type <i>variables</i> here, and a pointer or byref argument is a TypeDesc - both of
        /// which the runtime marks in the handle's low bits, so
        /// <see cref="IsMethodTableHandle"/> says which are safe to decode.
        /// </para>
        /// </remarks>
        public IntPtr[] TypeArguments
        {
            get
            {
                var count = this.GenericTypeArgumentCount;
                var dictionaries = this.GenericDictionaryCount;

                if (count == 0 || dictionaries == 0 || this.PerInstInfo == IntPtr.Zero)
                    return Array.Empty<IntPtr>();

                var own = new MemoryReader(this.PerInstInfo)
                    .ReadIntPtr((dictionaries - 1) * IntPtr.Size);

                if (own == IntPtr.Zero || !ProcessMemoryRegions.IsReadable(own, count * IntPtr.Size))
                    return Array.Empty<IntPtr>();

                var reader = new MemoryReader(own);
                var arguments = new IntPtr[count];

                for (var i = 0; i < count; i++)
                    arguments[i] = reader.ReadIntPtr(i * IntPtr.Size);

                return arguments;
            }
        }

        /// <summary>
        /// True when a type handle is a MethodTable rather than a TypeDesc.
        /// </summary>
        /// <remarks>
        /// The runtime tags the low bits of a type handle to mark the ones that are not
        /// MethodTables - type variables, pointers, byrefs and function pointers are TypeDescs -
        /// so decoding one as a MethodTable would read the wrong structure entirely.
        /// </remarks>
        public static bool IsMethodTableHandle(IntPtr handle)
        {
            return handle != IntPtr.Zero && (handle.ToInt64() & 3) == 0;
        }

        /// <summary>
        /// The size of the GenericsDictInfo that precedes a generic type's MethodTable.
        /// </summary>
        /// <remarks>
        /// Derived rather than hardcoded: the descriptor gives the field offsets but not the
        /// structure's size, so it is the end of the last field rounded up to pointer alignment,
        /// which is what puts the MethodTable itself back on its natural boundary.
        /// </remarks>
        private static int GenericsDictInfoSize
        {
            get
            {
                var layout = ContractDescriptor.Current.GetDataType("GenericsDictInfo");

                if (layout.Size.HasValue)
                    return (int)layout.Size.Value;

                var end = 0;
                foreach (var name in layout.FieldNames)
                    end = Math.Max(end, layout[name] + sizeof(ushort));

                return (end + IntPtr.Size - 1) & ~(IntPtr.Size - 1);
            }
        }

        private ushort GenericsDictInfo(string field)
        {
            // Only a generic type has one; reading before a non-generic MethodTable would be
            // reading whatever the loader heap happened to put there.
            if (!this.HasInstantiation)
                return 0;

            var descriptor = ContractDescriptor.Current;
            if (!descriptor.TryGetDataType("GenericsDictInfo", out var layout)
                || !layout.HasField(field))
            {
                return 0;
            }

            if (this.PerInstInfo == IntPtr.Zero)
                return 0;

            var info = this.PerInstInfo - GenericsDictInfoSize;

            return ProcessMemoryRegions.IsReadable(info, GenericsDictInfoSize)
                ? new MemoryReader(info).ReadUShort(layout[field])
                : (ushort)0;
        }

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

        /// <summary>True for <see cref="System.Nullable{T}"/>, a sub-category of value type.</summary>
        public bool IsNullable => this.Category == CategoryNullable;

        /// <summary>True for a primitive, including the ones the runtime calls "true" primitives.</summary>
        public bool IsPrimitive => (this.Flags & CategoryElementTypeMask) == CategoryPrimitive;

        /// <summary>
        /// True for a primitive the runtime treats as intrinsic rather than merely primitive-like.
        /// </summary>
        public bool IsTruePrimitive => this.Category == CategoryTruePrimitive;

        /// <summary>
        /// True for <see cref="string"/>: it carries a component size like an array, but is not
        /// one, and its components are UTF-16 code units.
        /// </summary>
        public bool IsString =>
            this.HasComponentSize && !this.IsArray && this.ComponentSize == StringComponentSize;

        /// <summary>True when instances of this type contain references the GC must trace.</summary>
        public bool ContainsGcPointers => (this.Flags & ContainsGcPointersFlag) != 0;

        /// <summary>True for a type in a collectible load context, which the GC can unload.</summary>
        public bool IsCollectible => (this.Flags & CollectibleFlag) != 0;

        /// <summary>True when instances need 8-byte alignment on a 32-bit runtime.</summary>
        public bool RequiresAlign8 => (this.Flags & RequiresAlign8Flag) != 0;

        /// <summary>True for an open type - one still mentioning its own type parameters.</summary>
        public bool ContainsGenericVariables => (this.Flags & ContainsGenericVariablesFlag) != 0;

        /// <summary>True for any generic type, however instantiated.</summary>
        public bool HasInstantiation => !this.LowFlagsAre(GenericsMask, GenericsNonGeneric);

        /// <summary>True for the open definition, e.g. <c>List&lt;T&gt;</c> itself.</summary>
        public bool IsGenericTypeDefinition =>
            this.LowFlagsAre(GenericsMask, GenericsTypicalInstantiation);

        /// <summary>
        /// True when this instantiation shares its code with others, e.g.
        /// <c>List&lt;__Canon&gt;</c> standing in for every reference instantiation.
        /// </summary>
        public bool IsSharedByGenericInstantiations =>
            this.LowFlagsAre(GenericsMask, GenericsSharedInstantiation);

        /// <summary>
        /// True for a byref-like value type - <c>ref struct</c>, such as
        /// <see cref="System.Span{T}"/> - which may hold managed pointers and so cannot live on
        /// the heap.
        /// </summary>
        public bool IsByRefLike => this.LowFlagsAre(IsByRefLikeFlag, IsByRefLikeFlag);

        /// <summary>
        /// Tests flags in the low word of MTFlags.
        /// </summary>
        /// <remarks>
        /// A string or array spends that word on its component size, so reading flags out of it
        /// would be reading a length as a bitfield. The runtime substitutes a fixed value for
        /// those types instead, and so does this.
        /// </remarks>
        private bool LowFlagsAre(uint mask, uint expected)
        {
            var low = this.HasComponentSize ? StringArrayLowFlags : this.Flags;

            return (low & mask) == expected;
        }

        /// <summary>
        /// The row id of this type's TypeDef, which the runtime packs into the high bits of
        /// MTFlags2 rather than storing a whole token.
        /// </summary>
        public uint TypeDefRid => this.Flags2 >> TypeDefRidShift;

        /// <summary>
        /// This type's ECMA-335 TypeDef token, reassembled from <see cref="TypeDefRid"/>.
        /// Resolving it through <see cref="System.Reflection.Module.ResolveType(int)"/> on the
        /// type's own module is the metadata route to its name, and the only route available
        /// when there is no live type handle to ask.
        /// </summary>
        /// <remarks>
        /// For a generic instantiation this names the generic type definition - the TypeDef is
        /// <c>List`1</c>, not <c>List&lt;string&gt;</c> - because that is what metadata records.
        /// </remarks>
        public uint TypeDefToken => TypeDefTokenType | this.TypeDefRid;

        /// <summary>
        /// The managed type this MethodTable is the runtime representation of. A type handle is
        /// a MethodTable address, so this is the inverse of <see cref="ClrObject.From(Type)"/>.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="TypeDefToken"/> this carries the full instantiation
        /// (<c>List&lt;string&gt;</c>), but a shared instantiation names itself honestly as
        /// <c>List&lt;__Canon&gt;</c>. Only meaningful for a MethodTable belonging to this
        /// process; a stale or invented address will fault rather than return null.
        /// </remarks>
        public System.Type Type =>
            System.Type.GetTypeFromHandle(RuntimeTypeHandle.FromIntPtr(this.Address));

        /// <summary>This type's full name, or null when the address is not a live type handle.</summary>
        public string Name => this.Type?.FullName;

        /// <summary>
        /// This type's full name read from its module's metadata, without asking the runtime for
        /// a <see cref="System.Type"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="Name"/> goes through the type handle and so reports the full instantiation
        /// (<c>List&lt;string&gt;</c>); this reports what metadata records, which is the generic
        /// definition (<c>List`1</c>). Null when the module has no mapped image.
        /// </remarks>
        public string MetadataName => this.Metadata?.FullTypeName(this.TypeDefToken);

        /// <summary>The namespace metadata records for this type.</summary>
        public string MetadataNamespace => this.Metadata?.TypeName(this.TypeDefToken).Namespace;

        /// <summary>The metadata of the module that declares this type.</summary>
        public ClrModuleMetadata Metadata =>
            this.Module == IntPtr.Zero
                ? null
                : ClrModuleMetadata.AtImageBase(ClrModule.At(this.Module).Base);

        /// <summary>The methods this type declares, in MethodDescChunk order.</summary>
        public List<ClrMethodDescription> Methods { get; private set; }

        /// <summary>
        /// The fields this type declares itself - its own instance fields, then its statics.
        /// Inherited fields belong to the type that declares them, so walk
        /// <see cref="ParentMethodTable"/> for those.
        /// </summary>
        /// <remarks>
        /// Read on first access rather than eagerly like <see cref="Methods"/>, because a
        /// MethodTable gets decoded for every distinct type on a heap walk and most callers never
        /// look at fields.
        /// </remarks>
        public List<ClrFieldDescription> Fields => this.fields ??= this.ReadFields();

        /// <summary>
        /// The interfaces this type declares it implements, from metadata.
        /// </summary>
        /// <remarks>
        /// Declared, not inherited. <see cref="NumberOfInterfaces"/> is the runtime's closure over
        /// everything a type implements, base classes included, and the contract publishes no
        /// pointer to that map - only the count. The C# compiler already writes a class's own
        /// closure into metadata, so the two usually agree; a class that inherits an interface
        /// from its base class declares none of its own and the counts diverge.
        /// </remarks>
        public IReadOnlyList<ClrInterfaceImplementation> DeclaredInterfaces =>
            this.declaredInterfaces ??= ClrInterfaceImplementation.Read(this);

        /// <summary>
        /// How many instance fields this type adds on top of its parent's - which is what the
        /// FieldDesc list actually holds, since the parent's live on the parent.
        /// </summary>
        public int DeclaredInstanceFieldCount
        {
            get
            {
                if (this.EEClass == null)
                    return 0;

                var inherited = this.ParentMethodTable?.EEClass?.NumberOfInstanceFields ?? 0;

                return Math.Max(0, this.EEClass.NumberOfInstanceFields - inherited);
            }
        }

        private List<ClrFieldDescription> ReadFields()
        {
            var fieldList = new List<ClrFieldDescription>();

            if (this.EEClass == null || this.EEClass.FieldDescList == IntPtr.Zero)
                return fieldList;

            // The array holds this type's own instance fields followed by its static fields, all
            // of one fixed size - so it is indexed rather than walked.
            var stride = (int)ContractDescriptor.Current.GetDataType("FieldDesc").RequiredSize;
            var count = this.DeclaredInstanceFieldCount + this.EEClass.NumberOfStaticFields;

            for (var i = 0; i < count; i++)
                fieldList.Add(ClrFieldDescription.Create(new MemoryReader(this.EEClass.FieldDescList + i * stride)));

            return fieldList;
        }

        /// <summary>
        /// The decoded MethodDesc this type declares for <paramref name="method"/>, or null when
        /// the type does not declare it.
        /// </summary>
        /// <remarks>
        /// Matched on the metadata token, which is the only identity a MethodDesc carries - it
        /// stores no name. An inherited method belongs to the type that declares it, so look it
        /// up on that type's MethodTable rather than a subclass's.
        /// </remarks>
        public ClrMethodDescription FindMethod(System.Reflection.MethodBase method)
        {
            return method == null ? null : this.FindMethod((uint)method.MetadataToken);
        }

        /// <summary>
        /// The method this type declares with this name, or null when it declares none.
        /// </summary>
        /// <remarks>
        /// The name comes from the module's metadata, so this reaches a method without a
        /// <see cref="Type"/> or a <see cref="System.Reflection.MethodBase"/>. An overloaded
        /// name has more than one match; this returns the first, and
        /// <see cref="FindMethods(string)"/> returns them all.
        /// </remarks>
        public ClrMethodDescription FindMethod(string name)
        {
            return this.FindMethods(name).FirstOrDefault();
        }

        /// <summary>Every method this type declares with this name, overloads included.</summary>
        public IEnumerable<ClrMethodDescription> FindMethods(string name)
        {
            if (name == null || this.Methods == null)
                yield break;

            foreach (var method in this.Methods)
            {
                if (method.Name == name)
                    yield return method;
            }
        }

        /// <summary>The decoded MethodDesc with this metadata token, or null.</summary>
        public ClrMethodDescription FindMethod(uint metadataToken)
        {
            return this.Methods?.FirstOrDefault(m => m.MetadataToken == metadataToken);
        }

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
            var methodTableOffset = chunkLayout["MethodTable"];
            var countOffset = chunkLayout["Count"];
            var tokenRangeOffset = chunkLayout["FlagsAndTokenRange"];

            var chunk = firstChunk;
            while (chunk != IntPtr.Zero)
            {
                var chunkReader = new MemoryReader(chunk);
                var count = chunkReader.ReadByte(countOffset) + 1;
                // The token is split between the chunk (its range) and each MethodDesc (its
                // remainder), so the range keeps whatever row-id bits the remainder does not.
                var tokenRangeMask = (ushort)(RowIdMask >> tokenRemainderBits);
                var tokenRange = (ushort)(chunkReader.ReadUShort(tokenRangeOffset) & tokenRangeMask);

                var offset = 0;
                for (var i = 0; i < count; i++)
                {
                    var methodReader = chunkReader.Offset(chunkHeaderSize + offset);
                    var method = ClrMethodDescription.Create(methodReader, tokenRange, tokenRemainderBits);
                    method.MethodTablePointer = chunkReader.ReadIntPtr(methodTableOffset);

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
            return $"MethodTable {this.Name ?? "<unnamed>"} @0x{this.Address.ToInt64():x} " +
                   $"token=0x{this.TypeDefToken:x8} baseSize={this.BaseSize} " +
                   $"virtuals={this.NumberOfVirtuals} interfaces={this.NumberOfInterfaces} " +
                   $"methods={this.Methods?.Count ?? 0} fields={this.DeclaredInstanceFieldCount}";
        }
    }
}