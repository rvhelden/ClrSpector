using System;

namespace ClrSpector.Methods
{
    public unsafe class FixupPrecode : Precode
    {
        public const byte TypePrestub = 0x5E;
        public readonly byte Op;
        public readonly int Rel32;
        public readonly byte Type;
        public readonly byte MethodDescChunkIndex;
        public readonly byte PrecodeChunkIndex;

        public FixupPrecode(byte op, int rel32, byte type, byte methodDescChunkIndex, byte precodeChunkIndex)
        {
            this.Op = op;
            this.Rel32 = rel32;
            this.Type = type;
            this.MethodDescChunkIndex = methodDescChunkIndex;
            this.PrecodeChunkIndex = precodeChunkIndex;
        }

        public static FixupPrecode Create(MemoryReader reader)
        {
            var op = reader.ReadByte();
            var rel32 = reader.ReadInt();
            var type = reader.ReadByte();
            var methodDescChunkIndex = reader.ReadByte();
            var precodeChunkIndex = reader.ReadByte();

            return new FixupPrecode(op, rel32, type, methodDescChunkIndex, precodeChunkIndex)
            {
                ClrPointer = (IntPtr)reader.BasePointer,
                Size = reader.Position
            };
        }

        public override IntPtr GetMethodDesc()
        {
            var @base = *(IntPtr *)this.GetBase().ToPointer();

            var methodDescAlignment = 1 << (IntPtr.Size == 8 ? 3 : 2);
            
            if (IntPtr.Size == 8)
                return new IntPtr(@base.ToInt64() + this.MethodDescChunkIndex * methodDescAlignment);
            else
                return new IntPtr(@base.ToInt32() + this.MethodDescChunkIndex * methodDescAlignment);
        }

        // Fixup precode chunk is associated with MethodDescChunk. The layout of the fixup precode chunk is:
        //
        // FixupPrecode     Entrypoint PrecodeChunkIndex = 2
        // FixupPrecode     Entrypoint PrecodeChunkIndex = 1
        // FixupPrecode     Entrypoint PrecodeChunkIndex = 0
        // TADDR            Base of MethodDescChunk
        private IntPtr GetBase()
        {
            if (IntPtr.Size == 8)
                return new IntPtr(base.ClrPointer.ToInt64() + (this.PrecodeChunkIndex + 1) * this.Size);
            else
                return new IntPtr(base.ClrPointer.ToInt32() + (this.PrecodeChunkIndex + 1) * this.Size);
        }
    }
}