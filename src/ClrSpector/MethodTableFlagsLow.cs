using System;

namespace ClrSpector
{
    [Flags]
    public enum MethodTableFlagsLow : uint
    {
        // AS YOU ADD NEW FLAGS PLEASE CONSIDER WHETHER Generics::NewInstantiation NEEDS
        // TO BE UPDATED IN ORDER TO ENSURE THAT METHODTABLES DUPLICATED FOR GENERIC INSTANTIATIONS
        // CARRY THE CORRECT INITIAL FLAGS.

        RemotingConfigChecked = 0x00000001,
        RequiresManagedActivation = 0x00000002,
        Unrestored = 0x00000004,
        CriticalTypePrepared = 0x00000008, // CriticalFinalizerObject derived type has had backout routines prepared
        HasApproxParent = 0x00000010,
        UnrestoredTypeKey = 0x00000020,
        IsNotFullyLoaded = 0x00000040,
        DependenciesLoaded = 0x00000080, // class and all depedencies loaded up to CLASS_LOADED_BUT_NOT_VERIFIED

        SkipWinRtOverride = 0x00000100, // No WinRT override is needed

        CanCompareBitsOrUseFastGetHashCode = 0x00000200,          // Is any field type or sub field type overrode Equals or GetHashCode
        HasCheckedCanCompareBitsOrUseFastGetHashCode = 0x00000400 // Whether we have checked the overridden Equals or GetHashCode
    }
}