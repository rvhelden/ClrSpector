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
        HasNativeCodeSlot = 0x08, // Has slot for native code
        Transparency_Mask = 0x30,
        Transparency_Unknown = 0x00,     // The transparency has not been computed yet
        Transparency_Transparent = 0x10, // Method is transparent
        Transparency_Critical = 0x20,    // Method is critical
        Transparency_TreatAsSafe = 0x30, // Method is treat as safe. Also implied critical.

        // CAS Demands: Demands for Permissions that are CAS Permissions. CAS Perms are those 
        // that derive from CodeAccessPermission and need a stackwalk to evaluate demands
        // Non-CAS perms are those that don't need a stackwalk and don't derive from CodeAccessPermission. The implementor 
        // specifies the behavior on a demand. Examples: CAS: FileIOPermission. Non-CAS: PrincipalPermission.
        // This bit gets set if the demands are BCL CAS demands only. Even if there are non-BCL CAS demands, we don't set this
        // bit.
        CASDemandsOnly = 0x40,
        HostProtectionLinkCheckOnly = 0x80, // Method has LinkTime check due to HP only.
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

    public unsafe class ClrMethodDescription : ClrInternalObject
    {
        public string DebugMethodName { get; set; }
        public string DebugClassName { get; set; }
        public string DebugMethodSignature { get; set; }
        public IntPtr DebugMethodTablePointer { get; set; }
        public IntPtr GcCover { get; set; }
        public ClrMethodTable DebugMethodTable => ClrMethodTable.Create(new MemoryReader(this.DebugMethodTablePointer));
        public ushort Flags3AndTokenRemainder { get; set; }
        public byte ChunkIndex { get; set; }
        public MethodDescFlag2 Flags2 { get; set; }
        public ushort SlotNumber { get; set; }
        public PackedSlotLayout Flags { get; set; }
        public MethodDescFlag3 Flags3 => (MethodDescFlag3)(this.Flags3AndTokenRemainder & ~(ushort)MethodDescFlag3.TokenRemainderMask);
        public ushort Token => (ushort)(this.Flags3AndTokenRemainder & (ushort)MethodDescFlag3.TokenRemainderMask);
        
        public static ClrMethodDescription Create(MemoryReader reader)
        {
            var md = new ClrMethodDescription();

            if (ClrEnvironment.IsDebug())
            {
                md.DebugMethodName = reader.Dereference().ReadString();
                md.DebugClassName = reader.Dereference().ReadString();
                md.DebugMethodSignature = reader.Dereference().ReadString();
                md.DebugMethodTablePointer = reader.ReadIntPtr();
                md.GcCover = reader.ReadIntPtr();
            }

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
