//@GENERICS:
// For most types there is a one-to-one mapping between MethodTable* and EEClass*
// However this is not the case for instantiated types where code and representation
// are shared between compatible instantiations (e.g. List<string> and List<object>)
// Then a single EEClass structure is shared between multiple MethodTable structures
// Uninstantiated generic types (e.g. List) have their own EEClass and MethodTable,
// used (a) as a representative for the generic type itself, (b) for static fields and
// methods, which aren't present in the instantiations, and (c) to hold some information
// (e.g. formal instantiations of superclass and implemented interfaces) that is common
// to all instantiations and isn't stored in the EEClass structures for instantiated types
//
//
// **  NOTE  **  NOTE  **  NOTE  **  NOTE  **  NOTE  **  NOTE  **  NOTE  **  NOTE
//
// A word about EEClass vs. MethodTable
// ------------------------------------
//
// At compile-time, we are happy to touch both MethodTable and EEClass.  However,
// at runtime we want to restrict ourselves to the MethodTable.  This is critical
// for common code paths, where we want to keep the EEClass out of our working
// set.  For uncommon code paths, like throwing exceptions or strange Contexts
// issues, it's okay to access the EEClass.
//
// To this end, the TypeHandle (CLASS_HANDLE) abstraction is now based on the
// MethodTable pointer instead of the EEClass pointer.  If you are writing a
// runtime helper that calls GetClass() to access the associated EEClass, please
// stop to wonder if you are making a mistake.
//
// **  NOTE  **  NOTE  **  NOTE  **  NOTE  **  NOTE  **  NOTE  **  NOTE  **  NOTE


// An code:EEClass is a representation of the part of a managed type that is not used very frequently (it is
// cold), and thus is segregated from the hot portion (which lives in code:MethodTable).  As noted above an
// it is also the case that EEClass is SHARED among all instantiations of a generic type, so anything that
// is specific to a paritcular type can not live off the EEClass.
// 
// From here you can get to 
//     code:MethodTable - The representation of the hot portion of a type.
//     code:MethodDesc - The representation of a method 
//     code:FieldDesc - The representation of a field. 
// 
// EEClasses hold the following important fields
//     * code:EEClass.m_pMethodTable - Points a MethodTable associated with 
//     * code:EEClass.m_pChunks - a list of code:MethodDescChunk which is simply a list of code:MethodDesc
//         which represent the methods.  
//     * code:EEClass.m_pFieldDescList - a list of fields in the type.  
//

//-------------------------------------------------------------
// CONCRETE DATA LAYOUT
//
// Although accessed far less frequently than MethodTables, EEClasses are still
// pulled into working set, especially at startup.  This has motivated several space
// optimizations in field layout where each is balanced against the need to access
// a particular field efficiently.
//
// Currently, the following strategy is used:
//
//     - Any field that has a default value for the vast majority of EEClass instances
//       should be stored in the EEClassOptionalFields (see header comment)
//
//     - Any field that is nearly always a small positive integer and is infrequently
//       accessed should be in the EEClassPackedFields (see header comment)
//
// If none of these categories apply - such as for always-meaningful pointer members or
// sets of flags - a full field is used.  Please avoid adding such members if possible.
//-------------------------------------------------------------

using System;
using System.Diagnostics;

namespace ClrSpector
{
    // class.h -> 1822
    public unsafe class ClrEEClass
    {
        public const int kMaxLengthBits = 5;  // Number of bits needed to express the maximum length of a field (32-bits)
        public const int kBitsPerint = 32; // Number of bits in a int

        public void* BasePointer { get; set; }
        public uint Size { get; set; }

        public IntPtr GuidInfo { get; set; }

        public IntPtr DebugClassName { get; set; }
        public bool DebuggingClass {  get; set; }

        public IntPtr OptionalFields { get; set; }
        public IntPtr MethodTablePointer { get; set; }
        public ClrMethodTable MethodTable => this.MethodTablePointer == IntPtr.Zero ? null : ClrMethodTable.Create(new MemoryReader(this.MethodTablePointer));
        public IntPtr FieldDescList { get; set; }
        public IntPtr MethodDescChunks { get; set; }

        public uint NativeSize { get; set; } // valid only if EEClass::IsBlittable() or EEClass::HasLayout() is true
        public IntPtr ComCallableWrapper { get; set; } // points to interop data structures used when this type is exposed to COM

        public uint AttrClass { get; set; }
        public VmFlags VmFlags { get; set; }
        public uint AuxFlags {  get; set; }

        // NOTE: Following BYTE fields are layed out together so they'll fit within the same DWORD for efficient
        // structure packing.
        public byte NormType { get; set; }
        public bool FieldsArePacked { get; set; } // TRUE iff fields pointed to by GetPackedFields() are in packed state
        public byte FixedEEClassFields { get; set; } // Count of bytes of normal fields of this instance (EEClass,
                                                     // LayoutEEClass etc.). Doesn't count bytes of "packed" fields
        public byte BaseSizePadding { get; set; } // How many bytes of padding are included in BaseSize

        public uint NumberOfInstanceFields { get; set; }
        public uint NumberOfMethods { get; set; }
        public uint NumberOfStaticFields { get; set; }
        public uint NumberOfHandleStatics { get; set; }
        public uint NumberOfBoxedStatics { get; set; }
        public uint NonGcStaticFieldBytes { get; set; }
        public uint NumberOfThreadStaticFields { get; set; }
        public uint NumberOfHandleThreadStatics { get; set; }
        public uint NumberOfBoxedThreadStatics { get; set; }
        public uint NonGcThreadStaticFieldBytes { get; set; }
        public uint NumberOfNonVirtualSlots { get; set; }
        public uint Count { get; set; }


        public bool IsBlittable => this.HasLayout && this.GetLayoutInfo().IsBlittable;
        public bool HasLayout => this.VmFlags.HasFlag(VmFlags.HasLayout);

        public static ClrEEClass Create(MemoryReader reader)
        {
            var eeclass = new ClrEEClass();
            eeclass.BasePointer = reader.BasePointer;

            eeclass.GuidInfo = reader.ReadIntPtr();

            if (ClrEnvironment.IsDebug())
            {
                eeclass.DebugClassName = reader.ReadIntPtr();
                eeclass.DebuggingClass = reader.ReadInt() == 1;
                reader.ReadInt(); // Padding
            }

            Debugger.Break();

            eeclass.OptionalFields = reader.ReadRelativeIntPtr();
            eeclass.MethodTablePointer = reader.ReadRelativeIntPtr();
            eeclass.FieldDescList = reader.ReadRelativeIntPtr();
            eeclass.MethodDescChunks = reader.ReadRelativeIntPtr();

            eeclass.NativeSize = reader.ReadUInt();
            eeclass.ComCallableWrapper = reader.ReadIntPtr();

            eeclass.AttrClass = reader.ReadUInt();
            eeclass.VmFlags = (VmFlags)reader.ReadUInt();

            if (ClrEnvironment.IsDebug())
            {
                eeclass.AuxFlags = reader.ReadUShort();
            }

            eeclass.NormType = reader.ReadByte();
            eeclass.FieldsArePacked = reader.ReadByte() == 0x01;
            eeclass.FixedEEClassFields = reader.ReadByte();
            eeclass.BaseSizePadding = reader.ReadByte();

            eeclass.Size = reader.Position;

            var fieldsBase = (uint*)((byte*)reader.BasePointer + eeclass.FixedEEClassFields);

            eeclass.NumberOfInstanceFields = ClrEEClass.GetField(fieldsBase, EEClassFields.NumInstanceFields, eeclass.FieldsArePacked);
            eeclass.NumberOfMethods = ClrEEClass.GetField(fieldsBase, EEClassFields.NumMethods, eeclass.FieldsArePacked);
            eeclass.NumberOfStaticFields = ClrEEClass.GetField(fieldsBase, EEClassFields.NumStaticFields, eeclass.FieldsArePacked);
            eeclass.NumberOfHandleStatics = ClrEEClass.GetField(fieldsBase, EEClassFields.NumHandleStatics, eeclass.FieldsArePacked);
            eeclass.NumberOfBoxedStatics = ClrEEClass.GetField(fieldsBase, EEClassFields.NumBoxedStatics, eeclass.FieldsArePacked);
            eeclass.NonGcStaticFieldBytes = ClrEEClass.GetField(fieldsBase, EEClassFields.NonGCStaticFieldBytes, eeclass.FieldsArePacked);
            eeclass.NumberOfThreadStaticFields = ClrEEClass.GetField(fieldsBase, EEClassFields.NumThreadStaticFields, eeclass.FieldsArePacked);
            eeclass.NumberOfHandleThreadStatics = ClrEEClass.GetField(fieldsBase, EEClassFields.NumHandleThreadStatics, eeclass.FieldsArePacked);
            eeclass.NumberOfBoxedThreadStatics = ClrEEClass.GetField(fieldsBase, EEClassFields.NumBoxedThreadStatics, eeclass.FieldsArePacked);
            eeclass.NonGcThreadStaticFieldBytes = ClrEEClass.GetField(fieldsBase, EEClassFields.NonGCThreadStaticFieldBytes, eeclass.FieldsArePacked);
            eeclass.NumberOfNonVirtualSlots = ClrEEClass.GetField(fieldsBase, EEClassFields.NumNonVirtualSlots, eeclass.FieldsArePacked);
            eeclass.Count = ClrEEClass.GetField(fieldsBase, EEClassFields.Count, eeclass.FieldsArePacked);
            
            return eeclass;
        }

        public ClrEEClassLayoutInfo GetLayoutInfo()
        {
            if (!this.HasLayout)
            {
                throw new InvalidOperationException("EEClass does not contain a layoutinfo");
            }

            return ClrEEClassLayoutInfo.Create(new MemoryReader(new IntPtr((byte*)this.BasePointer + this.Size)));
        }

        // packedfields.inl -> BitVectorGet:291
        private static uint GetField(uint* fieldsBase, EEClassFields field, bool fieldsArePacked)
        {
            if (fieldsArePacked)
                return ClrEEClass.GetPackedField(fieldsBase, field);

            return fieldsBase[(int)field];
        }

        private static uint GetPackedField(uint* fieldsBase, EEClassFields field)
        {
            var offset = 0u;
            for (int i = 0; i < (int)field; i++)
            {
                offset += ClrEEClass.kMaxLengthBits + ClrEEClass.GetBitVector(fieldsBase, (int)offset, ClrEEClass.kMaxLengthBits) + 1;
            }

            var fieldLength = ClrEEClass.GetBitVector(fieldsBase, (int)offset, ClrEEClass.kMaxLengthBits) + 1;
            offset += ClrEEClass.kMaxLengthBits;

            // Grab the field value.
            return ClrEEClass.GetBitVector(fieldsBase, (int)offset, (int)fieldLength);
        }

        /// <summary>
        /// Get the dwLength bits at m_rgPackedFields + dwOffset bits. Value is zero-extended to int size.
        /// </summary>
        /// <param name="offset">The offset.</param>
        /// <param name="length">The length.</param>
        /// <returns></returns>
        private static uint GetBitVector(uint* fieldsBase, int offset, int length)
        {
            // Calculate the start and end naturally aligned ints from which the value will come.
            var startBlock = offset / ClrEEClass.kBitsPerint;
            var endBlock = (offset + length - 1) / ClrEEClass.kBitsPerint;

            if (startBlock == endBlock)
            {
                // Easy case: the new value fits entirely within one aligned int. Compute the number of bits
                // we'll need to shift the extracted value (to the right) and a mask of the bits that will be
                // extracted in the destination int.
                var valueShift = offset % ClrEEClass.kBitsPerint;
                var valueMask = ((1U << length) - 1) << valueShift;

                // Mask out the bits we want and shift them down into the bottom of the result int.
                return (fieldsBase[startBlock] & valueMask) >> valueShift;
            }

            // Hard case: the return value is split across two ints (two ints is the max as the new value
            // can be at most int-sized itself). For simplicity we'll simply break this into two separate
            // non-spanning gets and stitch the result together from that. We can revisit this in the future
            // if the perf is a problem.
            int initialBits = ClrEEClass.kBitsPerint - (offset % ClrEEClass.kBitsPerint); // Number of bits to get in the first int

            // Get the initial (low-order) bits from the first int.
            var bitVector = ClrEEClass.GetBitVector(fieldsBase, offset, initialBits);

            // Get the remaining bits from the second int. These bits will need to be shifted to the left
            // (past the bits we've already read) before being OR'd into the result.
            bitVector |= ClrEEClass.GetBitVector(fieldsBase, offset + initialBits, length - initialBits) << initialBits;

            return bitVector;
        }
    }
}