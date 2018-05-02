using System;

namespace ClrSpector
{
    public abstract unsafe class ClrInternalObject
    {
        public IntPtr ClrPointer { get; protected set; }
        public uint Size { get; protected set; }
    }
}
