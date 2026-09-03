using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// A module's ECMA-335 metadata, read out of the image the loader mapped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what lets a name be recovered without going through <see cref="Type"/> or
    /// <see cref="System.Reflection.MethodBase"/>. A MethodTable knows its TypeDef token and a
    /// MethodDesc knows its MethodDef token, but neither stores a name - the names live in the
    /// module's string heap, and until now the only way to reach them was to hand the token back
    /// to reflection.
    /// </para>
    /// <para>
    /// The metadata is found by walking the mapped image's own headers: the PE header's CLI data
    /// directory gives the COR20 header, which gives the metadata directory. Because the image is
    /// mapped rather than read from disk, a relative virtual address is simply an offset from the
    /// module base, so no section translation is needed.
    /// </para>
    /// <para>
    /// The tables are then read by <see cref="MetadataImage"/> rather than by
    /// <c>System.Reflection.Metadata</c>. That is not purity for its own sake: the point of this
    /// project is that everything it reports is derived from the runtime's own published data, and
    /// a managed metadata reader is another implementation of the same spec sitting in the middle
    /// of that. Nothing here depends on any of <c>System.Reflection</c>.
    /// </para>
    /// <para>
    /// Nothing is copied. Rows, names and blobs are read in place out of the mapped bytes, which
    /// are read-only for the life of the module.
    /// </para>
    /// </remarks>
    public sealed unsafe class ClrModuleMetadata
    {
        /// <summary>Index of the CLI header in the PE data directories.</summary>
        private const int CliDirectoryIndex = 14;

        /// <summary>Where the data directories begin within the optional header.</summary>
        private const int DataDirectoriesOffsetPe32Plus = 112;

        private const int DataDirectoriesOffsetPe32 = 96;

        private const ushort Pe32PlusMagic = 0x20B;

        /// <summary>Offset of the metadata directory within the COR20 header.</summary>
        private const int Cor20MetadataDirectoryOffset = 8;

        /// <summary>A token's low three bytes are its row id; the high byte is its table.</summary>
        private const uint RowIdMask = 0x00FFFFFF;

        /// <summary>FieldAttributes.Static.</summary>
        private const uint FieldStatic = 0x0010;

        /// <summary>FieldAttributes.Literal, which is what makes a field an enum member.</summary>
        private const uint FieldLiteral = 0x0040;

        /// <summary>The calling convention byte that starts a FieldSig.</summary>
        private const byte FieldSignature = 0x06;

        /// <summary>How many assemblies a reference may be followed through before giving up.</summary>
        private const int MaximumResolutionHops = 8;

        private static readonly ClrCustomAttribute[] NoAttributes = new ClrCustomAttribute[0];

        private Dictionary<string, (MetadataTable Table, uint RowId)> typesByName;

        /// <summary>The table byte of a user string token, which names the <c>#US</c> heap.</summary>
        private const int UserStringTokenType = 0x70;

        /// <summary>The calling convention byte that marks a signature blob as a field's.</summary>
        private const byte FieldSignatureConvention = 0x06;

        /// <summary>The calling convention byte that marks a signature blob as a method's locals.</summary>
        private const byte LocalSignatureConvention = 0x07;

        /// <summary>
        /// A stop on a local count read from a blob. ECMA-335 allows 0xFFFE locals and no more,
        /// so a larger count means the blob is not being read as a LocalVarSig at all.
        /// </summary>
        private const uint MaximumLocals = 0xFFFE;

        private static readonly ConcurrentDictionary<IntPtr, ClrModuleMetadata> cache =
            new ConcurrentDictionary<IntPtr, ClrModuleMetadata>();

        private Dictionary<uint, uint> enclosingTypes;

        private ClrModuleMetadata(IntPtr imageBase, IntPtr metadata, int size)
        {
            this.ImageBase = imageBase;
            this.MetadataAddress = metadata;
            this.MetadataSize = size;
            this.Image = MetadataImage.At(metadata, size);
        }

        /// <summary>The base address of the mapped image.</summary>
        public IntPtr ImageBase { get; }

        public IntPtr MetadataAddress { get; }

        public int MetadataSize { get; }

        /// <summary>The metadata's tables and heaps, read in place.</summary>
        public MetadataImage Image { get; }

        /// <summary>
        /// The metadata of <paramref name="module"/>, or null when it has no mapped image - a
        /// module built at runtime keeps its metadata elsewhere.
        /// </summary>
        public static ClrModuleMetadata Of(ClrModule module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));

            var metadata = AtImageBase(module.Base);

            // Kept so a TypeRef can be followed to the assembly that defines it, which needs the
            // loader's AssemblyRef map and so cannot be done from the image alone. Assigned rather
            // than passed to the cache because the cache is keyed on the image base, which is all
            // the metadata itself needs.
            metadata.Owner ??= module;

            return metadata;
        }

        /// <summary>
        /// The loader's module, when this metadata was reached through one.
        /// </summary>
        /// <remarks>
        /// Null when the image base was given directly. Everything except cross-assembly
        /// reference resolution works without it.
        /// </remarks>
        public ClrModule Owner { get; private set; }

        /// <summary>The metadata of the image mapped at <paramref name="imageBase"/>.</summary>
        public static ClrModuleMetadata AtImageBase(IntPtr imageBase)
        {
            if (imageBase == IntPtr.Zero)
                return null;

            return cache.GetOrAdd(imageBase, Read);
        }

        /// <summary>
        /// The namespace and name of a type, from its TypeDef token.
        /// </summary>
        public (string Namespace, string Name) TypeName(uint typeDefToken)
        {
            if (!this.TryRow(typeDefToken, MetadataTable.TypeDef, out var rowId))
                return (null, null);

            // TypeDef: Flags, Name, Namespace, Extends, FieldList, MethodList.
            return (
                this.Image.String(this.Image.ReadColumn(MetadataTable.TypeDef, rowId, 2)),
                this.Image.String(this.Image.ReadColumn(MetadataTable.TypeDef, rowId, 1)));
        }

        /// <summary>
        /// A type's full name as metadata spells it - <c>Namespace.Name</c>, with a nested type
        /// joined to its declaring type by <c>+</c>.
        /// </summary>
        public string FullTypeName(uint typeDefToken)
        {
            if (!this.TryRow(typeDefToken, MetadataTable.TypeDef, out var rowId))
                return null;

            return this.FullTypeNameOfRow(rowId);
        }

        /// <summary>The name of a method, from its MethodDef token.</summary>
        public string MethodName(uint methodDefToken)
        {
            if (!this.TryRow(methodDefToken, MetadataTable.MethodDef, out var rowId))
                return null;

            return this.Image.String(this.Image.ReadColumn(MetadataTable.MethodDef, rowId, 3));
        }

        /// <summary>The name of a field, from its FieldDef token.</summary>
        public string FieldName(uint fieldDefToken)
        {
            if (!this.TryRow(fieldDefToken, MetadataTable.Field, out var rowId))
                return null;

            return this.Image.String(this.Image.ReadColumn(MetadataTable.Field, rowId, 1));
        }

        /// <summary>
        /// A readable name for any token an IL operand can carry, so a listing can be produced
        /// without reflection resolving anything.
        /// </summary>
        /// <remarks>
        /// A token that does not resolve comes back as its own hexadecimal value rather than
        /// throwing - a listing of a partly unreadable method is worth more than no listing.
        /// </remarks>
        public string TokenName(int token)
        {
            try
            {
                var table = (MetadataTable)(uint)((token >> 24) & 0xFF);
                var rowId = (uint)token & RowIdMask;

                switch (table)
                {
                    case MetadataTable.TypeDef:
                        return this.FullTypeName((uint)token);

                    case MetadataTable.TypeRef:
                        return this.TypeRefName(rowId);

                    case MetadataTable.MethodDef:
                    {
                        var owner = this.DeclaringTypeOf(MetadataTable.MethodDef, rowId, 5);

                        return $"{this.FullTypeNameOfRow(owner)}::{this.MethodName((uint)token)}";
                    }

                    case MetadataTable.Field:
                    {
                        var owner = this.DeclaringTypeOf(MetadataTable.Field, rowId, 4);

                        return $"{this.FullTypeNameOfRow(owner)}::{this.FieldName((uint)token)}";
                    }

                    case MetadataTable.MemberRef:
                    {
                        // MemberRef: Class (a coded index), Name, Signature.
                        var parent = this.Image.DecodeCoded(
                            CodedIndex.MemberRefParent,
                            this.Image.ReadColumn(MetadataTable.MemberRef, rowId, 0));

                        var name = this.Image.String(
                            this.Image.ReadColumn(MetadataTable.MemberRef, rowId, 1));

                        var owner = parent.Table == MetadataTable.TypeRef
                            ? this.TypeRefName(parent.RowId)
                            : parent.Table == MetadataTable.TypeDef
                                ? this.FullTypeNameOfRow(parent.RowId)
                                : parent.Table.ToString();

                        return $"{owner}::{name}";
                    }

                    case MetadataTable.TypeSpec:
                    {
                        // A constructed generic has no name of its own - only an encoded
                        // signature, which has to be decoded to say anything useful about it.
                        var blob = this.Image.Blob(
                            this.Image.ReadColumn(MetadataTable.TypeSpec, rowId, 0));

                        return SignatureTypeReader.ReadType(ref blob, this.Image).ToString();
                    }

                    case MetadataTable.MethodSpec:
                        return $"MethodSpecification 0x{token:x8}";

                    default:
                        return $"0x{token:x8}";
                }
            }
            catch (Exception)
            {
                return $"0x{token:x8}";
            }
        }

        /// <summary>
        /// The signature of whatever callable a token names - a MethodDef, a MemberRef, a
        /// generic instantiation or a standalone signature - or null when the token names
        /// nothing with a signature.
        /// </summary>
        /// <remarks>
        /// A name is enough to print a call, but not to read one: to know how much of the
        /// evaluation stack a <c>call</c> consumes you need its parameter count and whether it
        /// has a <c>this</c>, and both live in the signature blob. That is what
        /// <see cref="ClrMethodCSharp"/> needs to project a call read from a MethodDesc, where
        /// no reflection <see cref="System.Reflection.MethodBase"/> exists to ask.
        /// </remarks>
        public ClrMethodSignature TokenSignature(int token)
        {
            try
            {
                var table = (MetadataTable)(uint)((token >> 24) & 0xFF);
                var rowId = (uint)token & RowIdMask;

                switch (table)
                {
                    // MethodDef column 4, MemberRef column 2 and StandAloneSig column 0 are all
                    // the row's signature blob.
                    case MetadataTable.MethodDef:
                        return this.DecodeSignature(MetadataTable.MethodDef, rowId, 4);

                    case MetadataTable.MemberRef:
                        return this.DecodeSignature(MetadataTable.MemberRef, rowId, 2);

                    case MetadataTable.StandAloneSig:
                        return this.DecodeSignature(MetadataTable.StandAloneSig, rowId, 0);

                    case MetadataTable.MethodSpec:
                    {
                        // A MethodSpec is an instantiation of another method; the parameter
                        // count and the this-ness are the generic method's own.
                        var parent = this.Image.DecodeCoded(
                            CodedIndex.MethodDefOrRef,
                            this.Image.ReadColumn(MetadataTable.MethodSpec, rowId, 0));

                        return this.TokenSignature(((int)parent.Table << 24) | (int)parent.RowId);
                    }

                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Decodes the signature blob a row's column points at.</summary>
        private ClrMethodSignature DecodeSignature(MetadataTable table, uint rowId, int column)
        {
            if (rowId == 0 || rowId > (uint)this.Image.RowCount(table))
                return null;

            var blob = this.Image.Blob(this.Image.ReadColumn(table, rowId, column));

            // A MemberRef can name a field as well as a method, and a field signature starts
            // with its own calling convention - 0x06 - which is not a method's.
            if (blob.PeekByte() == FieldSignatureConvention)
                return null;

            return ClrMethodSignature.Decode(ref blob, this.Image);
        }

        /// <summary>
        /// The local variable slots a method body's signature token describes, in slot order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A method body carries its locals only as a token
        /// (<see cref="ClrMethodBodyImage.LocalSignatureToken"/>) for a standalone signature.
        /// Decoding it is what makes a MethodDesc-sourced listing able to say <c>int loc0</c>
        /// rather than only <c>loc0</c> - the runtime keeps no decoded form to ask for.
        /// </para>
        /// <para>
        /// Empty when the token is zero, names something that is not a locals signature, or
        /// cannot be read - a partly readable listing is worth more than none.
        /// </para>
        /// </remarks>
        public IReadOnlyList<ClrIlLocal> LocalSignature(int token)
        {
            var locals = new List<ClrIlLocal>();

            try
            {
                if (token == 0 || !this.TryRow((uint)token, MetadataTable.StandAloneSig, out var rowId))
                    return locals;

                var blob = this.Image.Blob(
                    this.Image.ReadColumn(MetadataTable.StandAloneSig, rowId, 0));

                // The same table holds calli signatures, which are methods and not locals.
                if (blob.ReadByte() != LocalSignatureConvention)
                    return locals;

                var count = blob.ReadCompressedUInt();

                if (count > MaximumLocals)
                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"A local signature declares {count} locals, which is beyond anything " +
                        "ECMA-335 allows, so the blob is not being read correctly.");

                for (var i = 0; i < (int)count; i++)
                    locals.Add(ClrIlLocal.Of(i, SignatureTypeReader.ReadType(ref blob, this.Image)));
            }
            catch (Exception)
            {
                // Whatever was decoded before the blob stopped making sense still names slots.
            }

            return locals;
        }

        /// <summary>The user string a <c>ldstr</c> token names.</summary>
        public string UserString(int token)
        {
            try
            {
                if ((token >> 24) != UserStringTokenType)
                    return null;

                return this.Image.UserString((uint)token & RowIdMask);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The relative virtual address of a method's body, or zero when it has none - an
        /// abstract method, or one the runtime implements itself.
        /// </summary>
        public int MethodBodyRva(uint methodDefToken)
        {
            if (!this.TryRow(methodDefToken, MetadataTable.MethodDef, out var rowId))
                return 0;

            // MethodDef column 0 is the RVA.
            return (int)this.Image.ReadColumn(MetadataTable.MethodDef, rowId, 0);
        }

        /// <summary>Splits a token, checking it names the table the caller expects.</summary>
        /// <summary>
        /// The custom attributes applied to whatever <paramref name="parentToken"/> names.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Any token the HasCustomAttribute coded index can carry works here: a TypeDef, a
        /// MethodDef, a Field, a Param, a GenericParam, the Module row or the Assembly row. That
        /// is why there is one method rather than one per kind of member.
        /// </para>
        /// <para>
        /// The table is sorted by parent, so this binary-searches it. When the sorted bit is not
        /// set - which a rewritten or generated image can leave clear - it falls back to a scan
        /// rather than to a search that would silently miss rows.
        /// </para>
        /// </remarks>
        public IReadOnlyList<ClrCustomAttribute> CustomAttributes(int parentToken)
        {
            var table = (MetadataTable)(uint)((parentToken >> 24) & 0xFF);
            var rowId = (uint)parentToken & RowIdMask;
            var rows = (uint)this.Image.RowCount(MetadataTable.CustomAttribute);

            if (rows == 0 || rowId == 0)
                return NoAttributes;

            uint parent;

            try
            {
                parent = this.Image.EncodeCoded(CodedIndex.HasCustomAttribute, table, rowId);
            }
            catch (ClrSpectorUnsupportedRuntimeException)
            {
                // Nothing of that kind can carry an attribute, so nothing does.
                return NoAttributes;
            }

            var found = new List<ClrCustomAttribute>();

            if (this.Image.IsSorted(MetadataTable.CustomAttribute))
            {
                var first = this.FirstAttributeRow(parent, rows);

                for (var row = first; row != 0 && row <= rows; row++)
                {
                    if (this.Image.ReadColumn(MetadataTable.CustomAttribute, row, 0) != parent)
                        break;

                    found.Add(ClrCustomAttribute.Read(this, row));
                }
            }
            else
            {
                for (var row = 1u; row <= rows; row++)
                {
                    if (this.Image.ReadColumn(MetadataTable.CustomAttribute, row, 0) == parent)
                        found.Add(ClrCustomAttribute.Read(this, row));
                }
            }

            return found;
        }

        /// <summary>
        /// The assembly-level attributes - what source writes as <c>[assembly: ...]</c>.
        /// </summary>
        /// <remarks>
        /// These hang off the single Assembly row, which only a module that carries the manifest
        /// has. A module without one returns nothing rather than throwing.
        /// </remarks>
        public IReadOnlyList<ClrCustomAttribute> AssemblyAttributes =>
            this.Image.RowCount(MetadataTable.Assembly) == 0
                ? NoAttributes
                : this.CustomAttributes(((int)MetadataTable.Assembly << 24) | 1);

        /// <summary>The module-level attributes - <c>[module: ...]</c>.</summary>
        public IReadOnlyList<ClrCustomAttribute> ModuleAttributes =>
            this.CustomAttributes(((int)MetadataTable.Module << 24) | 1);

        /// <summary>
        /// Every custom attribute in the module, whatever it is applied to.
        /// </summary>
        /// <remarks>
        /// Lazy, because a large assembly has tens of thousands of these and decoding a blob is
        /// not free. Use <see cref="ClrCustomAttribute.ParentToken"/> to see what each was
        /// applied to.
        /// </remarks>
        public IEnumerable<ClrCustomAttribute> AllCustomAttributes
        {
            get
            {
                var rows = (uint)this.Image.RowCount(MetadataTable.CustomAttribute);

                for (var row = 1u; row <= rows; row++)
                    yield return ClrCustomAttribute.Read(this, row);
            }
        }

        /// <summary>
        /// The full name of the type that declares whatever member a token names.
        /// </summary>
        /// <remarks>
        /// Works for a MethodDef or Field in this module, where the owner is found by searching
        /// the TypeDef table's member lists, and for a MemberRef, where the owner is the row the
        /// reference points at. This is how an attribute's type name is recovered from the
        /// constructor its row names, since the row never names the type directly.
        /// </remarks>
        public string DeclaringTypeName(int memberToken)
        {
            try
            {
                var table = (MetadataTable)(uint)((memberToken >> 24) & 0xFF);
                var rowId = (uint)memberToken & RowIdMask;

                switch (table)
                {
                    case MetadataTable.MethodDef:
                        return this.FullTypeNameOfRow(
                            this.DeclaringTypeOf(MetadataTable.MethodDef, rowId, 5));

                    case MetadataTable.Field:
                        return this.FullTypeNameOfRow(
                            this.DeclaringTypeOf(MetadataTable.Field, rowId, 4));

                    case MetadataTable.MemberRef:
                    {
                        var parent = this.Image.DecodeCoded(
                            CodedIndex.MemberRefParent,
                            this.Image.ReadColumn(MetadataTable.MemberRef, rowId, 0));

                        switch (parent.Table)
                        {
                            case MetadataTable.TypeRef:
                                return this.TypeRefName(parent.RowId);

                            case MetadataTable.TypeDef:
                                return this.FullTypeNameOfRow(parent.RowId);

                            case MetadataTable.TypeSpec:
                            {
                                var blob = this.Image.Blob(
                                    this.Image.ReadColumn(MetadataTable.TypeSpec, parent.RowId, 0));

                                return SignatureTypeReader.ReadType(ref blob, this.Image).ToString();
                            }

                            default:
                                return null;
                        }
                    }

                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Finds a type by its full metadata name, as a TypeDef here or a TypeRef to elsewhere.
        /// </summary>
        /// <remarks>
        /// The TypeDef table is searched first, so a type defined here is found as a definition
        /// rather than as a reference to itself. Names are matched exactly, including the
        /// namespace, and a nested type is spelled with <c>+</c> as
        /// <see cref="FullTypeName"/> spells it.
        /// </remarks>
        public bool TryFindType(string fullName, out (MetadataTable Table, uint RowId) found)
        {
            found = default;

            if (string.IsNullOrEmpty(fullName))
                return false;

            this.typesByName ??= this.BuildTypeIndex();

            return this.typesByName.TryGetValue(fullName, out found);
        }

        /// <summary>
        /// Finds a TypeDef in this module by its full metadata name.
        /// </summary>
        public uint FindTypeDef(string fullName)
        {
            return this.TryFindType(fullName, out var found) && found.Table == MetadataTable.TypeDef
                ? ((uint)MetadataTable.TypeDef << 24) | found.RowId
                : 0;
        }

        /// <summary>
        /// The integer type an enum is stored as, or <see cref="CorElementType.END"/> when the
        /// enum's definition could not be reached.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An enum has no field saying what it is backed by. What it has is one instance field -
        /// conventionally called <c>value__</c> - whose own signature is the underlying type, so
        /// that field's signature is the answer.
        /// </para>
        /// <para>
        /// A TypeRef is followed to the assembly that defines it, through the loader's AssemblyRef
        /// map rather than by name matching, so this works for an enum in another assembly -
        /// which most enums used in attributes are. That needs <see cref="Owner"/>, and returns
        /// unknown without it.
        /// </para>
        /// </remarks>
        public CorElementType EnumUnderlyingType(MetadataTable table, uint rowId)
        {
            return this.EnumUnderlyingType(table, rowId, 0);
        }

        /// <summary>
        /// The name of the enum member equal to <paramref name="value"/>, or null when the enum
        /// cannot be reached or has no member with that value.
        /// </summary>
        /// <remarks>
        /// An enum's members are static literal fields, and their values are Constant rows. A
        /// value that matches no single member is decomposed into the flags that make it up, so a
        /// combination reads as <c>A | B</c> rather than as a number. A leftover that no member
        /// accounts for means the whole thing is reported as unknown rather than as a partial
        /// answer.
        /// </remarks>
        public string EnumMemberName(MetadataTable table, uint rowId, object value)
        {
            try
            {
                var members = this.EnumMembers(table, rowId, 0);

                if (members == null || members.Count == 0)
                    return null;

                var name = this.TypeNameOf(table, rowId);
                var numeric = Convert.ToInt64(value, CultureInfo.InvariantCulture);

                foreach (var member in members)
                {
                    if (member.Value == numeric)
                        return name == null ? member.Name : $"{name}.{member.Name}";
                }

                // No single member matches, so this is either a flags combination or a value the
                // enum does not name.
                if (numeric == 0)
                    return null;

                var remaining = numeric;
                var parts = new List<string>();

                foreach (var member in members)
                {
                    if (member.Value != 0 && (remaining & member.Value) == member.Value)
                    {
                        parts.Add(name == null ? member.Name : $"{name}.{member.Name}");
                        remaining &= ~member.Value;
                    }
                }

                return remaining == 0 && parts.Count > 1 ? string.Join(" | ", parts) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The first row of the CustomAttribute table whose parent is <paramref name="parent"/>.
        /// </summary>
        /// <remarks>
        /// A plain binary search finds some matching row, not the first of a run, so this keeps
        /// narrowing after a hit instead of stopping at one.
        /// </remarks>
        private uint FirstAttributeRow(uint parent, uint rows)
        {
            uint low = 1;
            var high = rows;
            uint first = 0;

            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var value = this.Image.ReadColumn(MetadataTable.CustomAttribute, middle, 0);

                if (value >= parent)
                {
                    if (value == parent)
                        first = middle;

                    if (middle == 1)
                        break;

                    high = middle - 1;
                }
                else
                {
                    low = middle + 1;
                }
            }

            return first;
        }

        /// <summary>
        /// Every full type name in the module, mapped to the row that carries it.
        /// </summary>
        /// <remarks>
        /// Built once rather than scanned per lookup, because resolving the enums in an
        /// assembly's attributes asks the same question repeatedly. TypeRefs are added only where
        /// no TypeDef claims the name, so a definition always wins over a reference.
        /// </remarks>
        private Dictionary<string, (MetadataTable Table, uint RowId)> BuildTypeIndex()
        {
            var index = new Dictionary<string, (MetadataTable, uint)>(StringComparer.Ordinal);

            for (var row = 1u; row <= (uint)this.Image.RowCount(MetadataTable.TypeDef); row++)
            {
                var name = this.FullTypeNameOfRow(row);

                if (name != null)
                    index[name] = (MetadataTable.TypeDef, row);
            }

            for (var row = 1u; row <= (uint)this.Image.RowCount(MetadataTable.TypeRef); row++)
            {
                var name = this.TypeRefName(row);

                if (name != null && !index.ContainsKey(name))
                    index[name] = (MetadataTable.TypeRef, row);
            }

            return index;
        }

        /// <summary>
        /// <see cref="EnumUnderlyingType(MetadataTable, uint)"/>, counting the assembly hops taken
        /// so a chain of forwarding references cannot loop.
        /// </summary>
        private CorElementType EnumUnderlyingType(MetadataTable table, uint rowId, int depth)
        {
            if (rowId == 0 || depth > MaximumResolutionHops)
                return CorElementType.END;

            try
            {
                if (table == MetadataTable.TypeRef)
                {
                    var target = this.FollowTypeRef(rowId);

                    return target == null
                        ? CorElementType.END
                        : target.Value.Metadata.EnumUnderlyingType(
                            MetadataTable.TypeDef, target.Value.RowId, depth + 1);
                }

                if (table != MetadataTable.TypeDef
                    || rowId > (uint)this.Image.RowCount(MetadataTable.TypeDef))
                {
                    return CorElementType.END;
                }

                foreach (var field in this.FieldRowsOf(rowId))
                {
                    // Field: Flags, Name, Signature. The one instance field is the backing value.
                    if ((this.Image.ReadColumn(MetadataTable.Field, field, 0) & FieldStatic) != 0)
                        continue;

                    var blob = this.Image.Blob(
                        this.Image.ReadColumn(MetadataTable.Field, field, 2));

                    // A FieldSig is the calling convention byte 0x06, then the type.
                    if (blob.ReadByte() != FieldSignature)
                        return CorElementType.END;

                    return SignatureTypeReader.ReadType(ref blob, this.Image).ElementType;
                }

                return CorElementType.END;
            }
            catch (Exception)
            {
                return CorElementType.END;
            }
        }

        /// <summary>
        /// The named members of an enum, as name and value pairs.
        /// </summary>
        private List<(string Name, long Value)> EnumMembers(
            MetadataTable table, uint rowId, int depth)
        {
            if (rowId == 0 || depth > MaximumResolutionHops)
                return null;

            if (table == MetadataTable.TypeRef)
            {
                var target = this.FollowTypeRef(rowId);

                return target?.Metadata.EnumMembers(
                    MetadataTable.TypeDef, target.Value.RowId, depth + 1);
            }

            if (table != MetadataTable.TypeDef
                || rowId > (uint)this.Image.RowCount(MetadataTable.TypeDef))
            {
                return null;
            }

            var members = new List<(string, long)>();

            foreach (var field in this.FieldRowsOf(rowId))
            {
                var flags = this.Image.ReadColumn(MetadataTable.Field, field, 0);

                // Only the static literal fields are members; the instance one is the storage.
                if ((flags & FieldStatic) == 0 || (flags & FieldLiteral) == 0)
                    continue;

                if (!this.TryConstantOf(MetadataTable.Field, field, out var value))
                    continue;

                members.Add((this.Image.String(
                    this.Image.ReadColumn(MetadataTable.Field, field, 1)), value));
            }

            return members;
        }

        /// <summary>
        /// The value of a Constant row attached to a field, as a 64-bit integer.
        /// </summary>
        /// <remarks>
        /// Only the integral element types are read: an enum member is always one of those, and a
        /// string or floating-point constant is not something a member name can be recovered from.
        /// </remarks>
        private bool TryConstantOf(MetadataTable table, uint rowId, out long value)
        {
            value = 0;

            var rows = (uint)this.Image.RowCount(MetadataTable.Constant);

            if (rows == 0)
                return false;

            var parent = this.Image.EncodeCoded(CodedIndex.HasConstant, table, rowId);

            // Constant: Type, Padding, Parent, Value. Sorted by parent.
            for (var row = 1u; row <= rows; row++)
            {
                if (this.Image.ReadColumn(MetadataTable.Constant, row, 2) != parent)
                    continue;

                var element = (CorElementType)this.Image.ReadColumn(MetadataTable.Constant, row, 0);
                var blob = this.Image.Blob(this.Image.ReadColumn(MetadataTable.Constant, row, 3));

                switch (element)
                {
                    case CorElementType.BOOLEAN:
                    case CorElementType.U1: value = (long)blob.ReadFixed(1); return true;
                    case CorElementType.I1: value = (sbyte)blob.ReadFixed(1); return true;
                    case CorElementType.CHAR:
                    case CorElementType.U2: value = (long)blob.ReadFixed(2); return true;
                    case CorElementType.I2: value = (short)blob.ReadFixed(2); return true;
                    case CorElementType.U4: value = (long)(uint)blob.ReadFixed(4); return true;
                    case CorElementType.I4: value = (int)blob.ReadFixed(4); return true;
                    case CorElementType.U8:
                    case CorElementType.I8: value = (long)blob.ReadFixed(8); return true;
                    default: return false;
                }
            }

            return false;
        }

        /// <summary>
        /// The Field rows a TypeDef owns, from its FieldList and the next type's.
        /// </summary>
        /// <remarks>
        /// A type's members are a run in the member table rather than a list, so the run ends
        /// where the next type's begins - and for the last type, at the end of the table.
        /// </remarks>
        private IEnumerable<uint> FieldRowsOf(uint typeRow)
        {
            var types = (uint)this.Image.RowCount(MetadataTable.TypeDef);
            var fields = (uint)this.Image.RowCount(MetadataTable.Field);

            var first = this.Image.ReadColumn(MetadataTable.TypeDef, typeRow, 4);
            var last = typeRow < types
                ? this.Image.ReadColumn(MetadataTable.TypeDef, typeRow + 1, 4)
                : fields + 1;

            for (var field = first; field < last && field <= fields; field++)
                yield return field;
        }

        /// <summary>
        /// Follows a TypeRef to the TypeDef that defines it, in whichever assembly that is.
        /// </summary>
        /// <remarks>
        /// The resolution scope names an AssemblyRef, and the loader keeps a map from that row to
        /// the assembly it bound to - so this is a lookup rather than a name search, and gives
        /// the assembly actually loaded rather than one that merely matches the name. A nested
        /// type's scope is another TypeRef, and is followed by name once the outer one resolves.
        /// </remarks>
        private (ClrModuleMetadata Metadata, uint RowId)? FollowTypeRef(uint rowId)
        {
            if (rowId == 0 || rowId > (uint)this.Image.RowCount(MetadataTable.TypeRef))
                return null;

            var name = this.TypeRefName(rowId);

            if (name == null)
                return null;

            // A TypeRef can name a type this very module defines, in which case no hop is needed.
            if (this.TryFindType(name, out var here) && here.Table == MetadataTable.TypeDef)
                return (this, here.RowId);

            if (this.Owner == null)
                return null;

            var scope = this.Image.DecodeCoded(
                CodedIndex.ResolutionScope,
                this.Image.ReadColumn(MetadataTable.TypeRef, rowId, 0));

            if (scope.Table != MetadataTable.AssemblyRef)
                return null;

            var assemblyRefToken = ((uint)MetadataTable.AssemblyRef << 24) | scope.RowId;
            var assembly = this.Owner.AssemblyRefToAssembly(assemblyRefToken);

            if (assembly == IntPtr.Zero)
                return null;

            var manifest = ClrAssembly.At(assembly)?.ManifestModule;

            if (manifest == null || manifest.Base == IntPtr.Zero)
                return null;

            var metadata = Of(manifest);
            var token = metadata.FindTypeDef(name);

            return token == 0 ? null : (metadata, token & RowIdMask);
        }

        /// <summary>A readable name for a type named by a TypeDef or TypeRef row.</summary>
        private string TypeNameOf(MetadataTable table, uint rowId)
        {
            switch (table)
            {
                case MetadataTable.TypeDef: return this.FullTypeNameOfRow(rowId);
                case MetadataTable.TypeRef: return this.TypeRefName(rowId);
                default: return null;
            }
        }

        private bool TryRow(uint token, MetadataTable expected, out uint rowId)
        {
            rowId = token & RowIdMask;

            if ((MetadataTable)(token >> 24) != expected)
                return false;

            return rowId != 0 && rowId <= (uint)this.Image.RowCount(expected);
        }

        private string TypeRefName(uint rowId)
        {
            if (rowId == 0 || rowId > (uint)this.Image.RowCount(MetadataTable.TypeRef))
                return null;

            // TypeRef: ResolutionScope, Name, Namespace.
            var name = this.Image.String(this.Image.ReadColumn(MetadataTable.TypeRef, rowId, 1));
            var space = this.Image.String(this.Image.ReadColumn(MetadataTable.TypeRef, rowId, 2));

            return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
        }

        private string FullTypeNameOfRow(uint rowId)
        {
            if (rowId == 0 || rowId > (uint)this.Image.RowCount(MetadataTable.TypeDef))
                return null;

            var name = this.Image.String(this.Image.ReadColumn(MetadataTable.TypeDef, rowId, 1));

            // A nested type's own row carries only the short name; the enclosing type is a
            // separate row that has to be walked to, via the NestedClass table.
            var enclosing = this.EnclosingTypeOf(rowId);
            if (enclosing != 0)
                return $"{this.FullTypeNameOfRow(enclosing)}+{name}";

            var space = this.Image.String(this.Image.ReadColumn(MetadataTable.TypeDef, rowId, 2));

            return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
        }

        /// <summary>
        /// The TypeDef row that encloses <paramref name="rowId"/>, or zero when it is top level.
        /// </summary>
        /// <remarks>
        /// Nesting is recorded the other way round from how it is asked about: the NestedClass
        /// table lists nested-to-enclosing pairs, with no index from a type to its own row. The
        /// whole table is small, so it is turned into a lookup once on first use.
        /// </remarks>
        private uint EnclosingTypeOf(uint rowId)
        {
            var map = this.enclosingTypes;

            if (map == null)
            {
                var count = this.Image.RowCount(MetadataTable.NestedClass);
                map = new Dictionary<uint, uint>(count);

                for (var row = 1u; row <= (uint)count; row++)
                {
                    // NestedClass: NestedClass, EnclosingClass - both TypeDef row ids.
                    map[this.Image.ReadColumn(MetadataTable.NestedClass, row, 0)] =
                        this.Image.ReadColumn(MetadataTable.NestedClass, row, 1);
                }

                this.enclosingTypes = map;
            }

            return map.TryGetValue(rowId, out var enclosing) ? enclosing : 0;
        }

        /// <summary>
        /// The TypeDef that owns a method or field row.
        /// </summary>
        /// <remarks>
        /// ECMA-335 stores this the same way it stores every one-to-many relationship: each
        /// TypeDef holds the first row of a run that ends where the next type's run begins. So
        /// the owner of a row is the last type whose run starts at or before it - and because
        /// those starts only ever increase, that is a binary search rather than a scan.
        /// </remarks>
        private uint DeclaringTypeOf(MetadataTable table, uint rowId, int listColumn)
        {
            var typeCount = (uint)this.Image.RowCount(MetadataTable.TypeDef);
            if (typeCount == 0)
                return 0;

            uint low = 1;
            var high = typeCount;
            uint owner = 0;

            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var start = this.Image.ReadColumn(MetadataTable.TypeDef, middle, listColumn);

                if (start <= rowId)
                {
                    owner = middle;
                    low = middle + 1;
                }
                else
                {
                    if (middle == 1)
                        break;

                    high = middle - 1;
                }
            }

            return owner;
        }

        /// <summary>
        /// One of a mapped image's PE data directories, as a relative virtual address and a
        /// size, or (0, 0) when the image does not have it.
        /// </summary>
        /// <remarks>
        /// The metadata reader needs the CLI directory and the symbol reader needs the debug
        /// one, and both are the same walk: the DOS stub points at the PE signature, the COFF
        /// header follows, and the optional header after it holds the directories - at one of
        /// two offsets, depending on whether the image is 32- or 64-bit.
        /// </remarks>
        internal static (uint Rva, uint Size) DataDirectory(IntPtr imageBase, int index)
        {
            if (imageBase == IntPtr.Zero)
                return (0, 0);

            var image = (byte*)imageBase;

            var peOffset = *(int*)(image + 0x3C);
            var optionalHeader = image + peOffset + 4 + 20;

            var magic = *(ushort*)optionalHeader;
            var directories = optionalHeader
                              + (magic == Pe32PlusMagic ? DataDirectoriesOffsetPe32Plus : DataDirectoriesOffsetPe32);

            // The count is in the optional header too, so a directory beyond it is not there.
            var count = *(uint*)(directories - 4);

            if (index >= (int)count)
                return (0, 0);

            return (*(uint*)(directories + index * 8), *(uint*)(directories + index * 8 + 4));
        }

        private static ClrModuleMetadata Read(IntPtr imageBase)
        {
            var image = (byte*)imageBase;

            // PE headers: the DOS stub points at the PE signature, which is followed by the COFF
            // header and then the optional header holding the data directories.
            var peOffset = *(int*)(image + 0x3C);
            var optionalHeader = image + peOffset + 4 + 20;

            var magic = *(ushort*)optionalHeader;
            var directories = optionalHeader
                              + (magic == Pe32PlusMagic ? DataDirectoriesOffsetPe32Plus : DataDirectoriesOffsetPe32);

            var cliRva = *(uint*)(directories + CliDirectoryIndex * 8);
            if (cliRva == 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The image at 0x{imageBase.ToInt64():x} has no CLI header, so it carries no " +
                    "managed metadata.");

            // A mapped image places every section at its virtual address, so an RVA is just an
            // offset from the base - no section table walk needed.
            var cliHeader = image + cliRva;
            var metadataRva = *(uint*)(cliHeader + Cor20MetadataDirectoryOffset);
            var metadataSize = *(uint*)(cliHeader + Cor20MetadataDirectoryOffset + 4);

            if (metadataRva == 0 || metadataSize == 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The CLI header of the image at 0x{imageBase.ToInt64():x} names no metadata.");

            return new ClrModuleMetadata(imageBase, (IntPtr)(image + metadataRva), (int)metadataSize);
        }

        public override string ToString()
        {
            return $"metadata @0x{this.MetadataAddress.ToInt64():x} size={this.MetadataSize} " +
                   $"version={this.Image.Version}";
        }
    }
}