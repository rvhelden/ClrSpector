using System;

namespace ClrSpector
{
    [Flags]
    public enum MethodTableFlagsHigh : uint
    {
        CategoryMask = 0x000F0000,
        CategoryClass = 0x00000000,
        CategoryUnused1 = 0x00010000,
        CategoryMarshalByRefMask = 0x000E0000,
        CategoryMarshalByRef = 0x00020000,
        CategoryContextful = 0x00030000, // sub-category of MarshalByRef
        CategoryValueType = 0x00040000,
        CategoryValueTypeMask = 0x000C0000,
        CategoryNullable = 0x00050000,           // sub-category of ValueType
        CategoryPrimitiveValueType = 0x00060000, // sub-category of ValueType, Enum or primitive value type
        CategoryTruePrimitive = 0x00070000,      // sub-category of ValueType, Primitive (ELEMENT_TYPE_I, etc.)
        CategoryArray = 0x00080000,
        CategoryArrayMask = 0x000C0000,
        CategoryIfArrayThenSzArray = 0x00020000, // sub-category of Array
        CategoryInterface = 0x000C0000,
        CategoryUnused2 = 0x000D0000,
        CategoryTransparentProxy = 0x000E0000,
        CategoryAsyncPin = 0x000F0000,
        CategoryElementTypeMask = 0x000E0000,       // bits that matter for element type mask
        HasFinalizer = 0x00100000,                  // instances require finalization
        IfNotInterfaceThenMarshalable = 0x00200000, // Is this type marshalable by the pinvoke marshalling layer
        ICastable = 0x00400000,                     // class implements ICastable interface
        HasIndirectParent = 0x00800000,             // m_pParentMethodTable has double indirection
        ContainsPointers = 0x01000000,
        HasTypeEquivalence = 0x02000000,   // can be equivalent to another type
        HasCriticalFinalizer = 0x08000000, // finalizer must be run on Appdomain Unload
        Collectible = 0x10000000,
        ContainsGenericVariables = 0x20000000, // we cache this flag to help detect these efficiently and
        ComObject = 0x40000000,                // class is a com object
        HasComponentSize = 0x80000000,         // This is set if component size is used for flags.
        NonTrivialInterfaceCast = MethodTableFlagsHigh.CategoryArray | MethodTableFlagsHigh.ComObject | MethodTableFlagsHigh.ICastable
    }
}