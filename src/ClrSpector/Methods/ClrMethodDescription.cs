using System;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// A single method as the runtime represents it (a MethodDesc).
    /// </summary>
    public unsafe class ClrMethodDescription : ClrInternalObject
    {
        /// <summary>mdMethodDef - the metadata table that method tokens live in.</summary>
        private const uint MethodDefTokenType = 0x06000000;

        /// <summary>The slot number of this method in the type's vtable.</summary>
        public ushort SlotNumber { get; private set; }

        /// <summary>
        /// This MethodDesc's position within its chunk, in units of MethodDescAlignment.
        /// </summary>
        public byte ChunkIndex { get; private set; }

        public ushort Flags { get; private set; }

        public ushort Flags3AndTokenRemainder { get; private set; }

        public byte EntryPointFlags { get; private set; }

        public IntPtr CodeData { get; private set; }

        /// <summary>
        /// The method's ECMA-335 metadata token.
        /// </summary>
        /// <remarks>
        /// The runtime does not store the whole token on the MethodDesc. The low bits live on
        /// the MethodDesc (as the token remainder) and the high bits on the owning
        /// MethodDescChunk (as the token range), so the token is reassembled from both plus
        /// the MethodDescTokenRemainderBitCount global. Resolving this token through
        /// <see cref="System.Reflection.Module.ResolveMethod(int)"/> is what lets a decoded
        /// MethodDesc be tied back to a name and signature.
        /// </remarks>
        public uint MetadataToken { get; private set; }

        /// <summary>The classification bits that say what kind of MethodDesc this is.</summary>
        public MethodClassification Classification => (MethodClassification)(this.Flags & ClassificationMask);

        internal const ushort ClassificationMask = 0x0007;

        /// <summary>
        /// Flags that widen the MethodDesc beyond its base size; these participate in the
        /// size-table index alongside the classification.
        /// </summary>
        internal const ushort SizeAffectingFlagsMask = 0x0038;

        public static ClrMethodDescription Create(MemoryReader reader, ushort tokenRange, int tokenRemainderBitCount)
        {
            var layout = ContractDescriptor.Current.GetDataType("MethodDesc");

            var md = new ClrMethodDescription();
            md.ClrPointer = reader.Address;
            md.Size = layout.Size ?? 0;

            md.Flags3AndTokenRemainder = reader.ReadUShort(layout["Flags3AndTokenRemainder"]);
            md.ChunkIndex = reader.ReadByte(layout["ChunkIndex"]);
            md.EntryPointFlags = reader.ReadByte(layout["EntryPointFlags"]);
            md.SlotNumber = reader.ReadUShort(layout["Slot"]);
            md.Flags = reader.ReadUShort(layout["Flags"]);
            md.CodeData = reader.ReadIntPtr(layout["CodeData"]);

            var remainderMask = (ushort)((1 << tokenRemainderBitCount) - 1);
            var remainder = (uint)(md.Flags3AndTokenRemainder & remainderMask);
            md.MetadataToken = MethodDefTokenType | ((uint)tokenRange << tokenRemainderBitCount) | remainder;

            return md;
        }

        public override string ToString()
        {
            return $"MethodDesc @0x{this.ClrPointer.ToInt64():x} slot={this.SlotNumber} token=0x{this.MetadataToken:x8}";
        }
    }

    /// <summary>
    /// What kind of MethodDesc this is; the low bits of MethodDesc.Flags.
    /// </summary>
    public enum MethodClassification
    {
        IL = 0,
        FCall = 1,
        PInvoke = 2,
        EEImpl = 3,
        Array = 4,
        Instantiated = 5,
        ComInterop = 6,
        Dynamic = 7
    }
}
