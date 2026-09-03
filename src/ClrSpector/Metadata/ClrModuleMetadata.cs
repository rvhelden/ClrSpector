using System;
using System.Collections.Concurrent;
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

        /// <summary>The table byte of a user string token, which names the <c>#US</c> heap.</summary>
        private const int UserStringTokenType = 0x70;

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

            return AtImageBase(module.Base);
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