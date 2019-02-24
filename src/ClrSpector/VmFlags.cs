using System;

namespace ClrSpector
{
    [Flags]
    public enum VmFlags : uint
    {
        LayoutDependsOnOtherModules = 0x00000001,
        Delegate = 0x00000002,
        Unused = 0x0000001c,
        FixedAddressVtStatics = 0x00000020, // Value type Statics in this class will be pinned
        HasLayout = 0x00000040,
        Isnested = 0x00000080,
        IsEquivalentType = 0x00000200,
        Hasoverlayedfields = 0x00000400,
        HasFieldsWhichMustBeInited = 0x00000800,
        Unsafevaluetype = 0x00001000,
        BestfitmappingInited = 0x00002000,  // VMFLAG_BESTFITMAPPING and VMFLAG_THROWONUNMAPPABLECHAR are valid only if this is set
        Bestfitmapping = 0x00004000,        // BestFitMappingAttribute.Value
        Throwonunmappablechar = 0x00008000, // BestFitMappingAttribute.ThrowOnUnmappableChar
        NoGuid = 0x00020000,
        Hasnonpublicfields = 0x00040000,
        ContainsStackPtr = 0x00100000,
        PreferAlign8 = 0x00200000, // Would like to have 8-byte alignment
        SparseForCominterop = 0x00800000,
        Hascoclassattrib = 0x01000000,
        Comeventitfmask = 0x02000000, // class is a special COM event interface
        ProjectedFromWinrt = 0x04000000,
        ExportedToWinrt = 0x08000000,
        NotTightlyPacked = 0x10000000,
        ContainsMethodimpls = 0x20000000,
        MarshalingtypeMask = 0xc0000000,
        MarshalingtypeInhibit = 0x40000000,
        MarshalingtypeFreethreaded = 0x80000000,
        MarshalingtypeStandard = 0xc0000000,
    }
}