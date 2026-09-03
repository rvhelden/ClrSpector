using System;
using System.Text;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// A cursor over a metadata blob, reading the compressed encodings ECMA-335 uses.
    /// </summary>
    /// <remarks>
    /// Signature blobs are dense: lengths, counts and token indexes are all stored as
    /// variable-width integers whose first byte says how many follow. That makes a signature
    /// unreadable by fixed offsets, so it has to be walked front to back - which is why this is a
    /// cursor rather than an addressable structure.
    ///
    /// Nothing is copied. The cursor points into the mapped image, and every read is bounds
    /// checked against the blob's own length so a malformed signature stops rather than walking
    /// off into whatever follows it.
    /// </remarks>
    public unsafe struct SignatureBlob
    {
        private readonly byte* start;
        private readonly int length;
        private int position;

        internal SignatureBlob(byte* start, int length)
        {
            this.start = start;
            this.length = length;
            this.position = 0;
        }

        /// <summary>How many bytes are left unread.</summary>
        public int Remaining => this.length - this.position;

        /// <summary>True once the whole blob has been read.</summary>
        public bool IsAtEnd => this.position >= this.length;

        /// <summary>A cursor over the next <paramref name="count"/> bytes of this one.</summary>
        public SignatureBlob Slice(int count)
        {
            if (count < 0 || count > this.Remaining)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"A blob of {this.length} bytes cannot yield {count} bytes at offset " +
                    $"{this.position}.");

            return new SignatureBlob(this.start + this.position, count);
        }

        public byte ReadByte()
        {
            if (this.position >= this.length)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"A signature blob of {this.length} bytes ended before it was fully decoded, " +
                    $"so it is not a signature this build understands.");

            return this.start[this.position++];
        }

        /// <summary>The next byte without consuming it, or zero at the end.</summary>
        public byte PeekByte() => this.position < this.length ? this.start[this.position] : (byte)0;

        /// <summary>
        /// A compressed unsigned integer: one, two or four bytes, chosen by the top bits of the
        /// first (ECMA-335 II.23.2).
        /// </summary>
        public uint ReadCompressedUInt()
        {
            var first = this.ReadByte();

            if ((first & 0x80) == 0)
                return first;

            if ((first & 0xC0) == 0x80)
                return (uint)(((first & 0x3F) << 8) | this.ReadByte());

            if ((first & 0xE0) == 0xC0)
            {
                var b1 = this.ReadByte();
                var b2 = this.ReadByte();
                var b3 = this.ReadByte();

                return (uint)(((first & 0x1F) << 24) | (b1 << 16) | (b2 << 8) | b3);
            }

            throw new ClrSpectorUnsupportedRuntimeException(
                $"0x{first:x2} does not begin a compressed integer, so this blob is not a " +
                $"signature.");
        }

        /// <summary>
        /// A compressed signed integer, used for array bounds. The value is rotated so small
        /// negatives stay short, so decoding is a shift plus a sign fold.
        /// </summary>
        public int ReadCompressedInt()
        {
            var first = this.PeekByte();
            var value = this.ReadCompressedUInt();

            int bits;
            uint signBit;

            if ((first & 0x80) == 0)
            {
                bits = 7;
                signBit = 0x01;
            }
            else if ((first & 0xC0) == 0x80)
            {
                bits = 14;
                signBit = 0x01;
            }
            else
            {
                bits = 29;
                signBit = 0x01;
            }

            var rotated = value >> 1;
            var negative = (value & signBit) != 0;

            return negative ? (int)(rotated - (1u << (bits - 1))) : (int)rotated;
        }

        /// <summary>The blob's total length, independent of how much has been read.</summary>
        public int Length => this.length;

        /// <summary>How far into the blob the cursor has advanced.</summary>
        public int Position => this.position;

        /// <summary>The address of the blob's first byte, for reporting where a value came from.</summary>
        public IntPtr Address => (IntPtr)this.start;

        /// <summary>
        /// The next <paramref name="count"/> bytes as a fixed-width little-endian value.
        /// </summary>
        /// <remarks>
        /// Signatures use the compressed encodings above, but a custom attribute's argument values
        /// do not: ECMA-335 II.23.3 stores those in their natural width, little-endian, unaligned.
        /// The bytes are read one at a time rather than cast, because a blob is only
        /// byte-aligned in the heap and an unaligned wide load is not portable.
        /// </remarks>
        public ulong ReadFixed(int count)
        {
            if (count < 0 || count > 8 || this.Remaining < count)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"A blob of {this.length} bytes cannot yield a {count}-byte value at offset " +
                    $"{this.position}.");

            var value = 0UL;

            for (var i = 0; i < count; i++)
                value |= (ulong)this.start[this.position + i] << (8 * i);

            this.position += count;

            return value;
        }

        /// <summary>The next <paramref name="count"/> bytes decoded as UTF-8.</summary>
        public string ReadUtf8(int count)
        {
            if (count < 0 || this.Remaining < count)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"A blob of {this.length} bytes cannot yield {count} string bytes at offset " +
                    $"{this.position}.");

            var text = Encoding.UTF8.GetString(this.start + this.position, count);
            this.position += count;

            return text;
        }

        /// <summary>
        /// A TypeDefOrRefOrSpec: a compressed value whose low two bits name the table.
        /// </summary>
        /// <remarks>
        /// This encoding is the signature blob's own, not the tables' coded index - the tag order
        /// is TypeDef, TypeRef, TypeSpec (ECMA-335 II.23.2.8) - so it is decoded here rather than
        /// through <see cref="MetadataImage.DecodeCoded"/>.
        /// </remarks>
        public (MetadataTable Table, uint RowId) ReadTypeDefOrRefOrSpec()
        {
            var encoded = this.ReadCompressedUInt();
            var rowId = encoded >> 2;

            switch (encoded & 0x03)
            {
                case 0: return (MetadataTable.TypeDef, rowId);
                case 1: return (MetadataTable.TypeRef, rowId);
                case 2: return (MetadataTable.TypeSpec, rowId);
                default:
                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"A TypeDefOrRefOrSpec in a signature carries tag 3, which names no table.");
            }
        }
    }
}
