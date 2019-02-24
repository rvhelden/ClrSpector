using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace ClrSpector
{
    public unsafe class MemoryReader
    {
        public void* BasePointer { get; set; }
        public uint Position { get; set; }

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

        public MemoryReader Dereference()
        {
            var dereferenced = new MemoryReader(*(IntPtr*)this.GetCurrentPointer());
            this.Position += (uint)IntPtr.Size;

            return dereferenced;
        }

        public string ReadString()
        {
            var charBase = (byte*)this.GetCurrentPointer();

            var builder = new StringBuilder();

            int count = 0;
            while (true)
            {
                var value = *(charBase + count++);
                if (value == '\0')
                    break;

                builder.Append((char)value);
            }

            this.Position += (uint)count;

            return builder.ToString();
        }

        public IntPtr ReadIntPtr()
        {
            var value = *(IntPtr*)this.GetCurrentPointer();
            this.Position += (uint)IntPtr.Size;

            return value;
        }

        public IntPtr ReadRelativeIntPtr()
        {
            var value = *(IntPtr*)this.GetCurrentPointer();
            if (value == IntPtr.Zero)
            {
                this.Position += (uint)IntPtr.Size;
                return IntPtr.Zero;
            }

            value = IntPtr.Size == 8 ? 
                new IntPtr(value.ToInt64() + (byte*)this.BasePointer + this.Position) : 
                new IntPtr(value.ToInt32() + (byte*)this.BasePointer + this.Position);

            this.Position += (uint)IntPtr.Size;

            return value;
        }
    }
}
