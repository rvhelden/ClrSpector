using System;
using System.Collections.Generic;
using System.Text;

namespace ClrSpector
{
    public enum PackedSlotLayout : ushort
    {
        SlotMask = 0x03ff,
        NameHashMask = 0xFC00
    }

    [Flags]
    public enum MethodDescFlag2 : byte
    {
        // HasPrecode implies that HasStableEntryPoint is set.
        HasStableEntryPoint = 0x01, // The method entrypoint is stable (either precode or actual code)
        HasPrecode = 0x02,          // Precode has been allocated for this method
        IsUnboxingStub = 0x04,
        HasNativeCodeSlot = 0x08,   // Has slot for native code
        IsJitIntrinsic = 0x10,      // Jit may expand method as an intrinsic
        IsEligibleForTieredCompilation = 0x20
    }

    [Flags]
    public enum MethodDescFlag3 : ushort
    {
        // There are flags available for use here (currently 5 flags bits are available); however, new bits are hard to come by, so any new flags bits should
        // have a fairly strong justification for existence.
        TokenRemainderMask = 0x3FFF, // This must equal METHOD_TOKEN_REMAINDER_MASK calculated higher in this file
                                     // These are seperate to allow the flags space available and used to be obvious here
                                     // and for the logic that splits the token to be algorithmically generated based on the
                                     // #define
        HasForwardedValuetypeParameter = 0x4000,           // Indicates that a type-forwarded type is used as a valuetype parameter (this flag is only valid for ngenned items)
        ValueTypeParametersWalked = 0x4000,                // Indicates that all typeref's in the signature of the method have been resolved to typedefs (or that process failed) (this flag is only valid for non-ngenned methods)
        DoesNotHaveEquivalentValuetypeParameters = 0x8000, // Indicates that we have verified that there are no equivalent valuetype parameters for this method
    }


    // Method.hpp:
    public unsafe class ClrMethodDescription : ClrInternalObject
    {
        public ushort Flags3AndTokenRemainder { get; set; }
        public byte ChunkIndex { get; set; }
        public MethodDescFlag2 Flags2 { get; set; }
        
        // The slot number of this MethodDesc in the vtable array.
        // Note that we may store other information in the high bits if available -- 
        // see enum_packedSlotLayout and mdcRequiresFullSlotNumber for details.
        public ushort SlotNumber { get; set; }
        public PackedSlotLayout Flags { get; set; }
        public MethodDescFlag3 Flags3 => (MethodDescFlag3)(this.Flags3AndTokenRemainder & ~(ushort)MethodDescFlag3.TokenRemainderMask);
        public ushort Token => (ushort)(this.Flags3AndTokenRemainder & (ushort)MethodDescFlag3.TokenRemainderMask);
        
        public static ClrMethodDescription Create(MemoryReader reader)
        {
            var md = new ClrMethodDescription();

            md.Flags3AndTokenRemainder = reader.ReadUShort();
            md.ChunkIndex = reader.ReadByte();
            md.Flags2 = (MethodDescFlag2)reader.ReadByte();
            md.SlotNumber = reader.ReadUShort();
            md.Flags = (PackedSlotLayout)reader.ReadUShort();

            md.ClrPointer = new IntPtr(reader.BasePointer);
            md.Size = reader.Position;

            return md;
        }
    }
}
