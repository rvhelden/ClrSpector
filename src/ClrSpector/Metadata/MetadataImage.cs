using System;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// The ECMA-335 metadata of one module, parsed directly from the mapped image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the table and heap layer that reading a signature needs, and it deliberately owes
    /// nothing to <c>System.Reflection.Metadata</c>: it walks the metadata root, the stream
    /// headers and the <c>#~</c> table header itself, so a signature can be decoded with no
    /// managed metadata reader in the picture at all.
    /// </para>
    /// <para>
    /// The awkward part of ECMA-335 is that a row's address is not a fixed offset. Every column is
    /// two or four bytes depending on how big the thing it points at is - a heap index widens once
    /// the heap passes 64 KB, a table index once that table passes 65535 rows, and a coded index
    /// once the largest table it can address does - so the width of a column in one table depends
    /// on the row counts of others. Every table therefore has to be measured before any row of a
    /// later table can be found, which is why the whole schema is here rather than only the four
    /// tables a signature needs.
    /// </para>
    /// <para>
    /// Nothing is copied; rows and heap bytes are read in place out of the mapped image.
    /// </para>
    /// </remarks>
    public sealed unsafe class MetadataImage
    {
        /// <summary>"BSJB" - the metadata root's signature.</summary>
        private const uint MetadataSignature = 0x424A5342;

        /// <summary>HeapSizes bit 0: the string heap needs four-byte indexes.</summary>
        private const byte LargeStringHeap = 0x01;

        private const byte LargeGuidHeap = 0x02;

        private const byte LargeBlobHeap = 0x04;

        /// <summary>HeapSizes bit 6: an extra four bytes follow the row counts.</summary>
        private const byte HasExtraData = 0x40;

        /// <summary>
        /// One past the highest table there is, counting a portable PDB's own - which are
        /// numbered above the type system's, in the same stream, and measured the same way.
        /// </summary>
        private const int TableCount = (int)MetadataTable.CustomDebugInformation + 1;

        /// <summary>The size of a portable PDB's id: a GUID and the stamp that goes with it.</summary>
        private const int PdbIdSize = 20;

        private readonly byte* metadata;
        private readonly int metadataSize;

        private readonly byte* stringHeap;
        private readonly int stringHeapSize;
        private readonly byte* blobHeap;
        private readonly int blobHeapSize;
        private readonly byte* userStringHeap;
        private readonly int userStringHeapSize;

        private readonly int[] rowCounts = new int[TableCount];

        /// <summary>
        /// The row counts that decide how wide an index into a table is, which are not always
        /// the rows present here: a standalone PDB indexes into its module's tables and restates
        /// their counts for exactly this purpose, while holding none of their rows.
        /// </summary>
        private readonly int[] indexRowCounts = new int[TableCount];
        private readonly int[] rowSizes = new int[TableCount];
        private readonly long[] tableOffsets = new long[TableCount];
        private readonly int[][] columnOffsets = new int[TableCount][];

        private readonly ulong sortedMask;

        private readonly int stringIndexSize;
        private readonly int guidIndexSize;
        private readonly int blobIndexSize;

        private MetadataImage(IntPtr metadataAddress, int size)
        {
            this.metadata = (byte*)metadataAddress;
            this.metadataSize = size;

            var reader = new MemoryReader(metadataAddress);

            if (reader.ReadUInt(0) != MetadataSignature)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The metadata at 0x{metadataAddress.ToInt64():x} does not begin with the " +
                    $"'BSJB' signature, so it is not an ECMA-335 metadata root.");

            // Root: signature, major, minor, reserved, then a length-prefixed version string
            // padded to four bytes, then flags and the stream count.
            var versionLength = reader.ReadInt(12);
            this.Version = reader.ReadNullTerminatedString(16);
            var afterVersion = 16 + Align4(versionLength);
            var streamCount = reader.ReadUShort(afterVersion + 2);

            byte* tables = null;
            byte* pdb = null;
            var tablesSize = 0;
            var cursor = afterVersion + 4;

            for (var i = 0; i < streamCount; i++)
            {
                var streamOffset = reader.ReadInt(cursor);
                var streamSize = reader.ReadInt(cursor + 4);
                var name = reader.ReadNullTerminatedString(cursor + 8);

                if (name == null)
                    break;

                var start = this.metadata + streamOffset;

                switch (name)
                {
                    case "#~":
                    case "#-":
                        tables = start;
                        tablesSize = streamSize;
                        break;

                    case "#Strings":
                        this.stringHeap = start;
                        this.stringHeapSize = streamSize;
                        break;

                    case "#Blob":
                        this.blobHeap = start;
                        this.blobHeapSize = streamSize;
                        break;

                    case "#US":
                        this.userStringHeap = start;
                        this.userStringHeapSize = streamSize;
                        break;

                    case "#Pdb":
                        pdb = start;
                        break;
                }

                // The name is null-terminated and the header padded to a four-byte boundary.
                cursor += 8 + Align4(name.Length + 1);
            }

            if (tables == null)
                throw new ClrSpectorUnsupportedRuntimeException(
                    "The metadata has no '#~' or '#-' table stream, so its tables cannot be read.");

            var tableReader = new MemoryReader((IntPtr)tables);
            var heapSizes = tableReader.ReadByte(6);

            this.stringIndexSize = (heapSizes & LargeStringHeap) != 0 ? 4 : 2;
            this.guidIndexSize = (heapSizes & LargeGuidHeap) != 0 ? 4 : 2;
            this.blobIndexSize = (heapSizes & LargeBlobHeap) != 0 ? 4 : 2;

            var valid = tableReader.ReadULong(8);
            this.sortedMask = tableReader.ReadULong(16);

            // Row counts are present only for the tables the Valid mask marks, in table order.
            var countCursor = 24;
            for (var table = 0; table < 64; table++)
            {
                if ((valid & (1UL << table)) == 0)
                    continue;

                var count = tableReader.ReadInt(countCursor);
                countCursor += 4;

                if (table < TableCount)
                    this.rowCounts[table] = count;
            }

            if ((heapSizes & HasExtraData) != 0)
                countCursor += 4;

            // Indexes are as wide as the rows they can name, which starts as the rows here.
            Array.Copy(this.rowCounts, this.indexRowCounts, TableCount);

            if (pdb != null)
                this.ReadPdbStream(pdb);

            this.MeasureTables();

            var rowsStart = countCursor;
            var offset = (long)rowsStart;

            for (var table = 0; table < TableCount; table++)
            {
                this.tableOffsets[table] = offset;
                offset += (long)this.rowSizes[table] * this.rowCounts[table];
            }

            // Offsets here are relative to the table stream, so the stream's own size bounds them.
            if (offset > tablesSize)
                throw new ClrSpectorUnsupportedRuntimeException(
                    "The measured metadata tables run past the end of the table stream, so the " +
                    "schema this build understands does not match this module's metadata.");

            this.TableStream = tables;
        }

        /// <summary>The metadata version string from the root, e.g. "v4.0.30319".</summary>
        public string Version { get; }

        /// <summary>
        /// True when this is a standalone portable PDB rather than a module's own metadata.
        /// </summary>
        public bool IsPortablePdb => this.PdbId != null;

        /// <summary>
        /// A portable PDB's own id - the twenty bytes a module's CodeView debug entry has to
        /// match for the PDB to belong to it - or null for a module's metadata.
        /// </summary>
        public byte[] PdbId { get; private set; }

        /// <summary>
        /// The MethodDef token of the entry point a portable PDB records, or zero.
        /// </summary>
        public uint PdbEntryPoint { get; private set; }

        /// <summary>The start of the table stream; row offsets are relative to this.</summary>
        private byte* TableStream { get; }

        /// <summary>
        /// Reads the metadata root at <paramref name="metadataAddress"/>.
        /// </summary>
        /// <remarks>
        /// Measuring the tables costs a pass over the schema, so callers should hold on to the
        /// result rather than re-reading per lookup - <see cref="ClrModuleMetadata"/> keeps one
        /// per module image.
        /// </remarks>
        public static MetadataImage At(IntPtr metadataAddress, int size)
        {
            if (metadataAddress == IntPtr.Zero || size <= 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"There is no metadata at 0x{metadataAddress.ToInt64():x} with size {size}.");

            return new MetadataImage(metadataAddress, size);
        }

        /// <summary>
        /// Reads the <c>#Pdb</c> stream, which is what makes a standalone portable PDB readable
        /// at all.
        /// </summary>
        /// <remarks>
        /// A PDB's table stream carries row counts only for the PDB's own tables, yet its rows
        /// still hold indexes into the module's tables - and how wide those indexes are depends
        /// on how many rows those tables have. So the PDB restates them: a mask of the tables it
        /// references, then a row count for each. Without applying those first, every column
        /// after an index would be measured at the wrong offset.
        /// </remarks>
        private void ReadPdbStream(byte* stream)
        {
            var reader = new MemoryReader((IntPtr)stream);

            var id = new byte[PdbIdSize];
            for (var i = 0; i < PdbIdSize; i++)
                id[i] = reader.ReadByte(i);

            this.PdbId = id;
            this.PdbEntryPoint = reader.ReadUInt(PdbIdSize);

            var referenced = reader.ReadULong(PdbIdSize + 4);
            var cursor = PdbIdSize + 12;

            for (var table = 0; table < 64; table++)
            {
                if ((referenced & (1UL << table)) == 0)
                    continue;

                var count = reader.ReadInt(cursor);
                cursor += 4;

                // These are the module's tables, not this stream's: they widen an index and
                // contribute no rows, which is why only the index counts are touched.
                if (table < TableCount && this.rowCounts[table] == 0)
                    this.indexRowCounts[table] = count;
            }
        }

        /// <summary>The size of the whole metadata region, in bytes.</summary>
        public int MetadataSizeOf() => this.metadataSize;

        /// <summary>The size of the string heap, which holds names.</summary>
        public int StringHeapSize => this.stringHeapSize;

        /// <summary>The size of the blob heap, which holds signatures.</summary>
        public int BlobHeapSize => this.blobHeapSize;

        /// <summary>The size of the user string heap, which holds <c>ldstr</c> literals.</summary>
        public int UserStringHeapSize => this.userStringHeapSize;

        /// <summary>
        /// How many bytes a string heap index takes in a row: two, or four once the heap passes
        /// 64 KB. This is the widening that makes a row's offset depend on the image's size.
        /// </summary>
        public int StringIndexSize => this.stringIndexSize;

        /// <summary>How many bytes a blob heap index takes in a row.</summary>
        public int BlobIndexSize => this.blobIndexSize;

        /// <summary>How many bytes a GUID heap index takes in a row.</summary>
        public int GuidIndexSize => this.guidIndexSize;

        /// <summary>The measured byte size of one row of a table.</summary>
        public int RowSize(MetadataTable table) => this.rowSizes[(int)table];

        /// <summary>
        /// Whether the metadata declares a table sorted by its key column.
        /// </summary>
        /// <remarks>
        /// This is what makes a binary search over a table legitimate rather than a guess. A
        /// writer is allowed to leave a table unsorted, and the header says which it sorted, so a
        /// search that relies on order has to ask first.
        /// </remarks>
        public bool IsSorted(MetadataTable table) => (this.sortedMask & (1UL << (int)table)) != 0;

        /// <summary>How many rows a table has.</summary>
        public int RowCount(MetadataTable table) => this.rowCounts[(int)table];

        /// <summary>
        /// A reader positioned at row <paramref name="rowId"/> of <paramref name="table"/>.
        /// Row ids are one-based, as tokens are.
        /// </summary>
        public MemoryReader Row(MetadataTable table, uint rowId)
        {
            var index = (int)table;

            if (rowId == 0 || rowId > (uint)this.rowCounts[index])
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Row {rowId} is outside the {table} table, which has " +
                    $"{this.rowCounts[index]} rows.");

            var offset = this.tableOffsets[index] + (long)(rowId - 1) * this.rowSizes[index];
            return new MemoryReader((IntPtr)(this.TableStream + offset));
        }

        /// <summary>The byte offset of a column within its row.</summary>
        public int ColumnOffset(MetadataTable table, int column) => this.columnOffsets[(int)table][column];

        /// <summary>
        /// Reads a column, widening from two or four bytes as that column's measured size says.
        /// </summary>
        public uint ReadColumn(MetadataTable table, uint rowId, int column)
        {
            var offsets = this.columnOffsets[(int)table];
            var start = offsets[column];
            var width = offsets[column + 1] - start;
            var row = this.Row(table, rowId);

            switch (width)
            {
                case 1: return row.ReadByte(start);
                case 2: return row.ReadUShort(start);
                case 4: return row.ReadUInt(start);
                default:
                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"Column {column} of {table} measured {width} bytes, which is not a " +
                        $"metadata column width.");
            }
        }

        /// <summary>A UTF-8 string from the string heap.</summary>
        public string String(uint index)
        {
            if (this.stringHeap == null || index >= (uint)this.stringHeapSize)
                return null;

            return new MemoryReader((IntPtr)(this.stringHeap + index)).ReadNullTerminatedString(0);
        }

        /// <summary>
        /// A user string from the <c>#US</c> heap, which is what a <c>ldstr</c> token names.
        /// </summary>
        /// <remarks>
        /// These are UTF-16 rather than UTF-8, and length-prefixed in bytes with one trailing
        /// flag byte the runtime uses to note whether any character needs special handling - so
        /// the character count is one less than the byte count, halved.
        /// </remarks>
        public string UserString(uint index)
        {
            if (this.userStringHeap == null || index >= (uint)this.userStringHeapSize)
                return null;

            var blob = new SignatureBlob(
                this.userStringHeap + index, this.userStringHeapSize - (int)index);

            var byteCount = (int)blob.ReadCompressedUInt();
            if (byteCount <= 1)
                return string.Empty;

            var characters = (byteCount - 1) / 2;
            var text = new char[characters];

            for (var i = 0; i < characters; i++)
                text[i] = (char)(blob.ReadByte() | (blob.ReadByte() << 8));

            return new string(text);
        }

        /// <summary>
        /// A blob from the blob heap, as a reader positioned at its first byte.
        /// </summary>
        /// <remarks>
        /// A blob is stored length-prefixed with a compressed integer, so the length is read
        /// first and the reader left just past it.
        /// </remarks>
        public SignatureBlob Blob(uint index)
        {
            if (this.blobHeap == null || index >= (uint)this.blobHeapSize)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Blob index {index} is outside the {this.blobHeapSize}-byte blob heap.");

            var blob = new SignatureBlob(this.blobHeap + index, this.blobHeapSize - (int)index);
            var length = blob.ReadCompressedUInt();

            return blob.Slice((int)length);
        }

        /// <summary>
        /// Works out every column's width and offset, for every table.
        /// </summary>
        /// <remarks>
        /// Coded index widths depend on row counts, and row counts are already known by the time
        /// this runs, so one pass is enough - no table's column width depends on another table's
        /// column width, only on its row count.
        /// </remarks>
        private void MeasureTables()
        {
            for (var table = 0; table < TableCount; table++)
            {
                var columns = MetadataSchema.TryOf((MetadataTable)table) ?? Array.Empty<Column>();
                var offsets = new int[columns.Length + 1];
                var offset = 0;

                for (var column = 0; column < columns.Length; column++)
                {
                    offsets[column] = offset;
                    offset += this.WidthOf(columns[column]);
                }

                offsets[columns.Length] = offset;

                this.columnOffsets[table] = offsets;
                this.rowSizes[table] = offset;
            }
        }

        private int WidthOf(Column column)
        {
            switch (column.Kind)
            {
                case ColumnKind.Byte:
                    return 1;

                case ColumnKind.UInt16:
                    return 2;

                case ColumnKind.UInt32:
                    return 4;

                case ColumnKind.String:
                    return this.stringIndexSize;

                case ColumnKind.Guid:
                    return this.guidIndexSize;

                case ColumnKind.Blob:
                    return this.blobIndexSize;

                case ColumnKind.Table:
                    return this.indexRowCounts[(int)column.Table] < 65536 ? 2 : 4;

                case ColumnKind.Coded:
                    return this.CodedWidth(column.Coded);

                default:
                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"Unrecognised metadata column kind {column.Kind}.");
            }
        }

        /// <summary>
        /// A coded index is two bytes while the largest table it can name still fits in the bits
        /// left over after the tag.
        /// </summary>
        private int CodedWidth(CodedIndex coded)
        {
            var tables = MetadataSchema.TablesOf(coded);
            var tagBits = MetadataSchema.TagBitsOf(coded);
            var largest = 0;

            foreach (var table in tables)
            {
                if (table < 0)
                    continue;

                if (this.indexRowCounts[table] > largest)
                    largest = this.indexRowCounts[table];
            }

            return largest < 1 << (16 - tagBits) ? 2 : 4;
        }

        /// <summary>
        /// The coded-index value that names <paramref name="rowId"/> of
        /// <paramref name="table"/> - the inverse of <see cref="DecodeCoded"/>.
        /// </summary>
        /// <remarks>
        /// Needed to search a table by an owner column: the CustomAttribute table is sorted by its
        /// Parent coded index, so finding one owner's attributes means encoding the owner and
        /// binary-searching for that encoded value, not decoding every row.
        /// </remarks>
        public uint EncodeCoded(CodedIndex coded, MetadataTable table, uint rowId)
        {
            var tagBits = MetadataSchema.TagBitsOf(coded);
            var tables = MetadataSchema.TablesOf(coded);

            for (var tag = 0; tag < tables.Length; tag++)
            {
                if (tables[tag] == (int)table)
                    return (rowId << tagBits) | (uint)tag;
            }

            throw new ClrSpectorUnsupportedRuntimeException(
                $"Coded index {coded} cannot name the {table} table.");
        }

        /// <summary>Decodes a coded index into the table it names and the row within it.</summary>
        public (MetadataTable Table, uint RowId) DecodeCoded(CodedIndex coded, uint value)
        {
            var tagBits = MetadataSchema.TagBitsOf(coded);
            var tables = MetadataSchema.TablesOf(coded);
            var tag = (int)(value & ((1u << tagBits) - 1));

            if (tag >= tables.Length || tables[tag] < 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Coded index {coded} carries tag {tag}, which names no table.");

            return ((MetadataTable)tables[tag], value >> tagBits);
        }

        private static int Align4(int value) => (value + 3) & ~3;

        public override string ToString()
        {
            return $"metadata @0x{(long)this.metadata:x} size={this.metadataSize} " +
                   $"types={this.rowCounts[(int)MetadataTable.TypeDef]} " +
                   $"methods={this.rowCounts[(int)MetadataTable.MethodDef]} version={this.Version}";
        }
    }
}
