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

        /// <summary>
        /// True once the method has an address that will not change - either a precode or its
        /// final code.
        /// </summary>
        public bool HasStableEntryPoint => (this.Flags3AndTokenRemainder & HasStableEntryPointFlag) != 0;

        /// <summary>
        /// True when a precode has been allocated, which is what gives a redirect a single slot
        /// to patch. Implies <see cref="HasStableEntryPoint"/>.
        /// </summary>
        public bool HasPrecode => (this.Flags3AndTokenRemainder & HasPrecodeFlag) != 0;

        /// <summary>
        /// True for the stub that unwraps a boxed value type before calling the real method.
        /// </summary>
        public bool IsUnboxingStub => (this.Flags3AndTokenRemainder & IsUnboxingStubFlag) != 0;

        /// <summary>
        /// True when tiered compilation may recompile this method and rewrite its dispatch slot.
        /// </summary>
        /// <remarks>
        /// This is the flag that decides whether a redirect can hold. Promotion to optimised code
        /// writes the same slot a detour patches, silently dropping it - so
        /// <see cref="Detours.MethodDetour"/> refuses an eligible target rather than letting the
        /// redirect evaporate mid-test. Measured: with tiering off no method in CoreLib carries
        /// the flag; with it on, 10424 of 11621 sampled do.
        /// </remarks>
        public bool IsEligibleForTieredCompilation =>
            (this.Flags3AndTokenRemainder & IsEligibleForTieredCompilationFlag) != 0;

        /// <summary>True when a slot holding this method's entry point trails the MethodDesc.</summary>
        public bool HasNonVtableSlot => (this.Flags & HasNonVtableSlotFlag) != 0;

        /// <summary>True when a MethodImpl structure trails the MethodDesc.</summary>
        public bool HasMethodImpl => (this.Flags & HasMethodImplFlag) != 0;

        /// <summary>True when a native code slot trails the MethodDesc.</summary>
        public bool HasNativeCodeSlot => (this.Flags & HasNativeCodeSlotFlag) != 0;

        /// <summary>True when async method data trails the MethodDesc.</summary>
        public bool HasAsyncMethodData => (this.Flags & HasAsyncMethodDataFlag) != 0;

        /// <summary>True for a static method.</summary>
        public bool IsStatic => (this.Flags & StaticFlag) != 0;

        /// <summary>
        /// The slot holding this method's entry point, for a method that has one of its own
        /// rather than a vtable entry - or <see cref="IntPtr.Zero"/> when it has neither.
        /// </summary>
        /// <remarks>
        /// This is not the MethodTable's non-virtual slot array; it is a pointer packed in
        /// immediately after the MethodDesc itself. A static or non-virtual method typically
        /// dispatches through here rather than through any vtable, which is why
        /// <see cref="Detours.MethodVtable.FindSlot"/> finds nothing for one.
        /// </remarks>
        public IntPtr NonVtableSlotAddress =>
            this.HasNonVtableSlot ? this.OptionalSlotsStart : IntPtr.Zero;

        /// <summary>
        /// The slot holding the address of this method's jitted code, or
        /// <see cref="IntPtr.Zero"/> when it has none.
        /// </summary>
        public IntPtr NativeCodeSlotAddress
        {
            get
            {
                if (!this.HasNativeCodeSlot)
                    return IntPtr.Zero;

                var sizes = MethodDescSizes.Current;
                var offset = 0L;

                // The optional slots are packed in a fixed order, so reaching one means stepping
                // over exactly those before it that are present.
                if (this.HasNonVtableSlot)
                    offset += sizes.NonVtableSlotSize;

                if (this.HasMethodImpl)
                    offset += sizes.MethodImplSize;

                return this.OptionalSlotsStart + (int)offset;
            }
        }

        /// <summary>Where this MethodDesc's optional trailing slots begin.</summary>
        private IntPtr OptionalSlotsStart =>
            this.ClrPointer + (int)MethodDescSizes.Current.BaseSizeOf(this.Flags);

        /// <summary>The classification bits that say what kind of MethodDesc this is.</summary>
        public MethodClassification Classification => (MethodClassification)(this.Flags & ClassificationMask);

        internal const ushort ClassificationMask = 0x0007;

        /// <summary>
        /// Flags that widen the MethodDesc beyond its base size; these participate in the
        /// size-table index alongside the classification.
        /// </summary>
        internal const ushort SizeAffectingFlagsMask = 0x0078;

        /// <summary>
        /// Flags3AndTokenRemainder bits above the token remainder. The remainder occupies the low
        /// <c>MethodDescTokenRemainderBitCount</c> bits, so these never collide with it.
        /// </summary>
        private const ushort HasStableEntryPointFlag = 0x1000;

        private const ushort HasPrecodeFlag = 0x2000;
        private const ushort IsUnboxingStubFlag = 0x4000;
        private const ushort IsEligibleForTieredCompilationFlag = 0x8000;

        /// <summary>Flags bits marking which optional slots trail the MethodDesc.</summary>
        private const ushort HasNonVtableSlotFlag = 0x0008;

        private const ushort HasMethodImplFlag = 0x0010;
        private const ushort HasNativeCodeSlotFlag = 0x0020;
        private const ushort HasAsyncMethodDataFlag = 0x0040;
        private const ushort StaticFlag = 0x0080;

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
