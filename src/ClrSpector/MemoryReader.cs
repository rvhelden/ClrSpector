using System;
using System.Runtime.CompilerServices;

namespace ClrSpector
{
    public unsafe class MemoryReader
    {
        public void* BasePointer { get; set; }
        public int Position { get; set; }

        public MemoryReader(IntPtr basePtr)
        {
            this.BasePointer = basePtr.ToPointer();
        }

        public MemoryReader(void* basePtr)
        {
            this.BasePointer = basePtr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void* GetCurrentPointer()
        {
            return (byte*)this.BasePointer + this.Position;
        }

        public byte ReadByte()
        {
            var value = *(byte*)this.GetCurrentPointer();
            this.Position += sizeof(byte);

            return value;
        }

        public int ReadInt()
        {
            var value = *(int*)this.GetCurrentPointer();
            this.Position += sizeof(int);

            return value;
        }

        public uint ReadUInt()
        {
            var value = *(uint*) this.GetCurrentPointer();
            this.Position += sizeof(uint);

            return value;
        }

        public ushort ReadUShort()
        {
            var value = *(ushort*)this.GetCurrentPointer();
            this.Position += sizeof(ushort);

            return value;
        }

        public short ReadShort()
        {
            var value = *(short*)this.GetCurrentPointer();
            this.Position += sizeof(short);

            return value;
        }

        public IntPtr ReadIntPtr()
        {
            var value = *(IntPtr*)this.GetCurrentPointer();
            this.Position += IntPtr.Size;

            return value;
        }
    }
}
