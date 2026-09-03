using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// A method's IL as it sits in the module image, read straight out of memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="System.Reflection.MethodBody.GetILAsByteArray"/> needs a
    /// <see cref="System.Reflection.MethodBase"/>. This needs only a MethodDesc: its metadata
    /// token gives the body's relative virtual address, and because the image is mapped that is
    /// an offset from the module base.
    /// </para>
    /// <para>
    /// After the code comes whatever the header said followed it: the exception handling table,
    /// and in principle other section kinds, each announcing whether another follows. That table
    /// is the only place a method's try, catch, filter and finally regions exist - the runtime
    /// keeps no decoded copy for a reader to ask - so a body read from memory has to parse it or
    /// report no handlers at all.
    /// </para>
    /// <para>
    /// The body begins with a header in one of two shapes. A <b>tiny</b> header is a single byte
    /// whose top six bits are the code size - used when the method has no locals, no exception
    /// handlers, and a stack no deeper than eight. Anything else gets a <b>fat</b> header of at
    /// least twelve bytes carrying the real stack depth, the code size and the local signature
    /// token. Both are decoded here.
    /// </para>
    /// </remarks>
    public sealed unsafe class ClrMethodBodyImage
    {
        /// <summary>The low two bits of the first byte say which header shape this is.</summary>
        private const byte FormatMask = 0x03;

        private const byte TinyFormat = 0x02;
        private const byte FatFormat = 0x03;

        /// <summary>A tiny header stores the code size in its upper six bits.</summary>
        private const int TinySizeShift = 2;

        /// <summary>A fat header's size is in four-byte units, in the top nibble of its flags.</summary>
        private const int FatHeaderSizeShift = 12;

        private const ushort InitLocalsFlag = 0x0010;

        /// <summary>A fat header's flag saying data sections follow the code.</summary>
        private const ushort MoreSectionsFlag = 0x0008;

        /// <summary>Section kind bit: this section is the exception handling table.</summary>
        private const byte EhTableSection = 0x01;

        /// <summary>Section kind bit: this section uses the wide entry format.</summary>
        private const byte FatSection = 0x40;

        /// <summary>Section kind bit: another section follows this one.</summary>
        private const byte MoreSections = 0x80;

        /// <summary>Both section formats begin with a four-byte header.</summary>
        private const int SectionHeaderSize = 4;

        /// <summary>A small-format handler entry: offsets as 16 bits, lengths as 8.</summary>
        private const int SmallClauseSize = 12;

        /// <summary>A fat-format handler entry: everything as 32 bits.</summary>
        private const int FatClauseSize = 24;

        /// <summary>
        /// A stop on a section chain that is not being read correctly. ECMA-335 puts no limit on
        /// the count, but a method with more handlers than this means the walk has gone wrong,
        /// and reading on would be reading arbitrary memory.
        /// </summary>
        private const int MaximumRegions = 4096;

        private ClrMethodBodyImage()
        {
        }

        /// <summary>Where the method's header begins.</summary>
        public IntPtr Address { get; private set; }

        /// <summary>Where the IL itself begins, past the header.</summary>
        public IntPtr CodeAddress { get; private set; }

        /// <summary>The IL bytes.</summary>
        public byte[] Il { get; private set; }

        /// <summary>How deep the evaluation stack gets.</summary>
        public int MaxStack { get; private set; }

        /// <summary>True when the method's locals are zeroed on entry.</summary>
        public bool InitLocals { get; private set; }

        /// <summary>
        /// The standalone signature token describing the locals, or zero when the method has
        /// none. Only a fat header carries one.
        /// </summary>
        public uint LocalSignatureToken { get; private set; }

        /// <summary>True for the twelve-byte header, false for the one-byte one.</summary>
        public bool IsFatFormat { get; private set; }

        /// <summary>
        /// The try/handler regions the method declares, in the order the table lists them -
        /// which is innermost first, as ECMA-335 requires. Empty when the method has none.
        /// </summary>
        /// <remarks>
        /// A typed catch's <see cref="ClrIlExceptionRegion.CatchTypeName"/> is left unset here:
        /// this reads the body, not the metadata that would name the token. Reading IL through
        /// <see cref="ClrMethodIl"/> fills it in.
        /// </remarks>
        public IReadOnlyList<ClrIlExceptionRegion> ExceptionRegions { get; private set; }
            = Array.Empty<ClrIlExceptionRegion>();

        /// <summary>
        /// Reads the body at <paramref name="rva"/> in the image mapped at
        /// <paramref name="imageBase"/>, or null when there is no body.
        /// </summary>
        public static ClrMethodBodyImage Read(IntPtr imageBase, int rva)
        {
            if (imageBase == IntPtr.Zero || rva == 0)
                return null;

            var header = (byte*)imageBase + rva;
            var format = (byte)(*header & FormatMask);

            var body = new ClrMethodBodyImage { Address = (IntPtr)header };

            int codeSize;
            byte* code;

            if (format == TinyFormat)
            {
                codeSize = *header >> TinySizeShift;
                code = header + 1;

                body.MaxStack = 8;
                body.IsFatFormat = false;
            }
            else if (format == FatFormat)
            {
                var flags = *(ushort*)header;

                // The header's own size is stored in four-byte units.
                var headerSize = (flags >> FatHeaderSizeShift) * 4;

                body.MaxStack = *(ushort*)(header + 2);
                codeSize = (int)*(uint*)(header + 4);
                body.LocalSignatureToken = *(uint*)(header + 8);
                body.InitLocals = (flags & InitLocalsFlag) != 0;
                body.IsFatFormat = true;

                code = header + headerSize;
            }
            else
            {
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The method body at 0x{(long)header:x} begins with 0x{*header:x2}, which is " +
                    "neither a tiny nor a fat header. Either the address is wrong or this is not " +
                    "a method body.");
            }

            var il = new byte[codeSize];
            for (var i = 0; i < codeSize; i++)
                il[i] = code[i];

            if (body.IsFatFormat && (*(ushort*)header & MoreSectionsFlag) != 0)
                body.ExceptionRegions = ReadSections(Align(code + codeSize, SectionHeaderSize));

            body.CodeAddress = (IntPtr)code;
            body.Il = il;

            return body;
        }

        /// <summary>
        /// Walks the chain of data sections after the code, collecting the handlers out of the
        /// exception table.
        /// </summary>
        /// <remarks>
        /// Each section says its own size and whether another follows, so the chain is walked
        /// rather than indexed. Sections other than the exception table are skipped by that size:
        /// no other kind is defined, but a section this does not understand is a reason to step
        /// over it, not to stop reading.
        /// </remarks>
        private static IReadOnlyList<ClrIlExceptionRegion> ReadSections(byte* section)
        {
            var regions = new List<ClrIlExceptionRegion>();

            while (true)
            {
                var kind = section[0];

                // A fat section's size is the upper three bytes of the header word; a small
                // one's is a single byte, with the two after it reserved. Both counts include
                // the header itself.
                var fat = (kind & FatSection) != 0;
                var dataSize = fat ? (int)(*(uint*)section >> 8) : section[1];

                if (dataSize < SectionHeaderSize)
                    break;

                if ((kind & EhTableSection) != 0)
                    ReadClauses(section + SectionHeaderSize, dataSize - SectionHeaderSize, fat, regions);

                if ((kind & MoreSections) == 0 || regions.Count > MaximumRegions)
                    break;

                section = Align(section + dataSize, SectionHeaderSize);
            }

            return regions;
        }

        private static void ReadClauses(
            byte* clause, int size, bool fat, List<ClrIlExceptionRegion> regions)
        {
            var stride = fat ? FatClauseSize : SmallClauseSize;
            var count = Math.Min(size / stride, MaximumRegions - regions.Count);

            for (var i = 0; i < count; i++, clause += stride)
            {
                // The two formats differ only in how wide each field is; a small clause cannot
                // describe a method whose IL is longer than 64KB, which is why the fat one
                // exists at all.
                var kind = (ClrIlExceptionRegionKind)(fat ? *(uint*)clause : *(ushort*)clause);

                var tryOffset = fat ? (int)*(uint*)(clause + 4) : *(ushort*)(clause + 2);
                var tryLength = fat ? (int)*(uint*)(clause + 8) : clause[4];
                var handlerOffset = fat ? (int)*(uint*)(clause + 12) : *(ushort*)(clause + 5);
                var handlerLength = fat ? (int)*(uint*)(clause + 16) : clause[7];
                var last = *(uint*)(clause + (fat ? 20 : 8));

                // The last field is a class token for a typed catch and a filter offset for a
                // filter, and nothing at all for the other two kinds.
                var filterOffset = kind == ClrIlExceptionRegionKind.Filter ? (int)last : 0;
                var catchToken = kind == ClrIlExceptionRegionKind.Catch ? last : 0;

                regions.Add(new ClrIlExceptionRegion(
                    kind, tryOffset, tryLength, handlerOffset, handlerLength, filterOffset, catchToken));
            }
        }

        /// <summary>Rounds a pointer up to the next <paramref name="alignment"/> boundary.</summary>
        private static byte* Align(byte* address, int alignment)
        {
            var value = (long)address;

            return (byte*)(value + (alignment - 1) & ~((long)alignment - 1));
        }

        public override string ToString()
        {
            var handlers = this.ExceptionRegions.Count == 0
                ? string.Empty
                : $" handlers={this.ExceptionRegions.Count}";

            return $"method body @0x{this.Address.ToInt64():x} {(this.IsFatFormat ? "fat" : "tiny")} " +
                   $"il={this.Il.Length} bytes maxstack={this.MaxStack}{handlers}";
        }
    }
}
