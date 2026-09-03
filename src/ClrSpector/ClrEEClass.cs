//@GENERICS:
// For most types there is a one-to-one mapping between MethodTable* and EEClass*
// However this is not the case for instantiated types where code and representation
// are shared between compatible instantiations (e.g. List<string> and List<object>)
// Then a single EEClass structure is shared between multiple MethodTable structures
//
// An EEClass is the cold part of a managed type; the hot part lives in the MethodTable.
// EEClass holds the field list and the MethodDescChunk list that describe the type's
// members. See class.h in the CoreCLR sources.

using System;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// The cold half of a managed type. Field offsets come from the runtime's contract
    /// descriptor, so only the fields this runtime actually publishes are read.
    /// </summary>
    public unsafe class ClrEEClass
    {
        public void* BasePointer { get; private set; }

        public IntPtr Address => (IntPtr)this.BasePointer;

        public uint Size { get; private set; }

        /// <summary>The MethodTable this EEClass belongs to (the back-pointer).</summary>
        public IntPtr MethodTablePointer { get; private set; }

        public ClrMethodTable MethodTable => this.MethodTablePointer == IntPtr.Zero
            ? null
            : ClrMethodTable.Create(new MemoryReader(this.MethodTablePointer));

        public IntPtr FieldDescList { get; private set; }

        /// <summary>Head of the linked list of MethodDescChunks holding this type's methods.</summary>
        public IntPtr MethodDescChunks { get; private set; }

        /// <summary>The type's metadata flags (ECMA-335 TypeAttributes).</summary>
        public uint AttrClass { get; private set; }

        /// <summary>The CorElementType the runtime uses internally for this type.</summary>
        public CorElementType NormType { get; private set; }

        public ushort NumberOfInstanceFields { get; private set; }
        public ushort NumberOfMethods { get; private set; }
        public ushort NumberOfStaticFields { get; private set; }
        public ushort NumberOfThreadStaticFields { get; private set; }
        public ushort NumberOfNonVirtualSlots { get; private set; }

        /// <summary>
        /// How many bytes of the MethodTable's BaseSize are padding rather than fields.
        /// </summary>
        /// <remarks>
        /// A type's instances are never smaller than the minimum object size, so a small type is
        /// padded up to it. Subtracting this from BaseSize gives what the fields actually need,
        /// which is the difference between "this object is 24 bytes" and "this object's fields
        /// are 4 bytes and the rest is the allocator's floor".
        /// </remarks>
        public byte BaseSizePadding { get; private set; }

        /// <summary>
        /// The EEClass's optional fields, or <see cref="IntPtr.Zero"/> when it has none.
        /// </summary>
        /// <remarks>
        /// Rarely-used per-type data - explicit layout information, a COM interface type, the
        /// module a type was loaded from when it differs - is kept out of every EEClass and put
        /// in a side structure only the types that need it pay for. The descriptor publishes the
        /// pointer but no fields for what it points at, so this is the address only.
        /// </remarks>
        public IntPtr OptionalFields { get; private set; }

        /// <summary>True when this type carries the optional side structure.</summary>
        public bool HasOptionalFields => this.OptionalFields != IntPtr.Zero;

        public bool IsInterface =>
            (this.AttrClass & (uint)CorTypeAttr.tdClassSemanticsMask) == (uint)CorTypeAttr.tdInterface;

        public bool IsClass =>
            (this.AttrClass & (uint)CorTypeAttr.tdClassSemanticsMask) == (uint)CorTypeAttr.tdClass;

        public bool IsAbstract => (this.AttrClass & (uint)CorTypeAttr.tdAbstract) != 0;

        public bool IsSealed => (this.AttrClass & (uint)CorTypeAttr.tdSealed) != 0;

        public bool IsAutoLayout =>
            (this.AttrClass & (uint)CorTypeAttr.tdLayoutMask) == (uint)CorTypeAttr.tdAutoLayout;

        public bool IsSequentialLayout =>
            (this.AttrClass & (uint)CorTypeAttr.tdLayoutMask) == (uint)CorTypeAttr.tdSequentialLayout;

        public bool IsExplicitLayout =>
            (this.AttrClass & (uint)CorTypeAttr.tdLayoutMask) == (uint)CorTypeAttr.tdExplicitLayout;

        public static ClrEEClass Create(MemoryReader reader)
        {
            var layout = ContractDescriptor.Current.GetDataType("EEClass");

            var eeclass = new ClrEEClass();
            eeclass.BasePointer = reader.BasePointer;
            eeclass.Size = layout.Size ?? 0;

            eeclass.MethodTablePointer = reader.ReadIntPtr(layout["MethodTable"]);
            eeclass.FieldDescList = reader.ReadIntPtr(layout["FieldDescList"]);
            eeclass.MethodDescChunks = reader.ReadIntPtr(layout["MethodDescChunk"]);

            eeclass.AttrClass = reader.ReadUInt(layout["CorTypeAttr"]);
            eeclass.NormType = (CorElementType)reader.ReadByte(layout["InternalCorElementType"]);

            eeclass.NumberOfInstanceFields = reader.ReadUShort(layout["NumInstanceFields"]);
            eeclass.NumberOfMethods = reader.ReadUShort(layout["NumMethods"]);
            eeclass.NumberOfStaticFields = reader.ReadUShort(layout["NumStaticFields"]);
            eeclass.NumberOfThreadStaticFields = reader.ReadUShort(layout["NumThreadStaticFields"]);
            eeclass.NumberOfNonVirtualSlots = reader.ReadUShort(layout["NumNonVirtualSlots"]);

            if (layout.HasField("BaseSizePadding"))
                eeclass.BaseSizePadding = reader.ReadByte(layout["BaseSizePadding"]);

            if (layout.HasField("OptionalFields"))
                eeclass.OptionalFields = reader.ReadIntPtr(layout["OptionalFields"]);

            return eeclass;
        }
    }
}
