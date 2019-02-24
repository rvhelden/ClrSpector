using System;
using System.Collections.Generic;
using System.Text;

namespace ClrSpector
{
    public enum EEClassLayoutInfoFlags : byte
    {
        // TRUE if the GC layout of the class is bit-for-bit identical
        // to its unmanaged counterpart (i.e. no internal reference fields,
        // no ansi-unicode char conversions required, etc.) Used to
        // optimize marshaling.
        Blittable = 0x01,

        // Post V1.0 addition: Is this type also sequential in managed memory?
        ManagedSequential = 0x02,

        // When a sequential/explicit type has no fields, it is conceptually
        // zero-sized, but actually is 1 byte in length. This holds onto this
        // fact and allows us to revert the 1 byte of padding when another
        // explicit type inherits from this type.
        ZeroSize = 0x04,

        // The size of the struct is explicitly specified in the meta-data.
        HasExplicitSize = 0x08,
        NativePassInRegisters = 0x10, // Flag wheter a native struct is passed in registers.

        // HFA type of the unmanaged layout
        R4_HFA = 0x10,
        R8_HFA = 0x20,
    };

    public class ClrEEClassLayoutInfo
    {
        public uint NativeSize { get; set; }
        public uint ManagedSize { get; set; }

        // 1,2,4 or 8: this is equal to the largest of the alignment requirements
        // of each of the EEClass's members. If the NStruct extends another NStruct,
        // the base NStruct is treated as the first member for the purpose of
        // this calculation.
        byte LargestAlignmentRequirementOfAllMembers { get; set; }

        // Post V1.0 addition: This is the equivalent of m_LargestAlignmentRequirementOfAllMember
        // for the managed layout.
        byte ManagedLargestAlignmentRequirementOfAllMembers { get; set; }

        public EEClassLayoutInfoFlags Flags { get; set; }
        
        // Packing size in bytes (1, 2, 4, 8 etc.)
        public byte PackingSize { get; set; }

        // # of fields that are of the calltime-marshal variety.
        public uint NumCTMFields { get; set; }

        // An array of FieldMarshaler data blocks, used to drive call-time
        // marshaling of NStruct reference parameters. The number of elements
        // equals m_numCTMFields.
        public IntPtr FieldMarshalers { get; set; }


        public bool IsBlittable => this.Flags.HasFlag(EEClassLayoutInfoFlags.Blittable);

        public static ClrEEClassLayoutInfo Create(MemoryReader reader)
        {
            var layoutInfo = new ClrEEClassLayoutInfo();

            layoutInfo.NativeSize = reader.ReadUInt();
            layoutInfo.ManagedSize = reader.ReadUInt();
            layoutInfo.LargestAlignmentRequirementOfAllMembers = reader.ReadByte();
            layoutInfo.ManagedLargestAlignmentRequirementOfAllMembers = reader.ReadByte();
            layoutInfo.Flags = (EEClassLayoutInfoFlags)reader.ReadByte();
            layoutInfo.PackingSize = reader.ReadByte();
            layoutInfo.NumCTMFields = reader.ReadUInt();
            layoutInfo.FieldMarshalers = reader.ReadIntPtr();

            return layoutInfo;
        }
    }
}
