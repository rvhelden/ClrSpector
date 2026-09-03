using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
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

        private static ClrModuleMetadata coreLib;

        /// <summary>The table byte of a user string token, which names the <c>#US</c> heap.</summary>
        private const int UserStringTokenType = 0x70;

        /// <summary>The calling convention byte that marks a signature as an instantiation.</summary>
        private const byte GenericInstantiationConvention = 0x0A;

        /// <summary>
        /// A stop on a generic argument count read from a blob, past which the blob is not being
        /// read as a MethodSpec signature at all.
        /// </summary>
        private const uint MaximumGenericArguments = 1024;

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

            // Null for a dynamic module, which has no mapped image to read metadata out of.
            if (metadata == null)
                return null;

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

        /// <summary>
        /// The metadata of CoreLib, reached without the loader's reference maps and without a
        /// <see cref="Type"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The descriptor publishes the MethodTable of <c>System.Object</c> as a global, and a
        /// MethodTable knows its module - so CoreLib is reachable from the contract descriptor
        /// alone. That matters because the alternative routes to another assembly all go through
        /// maps the runtime fills in lazily: an assembly reference that nothing has needed yet has
        /// no entry, so resolution through it succeeds or fails depending on what the process
        /// happens to have done first.
        /// </para>
        /// <para>
        /// Almost every enum used in an attribute is defined here, which makes this the last
        /// resort that actually resolves things - and it resolves them by finding the real TypeDef
        /// and reading its real field, not by assuming anything.
        /// </para>
        /// </remarks>
        public static ClrModuleMetadata CoreLib
        {
            get
            {
                if (coreLib != null)
                    return coreLib;

                try
                {
                    var handle = ContractDescriptor.Current.Globals.Dereference("ObjectMethodTable");

                    if (handle == IntPtr.Zero || !ClrMethodTable.IsMethodTableHandle(handle))
                        return null;

                    var module = ClrMethodTable.Create(new MemoryReader(handle)).Module;

                    if (module == IntPtr.Zero)
                        return null;

                    return coreLib = Of(ClrModule.At(module));
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

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
                                : parent.Table == MetadataTable.TypeSpec
                                    // A constructed type has no name, only a signature - which
                                    // is how a call on a generic parameter is written, and what
                                    // makes "TypeSpec" the wrong thing to print.
                                    ? this.TypeSpecName(parent.RowId)
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
                        return this.MethodSpecName(rowId);

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

        /// <summary>The type a TypeSpec row's signature constructs.</summary>
        private string TypeSpecName(uint rowId)
        {
            if (rowId == 0 || rowId > (uint)this.Image.RowCount(MetadataTable.TypeSpec))
                return MetadataTable.TypeSpec.ToString();

            var blob = this.Image.Blob(this.Image.ReadColumn(MetadataTable.TypeSpec, rowId, 0));

            return SignatureTypeReader.ReadType(ref blob, this.Image).ToString();
        }

        /// <summary>
        /// A generic parameter as it was declared: its name, and what it was constrained to.
        /// </summary>
        /// <remarks>
        /// Constraints are the clearest case of something that is metadata rather than code.
        /// They change what the compiler may emit - <c>new()</c> is why <c>new T()</c> becomes a
        /// call to <see cref="System.Activator"/>, and an interface constraint is why a call on
        /// a type parameter can be a constrained call at all - but there is no instruction
        /// anywhere in the body that says so.
        /// </remarks>
        public sealed class ClrGenericParameter
        {
            internal ClrGenericParameter(string name, ushort flags, IReadOnlyList<string> constraints)
            {
                this.Name = name;
                this.Flags = flags;
                this.Constraints = constraints;
            }

            /// <summary>The name the source gave it.</summary>
            public string Name { get; }

            /// <summary>GenericParamAttributes: the variance and the special constraints.</summary>
            public ushort Flags { get; }

            /// <summary>The types it was constrained to, as their names.</summary>
            public IReadOnlyList<string> Constraints { get; }

            /// <summary>True for <c>where T : class</c>.</summary>
            public bool IsReferenceType => (this.Flags & 0x0004) != 0;

            /// <summary>True for <c>where T : struct</c>.</summary>
            public bool IsValueType => (this.Flags & 0x0008) != 0;

            /// <summary>True for <c>where T : new()</c>.</summary>
            public bool HasDefaultConstructor => (this.Flags & 0x0010) != 0;

            public override string ToString()
            {
                var parts = new List<string>();

                if (this.IsReferenceType)
                    parts.Add("class");

                if (this.IsValueType)
                    parts.Add("struct");

                parts.AddRange(this.Constraints);

                if (this.HasDefaultConstructor)
                    parts.Add("new()");

                return parts.Count == 0 ? this.Name : $"{this.Name} : {string.Join(", ", parts)}";
            }
        }

        /// <summary>
        /// The generic parameters a type or a method declared, in order, with their constraints.
        /// </summary>
        public IReadOnlyList<ClrGenericParameter> GenericParameters(int ownerToken)
        {
            var parameters = new List<ClrGenericParameter>();

            try
            {
                var table = (MetadataTable)(uint)((ownerToken >> 24) & 0xFF);
                var rowId = (uint)ownerToken & RowIdMask;
                var tag = Array.IndexOf(MetadataSchema.TablesOf(CodedIndex.TypeOrMethodDef), (int)table);

                if (tag < 0 || rowId == 0)
                    return parameters;

                var key = (rowId << MetadataSchema.TagBitsOf(CodedIndex.TypeOrMethodDef)) | (uint)tag;
                var found = new List<(int Number, uint Row, string Name, ushort Flags)>();

                // GenericParam: Number, Flags, Owner, Name.
                for (var row = 1u; row <= (uint)this.Image.RowCount(MetadataTable.GenericParam); row++)
                {
                    if (this.Image.ReadColumn(MetadataTable.GenericParam, row, 2) != key)
                        continue;

                    found.Add((
                        (int)this.Image.ReadColumn(MetadataTable.GenericParam, row, 0),
                        row,
                        this.Image.String(this.Image.ReadColumn(MetadataTable.GenericParam, row, 3)),
                        (ushort)this.Image.ReadColumn(MetadataTable.GenericParam, row, 1)));
                }

                foreach (var parameter in found.OrderBy(item => item.Number))
                {
                    parameters.Add(new ClrGenericParameter(
                        parameter.Name, parameter.Flags, this.ConstraintsOf(parameter.Row)));
                }
            }
            catch (Exception)
            {
                // A signature reads without them; it just does not say what it required.
            }

            return parameters;
        }

        /// <summary>The types a GenericParam row is constrained to.</summary>
        private IReadOnlyList<string> ConstraintsOf(uint parameterRow)
        {
            var constraints = new List<string>();

            // GenericParamConstraint: Owner (a GenericParam row), Constraint (a TypeDefOrRef).
            for (var row = 1u; row <= (uint)this.Image.RowCount(MetadataTable.GenericParamConstraint); row++)
            {
                if (this.Image.ReadColumn(MetadataTable.GenericParamConstraint, row, 0) != parameterRow)
                    continue;

                var constraint = this.Image.DecodeCoded(
                    CodedIndex.TypeDefOrRef,
                    this.Image.ReadColumn(MetadataTable.GenericParamConstraint, row, 1));

                var name = this.TokenName(((int)constraint.Table << 24) | (int)constraint.RowId);

                if (!string.IsNullOrEmpty(name))
                    constraints.Add(name);
            }

            return constraints;
        }

        /// <summary>
        /// The names a type or a method gave its generic parameters, in order.
        /// </summary>
        /// <remarks>
        /// A signature refers to them by position - <c>!0</c> for a type's, <c>!!0</c> for a
        /// method's - because that is all a signature holds. The names are in the GenericParam
        /// table, which is the only place <c>T</c> exists.
        /// </remarks>
        public IReadOnlyList<string> GenericParameterNames(int ownerToken)
        {
            var names = new List<string>();

            try
            {
                var table = (MetadataTable)(uint)((ownerToken >> 24) & 0xFF);
                var rowId = (uint)ownerToken & RowIdMask;
                var tag = Array.IndexOf(MetadataSchema.TablesOf(CodedIndex.TypeOrMethodDef), (int)table);

                if (tag < 0 || rowId == 0)
                    return names;

                var key = (rowId << MetadataSchema.TagBitsOf(CodedIndex.TypeOrMethodDef)) | (uint)tag;
                var found = new List<(int Number, string Name)>();

                // GenericParam: Number, Flags, Owner, Name.
                for (var row = 1u; row <= (uint)this.Image.RowCount(MetadataTable.GenericParam); row++)
                {
                    if (this.Image.ReadColumn(MetadataTable.GenericParam, row, 2) != key)
                        continue;

                    found.Add((
                        (int)this.Image.ReadColumn(MetadataTable.GenericParam, row, 0),
                        this.Image.String(this.Image.ReadColumn(MetadataTable.GenericParam, row, 3))));
                }

                foreach (var parameter in found.OrderBy(item => item.Number))
                    names.Add(parameter.Name);
            }
            catch (Exception)
            {
                // Without the names, a signature still reads - by position.
            }

            return names;
        }

        /// <summary>
        /// The name of a generic method instantiation: the method's own name, with the types it
        /// was instantiated with.
        /// </summary>
        /// <remarks>
        /// A MethodSpec has no name of its own - only a reference to the generic method and a
        /// blob of the type arguments - so both halves have to be read to say anything about it.
        /// This is what a call to a generic method looks like in IL, so leaving it unresolved
        /// leaves a hole in every listing that has one.
        /// </remarks>
        private string MethodSpecName(uint rowId)
        {
            if (rowId == 0 || rowId > (uint)this.Image.RowCount(MetadataTable.MethodSpec))
                return null;

            // MethodSpec: Method (a MethodDefOrRef), Instantiation (a blob).
            var method = this.Image.DecodeCoded(
                CodedIndex.MethodDefOrRef,
                this.Image.ReadColumn(MetadataTable.MethodSpec, rowId, 0));

            var name = this.TokenName(((int)method.Table << 24) | (int)method.RowId);
            var blob = this.Image.Blob(this.Image.ReadColumn(MetadataTable.MethodSpec, rowId, 1));

            // A MethodSpec signature is GENERICINST, an argument count, then the arguments.
            if (blob.Remaining < 2 || blob.ReadByte() != GenericInstantiationConvention)
                return name;

            var count = blob.ReadCompressedUInt();

            if (count == 0 || count > MaximumGenericArguments)
                return name;

            var arguments = new List<string>((int)count);

            for (var i = 0u; i < count; i++)
                arguments.Add(SignatureTypeReader.ReadType(ref blob, this.Image).ToString());

            return $"{name}<{string.Join(", ", arguments)}>";
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
                // The nested-aware name, so a nested TypeRef indexes under the same spelling a
                // nested TypeDef does rather than under its bare short name.
                var name = this.TypeRefFullName(row);

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

                    if (target != null)
                    {
                        return target.Value.Metadata.EnumUnderlyingType(
                            MetadataTable.TypeDef, target.Value.RowId, depth + 1);
                    }

                    // Metadata could not reach it, which happens when the runtime has not bound
                    // that assembly reference yet. If the type happens to be loaded anyway, its
                    // MethodTable answers the same question without any metadata at all.
                    var loaded = this.UnderlyingFromLoadedTypeRef(rowId);

                    if (loaded != CorElementType.END)
                        return loaded;

                    // Neither route depended on anything but luck about what the process had
                    // already touched. CoreLib does not, and is where nearly every enum used in
                    // an attribute is defined.
                    return UnderlyingFromCoreLib(this.TypeRefFullName(rowId), depth);
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
        /// An enum's underlying type looked up by name in CoreLib.
        /// </summary>
        private static CorElementType UnderlyingFromCoreLib(string name, int depth)
        {
            if (name == null || depth > MaximumResolutionHops)
                return CorElementType.END;

            var coreLibMetadata = CoreLib;

            if (coreLibMetadata == null)
                return CorElementType.END;

            var token = coreLibMetadata.FindTypeDef(name);

            return token == 0
                ? CorElementType.END
                : coreLibMetadata.EnumUnderlyingType(
                    MetadataTable.TypeDef, token & RowIdMask, depth + 1);
        }

        /// <summary>
        /// An enum's underlying type taken from its loaded MethodTable rather than from metadata.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The last resort, and the one that needs no name resolution at all. An enum's storage is
        /// a single instance field, and a FieldDesc records its element type directly - so if the
        /// runtime has built a MethodTable for the referenced type, the answer is one lookup away.
        /// </para>
        /// <para>
        /// This is not redundant with the metadata route, it is complementary: metadata works for
        /// a type that is declared but never loaded, and this works for a type that is loaded
        /// through a reference the loader has not written into its AssemblyRef map. Both are
        /// populated lazily, in different orders, so an enum that neither can reach is one whose
        /// width genuinely is not recorded anywhere in the process.
        /// </para>
        /// </remarks>
        private CorElementType UnderlyingFromLoadedTypeRef(uint rowId)
        {
            if (this.Owner == null)
                return CorElementType.END;

            var token = ((uint)MetadataTable.TypeRef << 24) | rowId;
            var handle = this.Owner.TypeRefToMethodTable(token);

            if (handle == IntPtr.Zero || !ClrMethodTable.IsMethodTableHandle(handle))
                return CorElementType.END;

            var table = ClrMethodTable.Create(new MemoryReader(handle));

            foreach (var field in table.Fields)
            {
                if (!field.IsStatic)
                    return field.ElementType;
            }

            return CorElementType.END;
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
                    case CorElementType.U1:
                        value = (long)blob.ReadFixed(1);
                        return true;
                    case CorElementType.I1:
                        value = (sbyte)blob.ReadFixed(1);
                        return true;
                    case CorElementType.CHAR:
                    case CorElementType.U2:
                        value = (long)blob.ReadFixed(2);
                        return true;
                    case CorElementType.I2:
                        value = (short)blob.ReadFixed(2);
                        return true;
                    case CorElementType.U4:
                        value = (long)(uint)blob.ReadFixed(4);
                        return true;
                    case CorElementType.I4:
                        value = (int)blob.ReadFixed(4);
                        return true;
                    case CorElementType.U8:
                    case CorElementType.I8:
                        value = (long)blob.ReadFixed(8);
                        return true;
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
        private (ClrModuleMetadata Metadata, uint RowId)? FollowTypeRef(uint rowId, int depth = 0)
        {
            if (rowId == 0
                || rowId > (uint)this.Image.RowCount(MetadataTable.TypeRef)
                || depth > MaximumResolutionHops)
            {
                return null;
            }

            var name = this.TypeRefFullName(rowId);

            if (name == null)
                return null;

            // A TypeRef can name a type this very module defines, in which case no hop is needed.
            if (this.TryFindType(name, out var here) && here.Table == MetadataTable.TypeDef)
                return (this, here.RowId);

            var scope = this.Image.DecodeCoded(
                CodedIndex.ResolutionScope,
                this.Image.ReadColumn(MetadataTable.TypeRef, rowId, 0));

            // A nested type's scope is the TypeRef of the type that encloses it, so the enclosing
            // one has to be resolved first - and the nested type is then found by name inside
            // whichever assembly that landed in.
            if (scope.Table == MetadataTable.TypeRef)
            {
                var enclosing = this.FollowTypeRef(scope.RowId, depth + 1);

                if (enclosing == null)
                    return null;

                var nested = enclosing.Value.Metadata.FindTypeDef(name);

                return nested == 0 ? null : (enclosing.Value.Metadata, nested & RowIdMask);
            }

            if (scope.Table != MetadataTable.AssemblyRef || this.Owner == null)
                return null;

            return this.FollowIntoAssemblyRef(scope.RowId, name, depth);
        }

        /// <summary>
        /// Finds <paramref name="name"/> in the assembly an AssemblyRef row bound to, following a
        /// type forwarder if that is all the assembly holds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The assembly comes from the loader's own AssemblyRef map rather than from a name
        /// search, so this reaches the assembly that was actually loaded.
        /// </para>
        /// <para>
        /// The forwarding step is the one that is easy to leave out and expensive to leave out.
        /// A reference assembly like <c>System.Runtime</c> defines almost nothing: its manifest is
        /// mostly ExportedType rows saying "this name really lives over there". A TypeRef into it
        /// therefore finds no TypeDef, and a resolver that stopped at that point would conclude
        /// the type is unreachable - which for an enum means falling back to guessing its width.
        /// Measured: every cross-assembly enum in an attribute went through a forwarder, so
        /// without this step none of them resolved.
        /// </para>
        /// </remarks>
        private (ClrModuleMetadata Metadata, uint RowId)? FollowIntoAssemblyRef(
            uint assemblyRefRow, string name, int depth)
        {
            if (depth > MaximumResolutionHops)
                return null;

            var token = ((uint)MetadataTable.AssemblyRef << 24) | assemblyRefRow;

            // The map holds the referenced Module, not its Assembly - see
            // ClrModule.AssemblyRefToModule for why reading it the other way fails silently.
            var referenced = this.Owner.AssemblyRefToModule(token);

            if (referenced == IntPtr.Zero)
                return null;

            var module = ClrModule.At(referenced);

            if (module == null || module.Base == IntPtr.Zero)
                return null;

            var metadata = Of(module);

            if (metadata == null)
                return null;

            var found = metadata.FindTypeDef(name);

            if (found != 0)
                return (metadata, found & RowIdMask);

            return metadata.FollowExportedType(name, depth + 1);
        }

        /// <summary>
        /// Follows this module's ExportedType rows for <paramref name="name"/>, which is how a
        /// facade assembly says the type is really defined somewhere else.
        /// </summary>
        /// <remarks>
        /// An ExportedType whose Implementation is an AssemblyRef is a type forwarder. One whose
        /// Implementation is another ExportedType is a nested type inside a forwarded one, and is
        /// resolved by forwarding the enclosing name and then looking the nested one up there.
        /// </remarks>
        private (ClrModuleMetadata Metadata, uint RowId)? FollowExportedType(string name, int depth)
        {
            if (depth > MaximumResolutionHops || this.Owner == null)
                return null;

            var rows = (uint)this.Image.RowCount(MetadataTable.ExportedType);

            for (var row = 1u; row <= rows; row++)
            {
                if (this.ExportedTypeFullName(row) != name)
                    continue;

                // ExportedType: Flags, TypeDefId, TypeName, TypeNamespace, Implementation.
                var implementation = this.Image.DecodeCoded(
                    CodedIndex.Implementation,
                    this.Image.ReadColumn(MetadataTable.ExportedType, row, 4));

                switch (implementation.Table)
                {
                    case MetadataTable.AssemblyRef:
                        return this.FollowIntoAssemblyRef(implementation.RowId, name, depth);

                    case MetadataTable.ExportedType:
                    {
                        // Nested: forward the enclosing name, then find this one inside it.
                        var enclosing = this.ExportedTypeFullName(implementation.RowId);
                        var target = enclosing == null
                            ? null
                            : this.FollowExportedType(enclosing, depth + 1);

                        if (target == null)
                            return null;

                        var nested = target.Value.Metadata.FindTypeDef(name);

                        return nested == 0 ? null : (target.Value.Metadata, nested & RowIdMask);
                    }

                    default:
                        return null;
                }
            }

            return null;
        }

        /// <summary>
        /// The full name an ExportedType row carries, nested rows joined with <c>+</c> the way
        /// <see cref="FullTypeName"/> spells a nested TypeDef.
        /// </summary>
        private string ExportedTypeFullName(uint rowId)
        {
            if (rowId == 0 || rowId > (uint)this.Image.RowCount(MetadataTable.ExportedType))
                return null;

            var name = this.Image.String(
                this.Image.ReadColumn(MetadataTable.ExportedType, rowId, 2));
            var space = this.Image.String(
                this.Image.ReadColumn(MetadataTable.ExportedType, rowId, 3));

            var full = string.IsNullOrEmpty(space) ? name : $"{space}.{name}";

            var implementation = this.Image.DecodeCoded(
                CodedIndex.Implementation,
                this.Image.ReadColumn(MetadataTable.ExportedType, rowId, 4));

            if (implementation.Table != MetadataTable.ExportedType || implementation.RowId == rowId)
                return full;

            var enclosing = this.ExportedTypeFullName(implementation.RowId);

            return enclosing == null ? full : $"{enclosing}+{full}";
        }

        /// <summary>
        /// The full name a TypeRef names, with a nested type joined to its enclosing one by
        /// <c>+</c> so it matches how a TypeDef's name is spelled.
        /// </summary>
        /// <remarks>
        /// A nested type's TypeRef carries only its own short name and points at its enclosing
        /// type's TypeRef for the rest, so the name has to be rebuilt by walking that chain -
        /// <c>DebuggingModes</c> alone matches no TypeDef, while
        /// <c>System.Diagnostics.DebuggableAttribute+DebuggingModes</c> does.
        /// </remarks>
        private string TypeRefFullName(uint rowId, int depth = 0)
        {
            if (rowId == 0
                || rowId > (uint)this.Image.RowCount(MetadataTable.TypeRef)
                || depth > MaximumResolutionHops)
            {
                return null;
            }

            var name = this.TypeRefName(rowId);

            if (name == null)
                return null;

            var scope = this.Image.DecodeCoded(
                CodedIndex.ResolutionScope,
                this.Image.ReadColumn(MetadataTable.TypeRef, rowId, 0));

            if (scope.Table != MetadataTable.TypeRef || scope.RowId == rowId)
                return name;

            var enclosing = this.TypeRefFullName(scope.RowId, depth + 1);

            return enclosing == null ? name : $"{enclosing}+{name}";
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