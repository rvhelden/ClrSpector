using System;
using System.Reflection;
using System.Runtime.CompilerServices;
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

        /// <summary>
        /// This method's code-versioning state - the tier its code is at, and any alternate
        /// versions - or null when the runtime has never prepared the method.
        /// </summary>
        /// <remarks>
        /// Read on demand rather than eagerly, since it follows several pointers per method and
        /// every method of a type gets decoded when its MethodTable does.
        /// </remarks>
        public ClrCodeVersionState CodeVersions => ClrCodeVersionState.Of(this);

        /// <summary>
        /// The type arguments of a generic method instantiation, as MethodTable addresses, or an
        /// empty array for anything else.
        /// </summary>
        /// <remarks>
        /// Only an <see cref="MethodClassification.Instantiated"/> MethodDesc carries these, in
        /// an array hanging off its PerInstInfo. A shared instantiation reports
        /// <c>System.__Canon</c> for its reference arguments, which is the honest answer - that is
        /// the instantiation the code was compiled for.
        /// </remarks>
        public IntPtr[] GenericArgumentMethodTables
        {
            get
            {
                if (this.Classification != MethodClassification.Instantiated)
                    return Array.Empty<IntPtr>();

                var layout = ContractDescriptor.Current.GetDataType("InstantiatedMethodDesc");
                var reader = new MemoryReader(this.ClrPointer);

                var count = reader.ReadUShort(layout["NumGenericArgs"]);
                var perInstInfo = reader.ReadIntPtr(layout["PerInstInfo"]);

                if (count == 0 || perInstInfo == IntPtr.Zero)
                    return Array.Empty<IntPtr>();

                var arguments = new IntPtr[count];
                var arrayReader = new MemoryReader(perInstInfo);

                for (var i = 0; i < count; i++)
                    arguments[i] = arrayReader.ReadIntPtr(i * IntPtr.Size);

                return arguments;
            }
        }

        /// <summary>
        /// This MethodDesc as a runtime handle. A MethodDesc address is exactly what a
        /// <see cref="RuntimeMethodHandle"/> wraps, which is the bridge between this library's
        /// view of a method and the runtime's own.
        /// </summary>
        public RuntimeMethodHandle Handle => RuntimeMethodHandle.FromIntPtr(this.ClrPointer);

        /// <summary>
        /// The method's stable entry point, jitting it first if need be.
        /// </summary>
        /// <remarks>
        /// Goes through the handle rather than a <see cref="MethodBase"/>, so nothing here needs
        /// reflection to have produced the method.
        /// </remarks>
        public IntPtr EntryPoint => this.Prepare().GetFunctionPointer();

        /// <summary>
        /// Jits the method if it has not been jitted, and hands back its handle.
        /// </summary>
        /// <remarks>
        /// The MethodDesc equivalent of
        /// <c>RuntimeHelpers.PrepareMethod(typeof(T).GetMethod(name).MethodHandle)</c>, with
        /// neither the <see cref="Type"/> nor the <see cref="MethodBase"/> needed to reach it.
        /// </remarks>
        public RuntimeMethodHandle Prepare()
        {
            var handle = this.Handle;
            RuntimeHelpers.PrepareMethod(handle);

            return handle;
        }

        /// <summary>
        /// The reflection method this MethodDesc is, or null when it will not resolve.
        /// </summary>
        /// <remarks>
        /// The escape hatch back to reflection, for the things only a signature can answer -
        /// comparing two methods' parameters, or emitting one shaped like this. Everything else
        /// in this library works from the MethodDesc directly.
        /// </remarks>
        public MethodBase Method
        {
            get
            {
                try
                {
                    return MethodBase.GetMethodFromHandle(this.Handle);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

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

        /// <summary>
        /// The MethodTable of the type that declares this method, taken from the chunk it lives
        /// in. Zero when the MethodDesc was decoded on its own rather than through its type.
        /// </summary>
        public IntPtr MethodTablePointer { get; internal set; }

        /// <summary>
        /// This method's name, read from its module's metadata rather than from reflection.
        /// </summary>
        /// <remarks>
        /// A MethodDesc stores no name - only a token. This resolves that token against the
        /// metadata in the mapped module image, so it needs no <see cref="System.Type"/> or
        /// <see cref="System.Reflection.MethodBase"/> to exist. Null when the declaring module
        /// has no mapped image, as a runtime-generated one does not.
        /// </remarks>
        public string Name => this.Metadata?.MethodName(this.MetadataToken);

        /// <summary>
        /// The declaring type's full name, from metadata. Null when the module has no image.
        /// </summary>
        public string DeclaringTypeName
        {
            get
            {
                var methodTable = this.DeclaringMethodTable;

                return methodTable == null ? null : this.Metadata?.FullTypeName(methodTable.TypeDefToken);
            }
        }

        /// <summary>The decoded type that declares this method, or null when it is not known.</summary>
        public ClrMethodTable DeclaringMethodTable =>
            this.MethodTablePointer == IntPtr.Zero
                ? null
                : ClrMethodTable.Create(new MemoryReader(this.MethodTablePointer));

        /// <summary>The metadata of the module that declares this method.</summary>
        public ClrModuleMetadata Metadata
        {
            get
            {
                var methodTable = this.DeclaringMethodTable;
                if (methodTable == null || methodTable.Module == IntPtr.Zero)
                    return null;

                return ClrModuleMetadata.AtImageBase(ClrModule.At(methodTable.Module).Base);
            }
        }

        /// <summary>
        /// This method's IL, read out of the module image, or null when it has no body.
        /// </summary>
        /// <remarks>
        /// The token gives the body's relative virtual address; the image is mapped, so that is
        /// an offset from the module base. Nothing here goes through
        /// <see cref="System.Reflection.MethodBody"/>.
        /// </remarks>
        public ClrMethodBodyImage ReadIl()
        {
            var methodTable = this.DeclaringMethodTable;
            if (methodTable == null || methodTable.Module == IntPtr.Zero)
                return null;

            var imageBase = ClrModule.At(methodTable.Module).Base;
            var metadata = ClrModuleMetadata.AtImageBase(imageBase);

            var rva = metadata?.MethodBodyRva(this.MetadataToken) ?? 0;

            return rva == 0 ? null : ClrMethodBodyImage.Read(imageBase, rva);
        }

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
            var name = this.Name;
            var described = name == null
                ? string.Empty
                : $" {this.DeclaringTypeName}::{name}";

            return $"MethodDesc @0x{this.ClrPointer.ToInt64():x}{described} " +
                   $"slot={this.SlotNumber} token=0x{this.MetadataToken:x8}";
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
