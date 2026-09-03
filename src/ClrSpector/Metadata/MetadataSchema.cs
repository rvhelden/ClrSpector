using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>What a metadata column holds, which is what decides how wide it is.</summary>
    internal enum ColumnKind : byte
    {
        Byte,
        UInt16,
        UInt32,

        /// <summary>An index into the string heap.</summary>
        String,

        /// <summary>An index into the blob heap.</summary>
        Blob,

        /// <summary>An index into the GUID heap.</summary>
        Guid,

        /// <summary>A row id in one specific table.</summary>
        Table,

        /// <summary>A row id in one of several tables, with the table in the low bits.</summary>
        Coded
    }

    /// <summary>
    /// The coded index kinds of ECMA-335 II.24.2.6 - a row id that can name one of several
    /// tables, with a tag in its low bits saying which.
    /// </summary>
    public enum CodedIndex : byte
    {
        TypeDefOrRef,
        HasConstant,
        HasCustomAttribute,
        HasFieldMarshal,
        HasDeclSecurity,
        MemberRefParent,
        HasSemantics,
        MethodDefOrRef,
        MemberForwarded,
        Implementation,
        CustomAttributeType,
        ResolutionScope,
        TypeOrMethodDef
    }

    internal readonly struct Column
    {
        private Column(ColumnKind kind, MetadataTable table, CodedIndex coded)
        {
            this.Kind = kind;
            this.Table = table;
            this.Coded = coded;
        }

        public ColumnKind Kind { get; }

        public MetadataTable Table { get; }

        public CodedIndex Coded { get; }

        public static Column U1() => new Column(ColumnKind.Byte, default, default);

        public static Column U2() => new Column(ColumnKind.UInt16, default, default);

        public static Column U4() => new Column(ColumnKind.UInt32, default, default);

        public static Column Str() => new Column(ColumnKind.String, default, default);

        public static Column Blb() => new Column(ColumnKind.Blob, default, default);

        public static Column Gid() => new Column(ColumnKind.Guid, default, default);

        public static Column Tbl(MetadataTable table) => new Column(ColumnKind.Table, table, default);

        public static Column Cod(CodedIndex coded) => new Column(ColumnKind.Coded, default, coded);
    }

    /// <summary>
    /// The column layout of every metadata table, and of every coded index.
    /// </summary>
    /// <remarks>
    /// Straight from ECMA-335 II.22 and II.24.2.6. All of it is here, not just the handful of
    /// tables a signature needs, because a table's rows can only be found by measuring every
    /// table before it - see <see cref="MetadataImage"/>.
    ///
    /// Column *order* matters as much as the set: it is what fixes each column's offset within a
    /// row. Coded index tag *order* matters for the same reason - the tag is an index into the
    /// list, so reordering one silently redirects it to a different table.
    /// </remarks>
    internal static class MetadataSchema
    {
        private static readonly int[] CodedTagBits =
        {
            2, // TypeDefOrRef
            2, // HasConstant
            5, // HasCustomAttribute
            1, // HasFieldMarshal
            2, // HasDeclSecurity
            3, // MemberRefParent
            1, // HasSemantics
            1, // MethodDefOrRef
            1, // MemberForwarded
            2, // Implementation
            3, // CustomAttributeType
            2, // ResolutionScope
            1  // TypeOrMethodDef
        };

        private static readonly Column[][] Tables = BuildTables();

        private static readonly int[][] CodedTables = BuildCodedTables();

        public static Column[] Of(MetadataTable table)
        {
            var index = (int)table;

            if (index >= Tables.Length || Tables[index] == null)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Metadata table {table} (0x{index:x2}) has no known column layout.");

            return Tables[index];
        }

        public static int[] TablesOf(CodedIndex coded) => CodedTables[(int)coded];

        public static int TagBitsOf(CodedIndex coded) => CodedTagBits[(int)coded];

        private static Column[][] BuildTables()
        {
            var tables = new Column[(int)MetadataTable.GenericParamConstraint + 1][];

            tables[(int)MetadataTable.Module] = new[]
            {
                Column.U2(), Column.Str(), Column.Gid(), Column.Gid(), Column.Gid()
            };

            tables[(int)MetadataTable.TypeRef] = new[]
            {
                Column.Cod(CodedIndex.ResolutionScope), Column.Str(), Column.Str()
            };

            tables[(int)MetadataTable.TypeDef] = new[]
            {
                Column.U4(), Column.Str(), Column.Str(), Column.Cod(CodedIndex.TypeDefOrRef),
                Column.Tbl(MetadataTable.Field), Column.Tbl(MetadataTable.MethodDef)
            };

            tables[(int)MetadataTable.FieldPtr] = new[] { Column.Tbl(MetadataTable.Field) };

            tables[(int)MetadataTable.Field] = new[] { Column.U2(), Column.Str(), Column.Blb() };

            tables[(int)MetadataTable.MethodPtr] = new[] { Column.Tbl(MetadataTable.MethodDef) };

            tables[(int)MetadataTable.MethodDef] = new[]
            {
                Column.U4(), Column.U2(), Column.U2(), Column.Str(), Column.Blb(),
                Column.Tbl(MetadataTable.Param)
            };

            tables[(int)MetadataTable.ParamPtr] = new[] { Column.Tbl(MetadataTable.Param) };

            tables[(int)MetadataTable.Param] = new[] { Column.U2(), Column.U2(), Column.Str() };

            tables[(int)MetadataTable.InterfaceImpl] = new[]
            {
                Column.Tbl(MetadataTable.TypeDef), Column.Cod(CodedIndex.TypeDefOrRef)
            };

            tables[(int)MetadataTable.MemberRef] = new[]
            {
                Column.Cod(CodedIndex.MemberRefParent), Column.Str(), Column.Blb()
            };

            tables[(int)MetadataTable.Constant] = new[]
            {
                Column.U1(), Column.U1(), Column.Cod(CodedIndex.HasConstant), Column.Blb()
            };

            tables[(int)MetadataTable.CustomAttribute] = new[]
            {
                Column.Cod(CodedIndex.HasCustomAttribute), Column.Cod(CodedIndex.CustomAttributeType),
                Column.Blb()
            };

            tables[(int)MetadataTable.FieldMarshal] = new[]
            {
                Column.Cod(CodedIndex.HasFieldMarshal), Column.Blb()
            };

            tables[(int)MetadataTable.DeclSecurity] = new[]
            {
                Column.U2(), Column.Cod(CodedIndex.HasDeclSecurity), Column.Blb()
            };

            tables[(int)MetadataTable.ClassLayout] = new[]
            {
                Column.U2(), Column.U4(), Column.Tbl(MetadataTable.TypeDef)
            };

            tables[(int)MetadataTable.FieldLayout] = new[]
            {
                Column.U4(), Column.Tbl(MetadataTable.Field)
            };

            tables[(int)MetadataTable.StandAloneSig] = new[] { Column.Blb() };

            tables[(int)MetadataTable.EventMap] = new[]
            {
                Column.Tbl(MetadataTable.TypeDef), Column.Tbl(MetadataTable.Event)
            };

            tables[(int)MetadataTable.EventPtr] = new[] { Column.Tbl(MetadataTable.Event) };

            tables[(int)MetadataTable.Event] = new[]
            {
                Column.U2(), Column.Str(), Column.Cod(CodedIndex.TypeDefOrRef)
            };

            tables[(int)MetadataTable.PropertyMap] = new[]
            {
                Column.Tbl(MetadataTable.TypeDef), Column.Tbl(MetadataTable.Property)
            };

            tables[(int)MetadataTable.PropertyPtr] = new[] { Column.Tbl(MetadataTable.Property) };

            tables[(int)MetadataTable.Property] = new[] { Column.U2(), Column.Str(), Column.Blb() };

            tables[(int)MetadataTable.MethodSemantics] = new[]
            {
                Column.U2(), Column.Tbl(MetadataTable.MethodDef), Column.Cod(CodedIndex.HasSemantics)
            };

            tables[(int)MetadataTable.MethodImpl] = new[]
            {
                Column.Tbl(MetadataTable.TypeDef), Column.Cod(CodedIndex.MethodDefOrRef),
                Column.Cod(CodedIndex.MethodDefOrRef)
            };

            tables[(int)MetadataTable.ModuleRef] = new[] { Column.Str() };

            tables[(int)MetadataTable.TypeSpec] = new[] { Column.Blb() };

            tables[(int)MetadataTable.ImplMap] = new[]
            {
                Column.U2(), Column.Cod(CodedIndex.MemberForwarded), Column.Str(),
                Column.Tbl(MetadataTable.ModuleRef)
            };

            tables[(int)MetadataTable.FieldRva] = new[]
            {
                Column.U4(), Column.Tbl(MetadataTable.Field)
            };

            tables[(int)MetadataTable.EncLog] = new[] { Column.U4(), Column.U4() };

            tables[(int)MetadataTable.EncMap] = new[] { Column.U4() };

            tables[(int)MetadataTable.Assembly] = new[]
            {
                Column.U4(), Column.U2(), Column.U2(), Column.U2(), Column.U2(), Column.U4(),
                Column.Blb(), Column.Str(), Column.Str()
            };

            tables[(int)MetadataTable.AssemblyProcessor] = new[] { Column.U4() };

            tables[(int)MetadataTable.AssemblyOS] = new[] { Column.U4(), Column.U4(), Column.U4() };

            tables[(int)MetadataTable.AssemblyRef] = new[]
            {
                Column.U2(), Column.U2(), Column.U2(), Column.U2(), Column.U4(), Column.Blb(),
                Column.Str(), Column.Str(), Column.Blb()
            };

            tables[(int)MetadataTable.AssemblyRefProcessor] = new[]
            {
                Column.U4(), Column.Tbl(MetadataTable.AssemblyRef)
            };

            tables[(int)MetadataTable.AssemblyRefOS] = new[]
            {
                Column.U4(), Column.U4(), Column.U4(), Column.Tbl(MetadataTable.AssemblyRef)
            };

            tables[(int)MetadataTable.File] = new[] { Column.U4(), Column.Str(), Column.Blb() };

            tables[(int)MetadataTable.ExportedType] = new[]
            {
                Column.U4(), Column.U4(), Column.Str(), Column.Str(),
                Column.Cod(CodedIndex.Implementation)
            };

            tables[(int)MetadataTable.ManifestResource] = new[]
            {
                Column.U4(), Column.U4(), Column.Str(), Column.Cod(CodedIndex.Implementation)
            };

            tables[(int)MetadataTable.NestedClass] = new[]
            {
                Column.Tbl(MetadataTable.TypeDef), Column.Tbl(MetadataTable.TypeDef)
            };

            tables[(int)MetadataTable.GenericParam] = new[]
            {
                Column.U2(), Column.U2(), Column.Cod(CodedIndex.TypeOrMethodDef), Column.Str()
            };

            tables[(int)MetadataTable.MethodSpec] = new[]
            {
                Column.Cod(CodedIndex.MethodDefOrRef), Column.Blb()
            };

            tables[(int)MetadataTable.GenericParamConstraint] = new[]
            {
                Column.Tbl(MetadataTable.GenericParam), Column.Cod(CodedIndex.TypeDefOrRef)
            };

            return tables;
        }

        /// <summary>
        /// The tables each coded index can name, in tag order. -1 marks a tag ECMA-335 reserves.
        /// </summary>
        private static int[][] BuildCodedTables()
        {
            var coded = new int[CodedTagBits.Length][];

            coded[(int)CodedIndex.TypeDefOrRef] = new[]
            {
                (int)MetadataTable.TypeDef, (int)MetadataTable.TypeRef, (int)MetadataTable.TypeSpec
            };

            coded[(int)CodedIndex.HasConstant] = new[]
            {
                (int)MetadataTable.Field, (int)MetadataTable.Param, (int)MetadataTable.Property
            };

            coded[(int)CodedIndex.HasCustomAttribute] = new[]
            {
                (int)MetadataTable.MethodDef, (int)MetadataTable.Field, (int)MetadataTable.TypeRef,
                (int)MetadataTable.TypeDef, (int)MetadataTable.Param, (int)MetadataTable.InterfaceImpl,
                (int)MetadataTable.MemberRef, (int)MetadataTable.Module,
                (int)MetadataTable.DeclSecurity, (int)MetadataTable.Property,
                (int)MetadataTable.Event, (int)MetadataTable.StandAloneSig,
                (int)MetadataTable.ModuleRef, (int)MetadataTable.TypeSpec,
                (int)MetadataTable.Assembly, (int)MetadataTable.AssemblyRef,
                (int)MetadataTable.File, (int)MetadataTable.ExportedType,
                (int)MetadataTable.ManifestResource, (int)MetadataTable.GenericParam,
                (int)MetadataTable.GenericParamConstraint, (int)MetadataTable.MethodSpec
            };

            coded[(int)CodedIndex.HasFieldMarshal] = new[]
            {
                (int)MetadataTable.Field, (int)MetadataTable.Param
            };

            coded[(int)CodedIndex.HasDeclSecurity] = new[]
            {
                (int)MetadataTable.TypeDef, (int)MetadataTable.MethodDef, (int)MetadataTable.Assembly
            };

            coded[(int)CodedIndex.MemberRefParent] = new[]
            {
                (int)MetadataTable.TypeDef, (int)MetadataTable.TypeRef, (int)MetadataTable.ModuleRef,
                (int)MetadataTable.MethodDef, (int)MetadataTable.TypeSpec
            };

            coded[(int)CodedIndex.HasSemantics] = new[]
            {
                (int)MetadataTable.Event, (int)MetadataTable.Property
            };

            coded[(int)CodedIndex.MethodDefOrRef] = new[]
            {
                (int)MetadataTable.MethodDef, (int)MetadataTable.MemberRef
            };

            coded[(int)CodedIndex.MemberForwarded] = new[]
            {
                (int)MetadataTable.Field, (int)MetadataTable.MethodDef
            };

            coded[(int)CodedIndex.Implementation] = new[]
            {
                (int)MetadataTable.File, (int)MetadataTable.AssemblyRef,
                (int)MetadataTable.ExportedType
            };

            // Tags 0, 1 and 4 are reserved and must not resolve to a table.
            coded[(int)CodedIndex.CustomAttributeType] = new[]
            {
                -1, -1, (int)MetadataTable.MethodDef, (int)MetadataTable.MemberRef, -1
            };

            coded[(int)CodedIndex.ResolutionScope] = new[]
            {
                (int)MetadataTable.Module, (int)MetadataTable.ModuleRef,
                (int)MetadataTable.AssemblyRef, (int)MetadataTable.TypeRef
            };

            coded[(int)CodedIndex.TypeOrMethodDef] = new[]
            {
                (int)MetadataTable.TypeDef, (int)MetadataTable.MethodDef
            };

            return coded;
        }
    }
}
