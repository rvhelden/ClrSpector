using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace ClrSpector
{
    /// <summary>
    /// Reads a runtime structure at a known base address.
    /// </summary>
    /// <remarks>
    /// Reads are addressed by explicit offset rather than sequentially. Offsets come from the
    /// runtime's own contract descriptor, so field order in this process is irrelevant - which
    /// is what makes the reader independent of the runtime's private layout choices.
    /// </remarks>
    public unsafe class MemoryReader
    {
        public MemoryReader(IntPtr basePtr)
        {
            this.BasePointer = basePtr.ToPointer();
        }

        public MemoryReader(void* basePtr)
        {
            this.BasePointer = basePtr;
        }

        public void* BasePointer { get; }

        public IntPtr Address => (IntPtr)this.BasePointer;

        public bool IsNull => this.BasePointer == null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void* At(int offset) => (byte*)this.BasePointer + offset;

        public byte ReadByte(int offset) => *(byte*)this.At(offset);

        public ushort ReadUShort(int offset) => *(ushort*)this.At(offset);

        public short ReadShort(int offset) => *(short*)this.At(offset);

        public uint ReadUInt(int offset) => *(uint*)this.At(offset);

        public int ReadInt(int offset) => *(int*)this.At(offset);

        public IntPtr ReadIntPtr(int offset) => *(IntPtr*)this.At(offset);

        /// <summary>A reader positioned at the pointer stored at <paramref name="offset"/>.</summary>
        public MemoryReader Dereference(int offset) => new MemoryReader(this.ReadIntPtr(offset));

        /// <summary>A reader positioned <paramref name="offset"/> bytes further along.</summary>
        public MemoryReader Offset(int offset) => new MemoryReader(this.At(offset));

        public string ReadNullTerminatedString(int offset)
        {
            var start = (byte*)this.At(offset);
            if (start == null)
                return null;

            var builder = new StringBuilder();
            for (var i = 0; ; i++)
            {
                var value = start[i];
                if (value == 0)
                    break;

                builder.Append((char)value);
            }

            return builder.ToString();
        }
    }
}
