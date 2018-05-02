using System;

namespace ClrSpector.Methods
{
    public abstract class Precode : ClrInternalObject
    {
        public abstract IntPtr GetMethodDesc();
    }
}