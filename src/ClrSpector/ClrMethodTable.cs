using System;

namespace ClrSpector
{
    public unsafe class ClrMethodTable
    {
        public void* BasePointer { get; set; }
        public int Size { get; set; }
        public MethodTableFlagsHigh FlagsHigh { get; set; }
        public MethodTableFlagsLow FlagsLow { get; set; }
        public uint BaseSize { get; set; }
        public MethodTableFlags2 Flags2 { get; set; }
        public short Token { get; set; }
        public ushort NumberOfVirtuals { get; set; }
        public ushort NumberOfInterfaces { get; set; }
        public ClrMethodTable ParentMethodTable { get; set; }
        public IntPtr Module { get; set; }
        public IntPtr WriteableData { get; set; }
        public ClrEEClass EEClass { get; set; }
        public ClrMethodTable CanonicalMethodTable { get; set; }
        public IntPtr ElementTypeHnd { get; set; }
        public IntPtr PerInstInfo { get; set; }
        public IntPtr InterfaceMap { get; set; }
        public bool HasNonVirtualSlots => this.Flags2.HasFlag(MethodTableFlags2.HasNonVirtualSlots);
        public bool HasSingleNonVirtualSlot => this.Flags2.HasFlag(MethodTableFlags2.HasSingleNonVirtualSlot);
        public bool HasNonVirtualSlotsArray => this.HasNonVirtualSlots && !this.HasSingleNonVirtualSlot;
        public bool IsInterface => this.FlagsHigh.HasFlag(MethodTableFlagsHigh.CategoryInterface);
        public bool IsClass => this.FlagsHigh.HasFlag(MethodTableFlagsHigh.CategoryClass);
        public bool IsArray => this.FlagsHigh.HasFlag(MethodTableFlagsHigh.CategoryArray);
        public ushort ComponentSize { get; set; }

        public static ClrMethodTable Create(MemoryReader reader)
        {
            var mt = new ClrMethodTable();
            mt.BasePointer = reader.BasePointer;

            var flags = reader.ReadUInt();
            mt.FlagsHigh = (MethodTableFlagsHigh) (flags & 0xFFFF0000);
            mt.FlagsLow = (MethodTableFlagsLow) (flags & 0x0000FFFF);
            mt.ComponentSize = (ushort)((ushort)mt.FlagsHigh & 0x00FF);
            
            mt.BaseSize = reader.ReadUInt();
            mt.Flags2 = (MethodTableFlags2)reader.ReadUShort();

            mt.Token = reader.ReadShort();
            mt.NumberOfVirtuals = reader.ReadUShort();
            mt.NumberOfInterfaces = reader.ReadUShort();

            var parentMtPointer = reader.ReadIntPtr();
            if (parentMtPointer != IntPtr.Zero)
                mt.ParentMethodTable = ClrMethodTable.Create(new MemoryReader(parentMtPointer));

            mt.Module = reader.ReadIntPtr();
            mt.WriteableData = reader.ReadIntPtr();

            var eeclassPointer = reader.ReadIntPtr();
            var fieldType = (MethodTableUnionFieldKind) ((long) eeclassPointer.ToPointer() & 3);

            var classPtr = IntPtr.Size == 8 ?
                new IntPtr(eeclassPointer.ToInt64() & ~3) :
                new IntPtr(eeclassPointer.ToInt32() & ~3);

            switch (fieldType)
            {
                case MethodTableUnionFieldKind.EEClass: mt.EEClass = ClrEEClass.Create(new MemoryReader(classPtr)); break;
                case MethodTableUnionFieldKind.Invalid: break; 
                case MethodTableUnionFieldKind.MethodTable: break;
                case MethodTableUnionFieldKind.Indirection: break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var multipurposeSlot1 = reader.ReadIntPtr();
            var multipurposeSlot2 = reader.ReadIntPtr();

            if (mt.FlagsHigh.HasFlag(MethodTableFlagsHigh.CategoryArray))
                mt.ElementTypeHnd = multipurposeSlot1;
            else if (mt.Flags2.HasFlag(MethodTableFlags2.HasPerInstInfo))
                mt.PerInstInfo = multipurposeSlot1;

            if (mt.Flags2.HasFlag(MethodTableFlags2.HasInterfaceMap))
                mt.InterfaceMap = multipurposeSlot2;

            mt.Size = reader.Position;

            return mt;
        }
    }
}
