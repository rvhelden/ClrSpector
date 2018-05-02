using System;
using System.Collections.Generic;
using System.Text;

namespace ClrSpector
{
    public class ClrMethodDataEntry
    {
        public ClrMethodDescription Declaration { get; set; }
        public ClrMethodDescription Implementation { get; set; }
    }

    public class ClrMethodData
    {
        public ClrMethodTable MethodTable { get; set; }
        public uint NextChainDepth { get; set; }
        public bool ContainsMethodImpl { get; set; }

        public ClrMethodData(ClrMethodTable methodTable)
        {
            this.MethodTable = methodTable;
            this.NextChainDepth = 0;
            this.ContainsMethodImpl = false;
        }
    }
}
