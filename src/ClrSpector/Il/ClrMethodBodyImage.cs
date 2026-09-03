using System;
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

            body.CodeAddress = (IntPtr)code;
            body.Il = il;

            return body;
        }

        public override string ToString()
        {
            return $"method body @0x{this.Address.ToInt64():x} {(this.IsFatFormat ? "fat" : "tiny")} " +
                   $"il={this.Il.Length} bytes maxstack={this.MaxStack}";
        }
    }
}
