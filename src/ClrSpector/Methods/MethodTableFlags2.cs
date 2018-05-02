using System;

namespace ClrSpector
{
    [Flags]
    public enum MethodTableFlags2 : ushort
    {
        MultipurposeSlotsMask = 0x001F,
        HasPerInstInfo = 0x0001,
        HasInterfaceMap = 0x0002,
        HasDispatchMapSlot = 0x0004,
        HasNonVirtualSlots = 0x0008,
        HasModuleOverride = 0x0010,
        IsZapped = 0x0020,      // This could be fetched from m_pLoaderModule if we run out of flags
        IsPreRestored = 0x0040, // Class does not need restore
        HasModuleDependencies = 0x0080,
        IsIntrinsicType = 0x0100, // Is an SIMD Intinsic type
        RequiresDispatchTokenFat = 0x0200,
        HasCctor = 0x0400,       // Has class constructor, static constructor
        HasCCWTemplate = 0x0800, // Com Callable Wrapper Has an extra field pointing to a CCW template
        RequiresAlign8 = 0x1000, // Type requires 8-byte alignment (only set on platforms that require this and don't get it implicitly)
        HasBoxedRegularStatics = 0x2000, // GetNumBoxedRegularStatics() != 0
        HasSingleNonVirtualSlot = 0x4000,
        DependsOnEquivalentOrForwardedStructs = 0x8000, // Declares methods that have type equivalent or type forwarded structures in their signature
    }
}