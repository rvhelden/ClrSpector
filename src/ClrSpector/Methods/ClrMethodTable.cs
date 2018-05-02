//-------------------------------------------------------------------
// The VTABLE
//
// Rather than the traditional array of code pointers (or "slots") we use a two-level vtable in
// which slots for virtual methods live in chunks.  Doing so allows the chunks to be shared among
// method tables (the most common example being between parent and child classes where the child
// does not override any method in the chunk).  This yields substantial space savings at the fixed 
// cost of one additional indirection for a virtual call.
//
// Note that none of this should be visible outside the implementation of MethodTable; all other
// code continues to refer to a virtual method via the traditional slot number.  This is similar to
// how we refer to non-virtual methods as having a slot number despite having long ago moved their
// code pointers out of the vtable.
// 
// Consider a class where GetNumVirtuals is 5 and (for the sake of the example) assume we break 
// the vtable into chunks of size 3.  The layout would be as follows:
//
//   pMT                       chunk 1                   chunk 2
//   ------------------        ------------------        ------------------
//   |                |        |      M1()      |        |      M4()      |
//   |   fixed-size   |        ------------------        ------------------
//   |   portion of   |        |      M2()      |        |      M5()      |
//   |   MethodTable  |        ------------------        ------------------
//   |                |        |      M3()      |
//   ------------------        ------------------
//   | ptr to chunk 1 |
//   ------------------
//   | ptr to chunk 2 |
//   ------------------
//
// We refer to "ptr to chunk 1" and "ptr to chunk 2" as "indirection slots."
// 
// The current chunking strategy is independent of class properties; all are of size 8.  Several 
// other strategies were tried, and the only one that has performed better empirically is to begin 
// with a single chunk of size 4 (matching the number of virtuals in System.Object) and then
// continue with chunks of size 8.  However it was a small improvement and required the run-time 
// helpers listed below to be measurably slower.
//
// If you want to change this, you should only need to modify the first four functions below
// along with any assembly helper that has taken a dependency on the layout.  Currently,
// those consist of:
//     JIT_IsInstanceOfInterface
//     JIT_ChkCastInterface
//     Transparent proxy stub
//
// This layout only applies to the virtual methods in a class (those with slot number below GetNumVirtuals).
// Non-virtual methods that are in the vtable (those with slot numbers between GetNumVirtuals and
// GetNumVtableSlots) are laid out in a single chunk pointed to by an optional member.
// See GetSlotPtrRaw for more details.



using System;
using System.Collections.Generic;
using ClrSpector.Methods;

namespace ClrSpector
{
    public unsafe class ClrMethodTable
    {
        public const uint VTABLE_SLOTS_PER_CHUNK = 8;
        public const int VTABLE_SLOTS_PER_CHUNK_LOG2 = 3;

        public const int OFFSETOF_PRECODE_TYPE_X64 = 0;
        public const int OFFSETOF_PRECODE_TYPE_CALL_OR_JMP_X64 = 5;
        public const int OFFSETOF_PRECODE_TYPE_MOV_R10_X64 = 10;
        public const int OFFSETOF_PRECODE_TYPE_X86 = 5;
        public const int OFFSETOF_PRECODE_TYPE_CALL_OR_JMP_X86 = 5;
        public const int OFFSETOF_PRECODE_TYPE_MOV_RM_R_X86 = 6;

        public const byte X86_INSTR_MOV_RM_R = 0x89; // mov r/m,reg
        public const byte X86_INSTR_MOV_R10_IMM64 = 0xBA49 & 0xFF; // mov r10, imm64
        public const byte X86_INSTR_CALL_REL32 = 0xE8; // call rel32
        public const byte X86_INSTR_JMP_REL32 = 0xE9;// jmp rel32

        public void* BasePointer { get; set; }
        public uint Size { get; set; }

        public uint Flags { get; set; }
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
        public IntPtr ElementTypeHnd { get; set; }
        public IntPtr PerInstInfo { get; set; }
        public IntPtr InterfaceMap { get; set; }
        public IntPtr DebugClassName { get; set; }
        public bool HasNonVirtualSlots => this.Flags2.HasFlag(MethodTableFlags2.HasNonVirtualSlots);
        public bool HasSingleNonVirtualSlot => this.Flags2.HasFlag(MethodTableFlags2.HasSingleNonVirtualSlot);
        public bool HasNonVirtualSlotsArray => this.HasNonVirtualSlots && !this.HasSingleNonVirtualSlot;
        public bool IsInterface => this.FlagsHigh.HasFlag(MethodTableFlagsHigh.CategoryInterface);
        public bool IsClass => this.FlagsHigh.HasFlag(MethodTableFlagsHigh.CategoryClass);
        public bool IsArray => this.FlagsHigh.HasFlag(MethodTableFlagsHigh.CategoryArray);
        public bool IsStringOrArray => this.FlagsHigh.HasFlag(MethodTableFlagsHigh.HasComponentSize);
        public ushort ComponentSize { get; set; }
        public ClrMethodTable CanonMethodTable { get; set; }
        public bool IsCanonicalMethodTable => this == this.CanonMethodTable;

        public uint NumberOfVtableIndirections => (this.NumberOfVirtuals + (ClrMethodTable.VTABLE_SLOTS_PER_CHUNK - 1)) >> ClrMethodTable.VTABLE_SLOTS_PER_CHUNK_LOG2;
        public uint NumberOfVTableSlots => this.NumberOfVirtuals + (this.HasNonVirtualSlots ? this.EEClass.NumberOfNonVirtualSlots : 0);

        public List<ClrMethodDescription> Methods { get; set; }

        public static ClrMethodTable Create(MemoryReader reader)
        {
            var mt = new ClrMethodTable();
            mt.BasePointer = reader.BasePointer;

            mt.Flags = reader.ReadUInt();
            mt.FlagsHigh = (MethodTableFlagsHigh) (mt.Flags & 0xFFFF0000);
            mt.FlagsLow = (MethodTableFlagsLow) (mt.Flags & 0x0000FFFF);
            mt.ComponentSize = (ushort)((ushort)mt.FlagsHigh & 0x00FF);
            
            mt.BaseSize = reader.ReadUInt();
            mt.Flags2 = (MethodTableFlags2)reader.ReadUShort();

            mt.Token = reader.ReadShort();
            mt.NumberOfVirtuals = reader.ReadUShort();
            mt.NumberOfInterfaces = reader.ReadUShort();

            if (ClrEnvironment.IsDebug())
                mt.DebugClassName = reader.ReadIntPtr();

            var parentMtPointer = reader.ReadIntPtr();
            /*if (parentMtPointer != IntPtr.Zero)
                mt.ParentMethodTable = ClrMethodTable.Create(new MemoryReader(parentMtPointer));*/

            mt.Module = reader.ReadIntPtr();
            mt.WriteableData = reader.ReadIntPtr();

            var unionField = reader.ReadIntPtr();
            var unionFieldKind = (MethodTableUnionFieldKind) ((long)unionField.ToPointer() & 3);

            var classPtr = IntPtr.Size == 8 ?
                new IntPtr(unionField.ToInt64() & ~3) :
                new IntPtr(unionField.ToInt32() & ~3);

            var unionReader = new MemoryReader(classPtr);

            switch (unionFieldKind)
            {
                case MethodTableUnionFieldKind.EEClass: 
                    mt.EEClass = ClrEEClass.Create(unionReader);
                    mt.CanonMethodTable = mt;
                    break;
                case MethodTableUnionFieldKind.Invalid: break; 
                case MethodTableUnionFieldKind.MethodTable:
                    mt.CanonMethodTable = ClrMethodTable.Create(unionReader);
                    break;
                case MethodTableUnionFieldKind.Indirection:
                    mt.CanonMethodTable = ClrMethodTable.Create(new MemoryReader(*(IntPtr *)unionReader.BasePointer));
                    break;
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
            
            mt.CreateMethodDescriptions();

            return mt;
        }

        private uint GetIndexOfVtableIndirection(uint slot)
        {
            return slot >> ClrMethodTable.VTABLE_SLOTS_PER_CHUNK_LOG2;
        }

        private uint GetIndexAfterVtableIndirection(uint slot)
        {
            return slot & (ClrMethodTable.VTABLE_SLOTS_PER_CHUNK - 1);
        }

        private uint GetFlag(MethodTableFlags2 flag)
        {
            return (uint)this.Flags2 & (uint)flag;
        }

        private void* GetMultipurposeSlotPtr(MethodTableFlags2 flag, byte[] offsets)
        {
            var flagValue = this.GetFlag(flag - 1);
            uint offset = offsets[flagValue];

            if (offset >= this.BaseSize)
                offset += this.NumberOfVtableIndirections * (uint)IntPtr.Size;
            
            return (byte*) this.BasePointer + offset;
        }

        private void CreateMethodDescriptions()
        {
            this.Methods = new List<ClrMethodDescription>();

            for (uint slotNumber = 0; slotNumber < this.NumberOfVTableSlots; slotNumber++)
                this.Methods.Add(this.CreateMethodDescription(slotNumber));
        }

        private ClrMethodDescription CreateMethodDescription(uint slotNumber)
        {
            var slotPointer = this.GetSlotPtrRaw(slotNumber);

            var precode = this.CreatePrecode(new MemoryReader(slotPointer));
            var pMD = precode.GetMethodDesc();

            var md = ClrMethodDescription.Create(new MemoryReader(pMD));
            return md;
        }

        private Precode CreatePrecode(MemoryReader reader)
        {
            var precodeType = (PrecodeType)reader.ReadByte();

            if ((byte)precodeType == ClrMethodTable.X86_INSTR_MOV_RM_R)
                reader.Position = ClrMethodTable.OFFSETOF_PRECODE_TYPE_MOV_RM_R_X86;
            else if ((byte)precodeType == ClrMethodTable.X86_INSTR_MOV_RM_R)
                reader.Position = ClrMethodTable.OFFSETOF_PRECODE_TYPE_MOV_R10_X64;
            else if ((byte)precodeType == ClrMethodTable.X86_INSTR_CALL_REL32 || (byte)precodeType == ClrMethodTable.X86_INSTR_JMP_REL32)
                reader.Position = ClrMethodTable.OFFSETOF_PRECODE_TYPE_CALL_OR_JMP_X64;

            precodeType = (PrecodeType)reader.ReadByte();
            if ((byte)precodeType == FixupPrecode.TypePrestub)
                precodeType = PrecodeType.Fixup;

            reader.Position = 0;

            switch (precodeType)
            {
                case PrecodeType.StubX86:
                    break;
                case PrecodeType.StubX64:
                    break;
                case PrecodeType.NDirectImportX86:
                    break;
                case PrecodeType.NDirectImportX64:
                    break;
                case PrecodeType.RemotingX86:
                    break;
                case PrecodeType.Fixup:
                    return FixupPrecode.Create(reader);
                case PrecodeType.ThisPtrRetBufPrecodeX86:
                    break;
            }

            return null;
        }

        private void* GetNonVirtualSlotsPtr()
        {
            return this.GetMultipurposeSlotPtr(MethodTableFlags2.HasNonVirtualSlots, new byte[] { 48, 56, 48, 64, 56, 64, 72, 48, 56, 48, 64 });
        }

        public IntPtr* GetIndirectionSlots()
        {
            return (IntPtr*)((byte*)this.BasePointer + this.Size);
        }

        private IntPtr GetSlotPtrRaw(uint slotNumber)
        {
            if (slotNumber < this.NumberOfVirtuals)
            {
                // Virtual slots live in chunks pointed to by vtable indirections
                var index = this.GetIndexOfVtableIndirection(slotNumber);
                var indirectionSlots = this.GetIndirectionSlots();
                var methodTableChunk = (IntPtr *)*(indirectionSlots + index);
                var vtableEntry = methodTableChunk + this.GetIndexAfterVtableIndirection(slotNumber);
                return *vtableEntry;
            }
            else if (this.HasSingleNonVirtualSlot)
            {
                // Non-virtual slots < GetNumVtableSlots live in a single chunk pointed to by an optional member,
                // except when there is only one in which case it lives in the optional member itself

                if (IntPtr.Size == 4)
                    return new IntPtr(*(int*) this.GetNonVirtualSlotsPtr());
                else
                    return new IntPtr(*(long*) this.GetNonVirtualSlotsPtr());
            }
            else
            {
                // Non-virtual slots < GetNumVtableSlots live in a single chunk pointed to by an optional member
                if (IntPtr.Size == 4)
                    return new IntPtr(*(int *) this.GetNonVirtualSlotsPtr() + (slotNumber - this.NumberOfVirtuals));
                else
                    return new IntPtr(*(long *) this.GetNonVirtualSlotsPtr() + (slotNumber - this.NumberOfVirtuals));
            }
        }
    }
}
