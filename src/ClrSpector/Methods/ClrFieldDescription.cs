using System;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// A single field as the runtime represents it (a FieldDesc): where it sits in an instance,
    /// what element type it is, and how it is stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The offset is the interesting part. The runtime is free to lay a type's fields out in
    /// whatever order suits it - packing smaller fields into gaps, hoisting references together
    /// for the GC - and reflection does not report where anything landed. This does.
    /// </para>
    /// <para>
    /// A FieldDesc is only 16 bytes, so almost everything is packed into two words:
    /// <c>DWord1</c> carries the metadata token in its low 24 bits with the storage flags above
    /// it, and <c>DWord2</c> carries a 27-bit offset with the 5-bit element type above that. The
    /// masks come from the runtime's own reader, not from field.h being transcribed here.
    /// </para>
    /// </remarks>
    public sealed unsafe class ClrFieldDescription : ClrInternalObject
    {
        /// <summary>mdFieldDef - the metadata table that field tokens live in.</summary>
        private const uint FieldDefTokenType = 0x04000000;

        /// <summary>DWord1: the token's row id occupies the low 24 bits.</summary>
        private const uint TokenMask = 0x00FFFFFF;

        private const uint IsStaticFlag = 0x01000000;
        private const uint IsThreadStaticFlag = 0x02000000;
        private const uint IsRvaFlag = 0x04000000;

        /// <summary>DWord2: the offset occupies the low 27 bits.</summary>
        private const uint OffsetMask = 0x07FFFFFF;

        /// <summary>DWord2: the element type occupies the top 5 bits.</summary>
        private const uint ElementTypeMask = 0xF8000000;

        private const int ElementTypeShift = 27;

        /// <summary>The MethodTable of the type that declares this field.</summary>
        public IntPtr MethodTableOfEnclosingClass { get; private set; }

        /// <summary>
        /// The field's ECMA-335 FieldDef token. Resolving it through
        /// <see cref="System.Reflection.Module.ResolveField(int)"/> is what gives the field a
        /// name - a FieldDesc stores none.
        /// </summary>
        public uint MetadataToken { get; private set; }

        /// <summary>
        /// Where the field sits: for an instance field, the byte offset from the start of the
        /// object's data - which is the object's address plus one pointer, since the MethodTable
        /// pointer comes first. For a static field, an offset into its statics block instead.
        /// </summary>
        public uint Offset { get; private set; }

        /// <summary>The element type the runtime records for this field.</summary>
        public CorElementType ElementType { get; private set; }

        public bool IsStatic { get; private set; }

        /// <summary>True for a <c>[ThreadStatic]</c> field, which lives in per-thread storage.</summary>
        public bool IsThreadStatic { get; private set; }

        /// <summary>
        /// True for a field whose data lives in the module image rather than in a statics block -
        /// how the compiler stores a large initialised array.
        /// </summary>
        public bool IsRva { get; private set; }

        /// <summary>
        /// True when <see cref="Offset"/> is the sentinel meaning "the real offset is the field's
        /// RVA in metadata", which only metadata can answer.
        /// </summary>
        public bool HasBigRvaOffset { get; private set; }

        /// <summary>
        /// The raw first word, exposed because it holds flags this library does not name.
        /// </summary>
        public uint Word1 { get; private set; }

        /// <summary>The raw second word.</summary>
        public uint Word2 { get; private set; }

        public static ClrFieldDescription Create(MemoryReader reader)
        {
            var descriptor = ContractDescriptor.Current;
            var layout = descriptor.GetDataType("FieldDesc");

            var field = new ClrFieldDescription();
            field.ClrPointer = reader.Address;
            field.Size = layout.Size ?? 0;

            field.Word1 = reader.ReadUInt(layout["DWord1"]);
            field.Word2 = reader.ReadUInt(layout["DWord2"]);
            field.MethodTableOfEnclosingClass = reader.ReadIntPtr(layout["MTOfEnclosingClass"]);

            field.MetadataToken = FieldDefTokenType | (field.Word1 & TokenMask);
            field.IsStatic = (field.Word1 & IsStaticFlag) != 0;
            field.IsThreadStatic = (field.Word1 & IsThreadStaticFlag) != 0;
            field.IsRva = (field.Word1 & IsRvaFlag) != 0;

            field.Offset = field.Word2 & OffsetMask;
            field.ElementType = (CorElementType)((field.Word2 & ElementTypeMask) >> ElementTypeShift);

            // The runtime stores a sentinel offset for a field too large to encode in 27 bits.
            if (descriptor.Globals.Has("FieldOffsetBigRVA"))
                field.HasBigRvaOffset = field.Offset == (uint)descriptor.Globals.Number("FieldOffsetBigRVA");

            return field;
        }

        public override string ToString()
        {
            var storage = this.IsThreadStatic ? " threadstatic"
                : this.IsStatic ? " static"
                : string.Empty;
            var rva = this.IsRva ? " rva" : string.Empty;

            return $"FieldDesc @0x{this.ClrPointer.ToInt64():x} token=0x{this.MetadataToken:x8} " +
                   $"offset={this.Offset} type={this.ElementType}{storage}{rva}";
        }
    }
}
