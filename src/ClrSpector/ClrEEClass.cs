using System;

namespace ClrSpector
{
    public unsafe class ClrEEClass
    {
        public const int kMaxLengthBits = 5;  // Number of bits needed to express the maximum length of a field (32-bits)
        public const int kBitsPerint = 32; // Number of bits in a int

        public void* BasePointer { get; set; }
        public uint Size { get; set; }

        public IntPtr GuidInfo { get; set; }
        public IntPtr OptionalFields { get; set; }
        public IntPtr MethodTablePointer { get; set; }
        public ClrMethodTable MethodTable => this.MethodTablePointer == IntPtr.Zero ? null : ClrMethodTable.Create(new MemoryReader(this.MethodTablePointer));
        public IntPtr FieldDescList { get; set; }
        public IntPtr MethodDescChunks { get; set; }

        public IntPtr NativeSize { get; set; } // valid only if EEClass::IsBlittable() or EEClass::HasLayout() is true
        public IntPtr ComCallableWrapper { get; set; } // points to interop data structures used when this type is exposed to COM

        public uint AttrClass { get; set; }
        public VmFlags VmFlags { get; set; }
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

        public static ClrEEClass Create(MemoryReader reader)
        {
            var eeclass = new ClrEEClass();
            eeclass.BasePointer = reader.BasePointer;

            eeclass.GuidInfo = reader.ReadIntPtr();
            eeclass.OptionalFields = reader.ReadIntPtr();
            eeclass.MethodTablePointer = reader.ReadIntPtr();
            eeclass.FieldDescList = reader.ReadIntPtr();
            eeclass.MethodDescChunks = reader.ReadIntPtr();

            eeclass.NativeSize = reader.ReadIntPtr();
            eeclass.ComCallableWrapper = reader.ReadIntPtr();

            eeclass.AttrClass = reader.ReadUInt();
            eeclass.VmFlags = (VmFlags)reader.ReadUInt();

            eeclass.NormType = reader.ReadByte();
            eeclass.FieldsArePacked = reader.ReadByte() == 0x01;
            eeclass.FixedEEClassFields = reader.ReadByte();
            eeclass.BaseSizePadding = reader.ReadByte();

            eeclass.Size = reader.Position;

            var fieldsBase = (uint*)((byte*)reader.BasePointer + eeclass.FixedEEClassFields);

            eeclass.NumberOfInstanceFields = GetField(fieldsBase, EEClassFields.NumInstanceFields, eeclass.FieldsArePacked);
            eeclass.NumberOfMethods = GetField(fieldsBase, EEClassFields.NumMethods, eeclass.FieldsArePacked);
            eeclass.NumberOfStaticFields = GetField(fieldsBase, EEClassFields.NumStaticFields, eeclass.FieldsArePacked);
            eeclass.NumberOfHandleStatics = GetField(fieldsBase, EEClassFields.NumHandleStatics, eeclass.FieldsArePacked);
            eeclass.NumberOfBoxedStatics = GetField(fieldsBase, EEClassFields.NumBoxedStatics, eeclass.FieldsArePacked);
            eeclass.NonGcStaticFieldBytes = GetField(fieldsBase, EEClassFields.NonGCStaticFieldBytes, eeclass.FieldsArePacked);
            eeclass.NumberOfThreadStaticFields = GetField(fieldsBase, EEClassFields.NumThreadStaticFields, eeclass.FieldsArePacked);
            eeclass.NumberOfHandleThreadStatics = GetField(fieldsBase, EEClassFields.NumHandleThreadStatics, eeclass.FieldsArePacked);
            eeclass.NumberOfBoxedThreadStatics = GetField(fieldsBase, EEClassFields.NumBoxedThreadStatics, eeclass.FieldsArePacked);
            eeclass.NonGcThreadStaticFieldBytes = GetField(fieldsBase, EEClassFields.NonGCThreadStaticFieldBytes, eeclass.FieldsArePacked);
            eeclass.NumberOfNonVirtualSlots = GetField(fieldsBase, EEClassFields.NumNonVirtualSlots, eeclass.FieldsArePacked);
            eeclass.Count = GetField(fieldsBase, EEClassFields.Count, eeclass.FieldsArePacked);
            
            return eeclass;
        }

        private static uint GetField(uint* fieldsBase, EEClassFields field, bool fieldsArePacked)
        {
            if (fieldsArePacked)
                return GetPackedField(fieldsBase, field);

            return fieldsBase[(int)field];
        }

        private static uint GetPackedField(uint* fieldsBase, EEClassFields field)
        {
            var offset = 0u;
            for (int i = 0; i < (int)field; i++)
                offset += ClrEEClass.kMaxLengthBits + ClrEEClass.GetBitVector(fieldsBase, (int)offset, kMaxLengthBits) + 1;

            var fieldLength = ClrEEClass.GetBitVector(fieldsBase, (int)offset, kMaxLengthBits) + 1;
            offset += kMaxLengthBits;

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
            var startBlock = offset / kBitsPerint;
            var endBlock = (offset + length - 1) / kBitsPerint;

            if (startBlock == endBlock)
            {
                // Easy case: the new value fits entirely within one aligned int. Compute the number of bits
                // we'll need to shift the extracted value (to the right) and a mask of the bits that will be
                // extracted in the destination int.
                var valueShift = offset % kBitsPerint;
                var valueMask = ((1U << length) - 1) << valueShift;

                // Mask out the bits we want and shift them down into the bottom of the result int.
                return (fieldsBase[startBlock] & valueMask) >> valueShift;
            }
            else
            {
                // Hard case: the return value is split across two ints (two ints is the max as the new value
                // can be at most int-sized itself). For simplicity we'll simply break this into two separate
                // non-spanning gets and stitch the result together from that. We can revisit this in the future
                // if the perf is a problem.
                int initialBits = kBitsPerint - (offset % kBitsPerint); // Number of bits to get in the first int

                // Get the initial (low-order) bits from the first int.
                var bitVector = ClrEEClass.GetBitVector(fieldsBase, offset, initialBits);

                // Get the remaining bits from the second int. These bits will need to be shifted to the left
                // (past the bits we've already read) before being OR'd into the result.
                bitVector |= ClrEEClass.GetBitVector(fieldsBase, offset + initialBits, length - initialBits) << initialBits;

                return bitVector;
            }
        }
    }
}