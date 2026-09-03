using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// A ceiling on a stored signature's length, since it is read out of memory rather than
        /// bounded by anything. Well past any real signature.
        /// </summary>
        private const uint MaximumStoredSignature = 64 * 1024;

        /// <summary>
        /// The token-range bits of MethodDescChunk.FlagsAndTokenRange; the rest are flags.
        /// </summary>
        private const ushort TokenRangeMask = 0x0FFF;

        /// <summary>The kind bits of InstantiatedMethodDesc.Flags2.</summary>
        private const ushort GenericKindMask = 0x0007;

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
        public string Name => this.Metadata?.MethodName(this.MetadataToken) ?? this.StoredName;

        /// <summary>
        /// The custom attributes applied to this method, decoded from metadata.
        /// </summary>
        /// <remarks>
        /// Empty for a method with no metadata row of its own - an array accessor or an emitted
        /// method - since there is nowhere for an attribute to have been recorded. An
        /// instantiation reports what was written on the open definition, which is the only place
        /// an attribute can be written.
        /// </remarks>
        public IReadOnlyList<ClrCustomAttribute> CustomAttributes =>
            this.Metadata?.CustomAttributes((int)this.MetadataToken)
            ?? (IReadOnlyList<ClrCustomAttribute>)new ClrCustomAttribute[0];

        /// <summary>
        /// The implementation flags the MethodDef row carries, which <c>[MethodImpl]</c> compiles
        /// into.
        /// </summary>
        /// <remarks>
        /// Read from column 1 of the row directly. Zero when there is no row to read.
        /// </remarks>
        public ushort ImplementationFlags
        {
            get
            {
                var metadata = this.Metadata;

                if (metadata == null)
                    return 0;

                var rowId = this.MetadataToken & 0x00FFFFFF;

                if (rowId == 0 || rowId > (uint)metadata.Image.RowCount(MetadataTable.MethodDef))
                    return 0;

                // MethodDef: RVA, ImplFlags, Flags, Name, Signature, ParamList.
                return (ushort)metadata.Image.ReadColumn(MetadataTable.MethodDef, rowId, 1);
            }
        }

        /// <summary>
        /// The attributes that were applied in source but were compiled into the MethodDef row's
        /// bits rather than into a CustomAttribute row.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>[MethodImpl]</c> is a <i>pseudo-custom attribute</i> (ECMA-335 II.21): the compiler
        /// folds it into <see cref="ImplementationFlags"/> and writes nothing to the
        /// CustomAttribute table, so <see cref="CustomAttributes"/> cannot see it however
        /// carefully it looks. Reflection synthesises it back on the way out, which is why
        /// <c>GetCustomAttributesData</c> reports it.
        /// </para>
        /// <para>
        /// Kept separate from <see cref="CustomAttributes"/> rather than mixed into it, so that
        /// "was read from a row" and "was reconstructed from flags" stay distinguishable -
        /// everything here has <see cref="ClrCustomAttribute.IsSynthesised"/> set.
        /// <see cref="AllAttributes"/> is the combined, reflection-equivalent view.
        /// </para>
        /// </remarks>
        public IReadOnlyList<ClrCustomAttribute> PseudoCustomAttributes
        {
            get
            {
                var flags = this.ImplementationFlags;

                return flags == 0
                    ? (IReadOnlyList<ClrCustomAttribute>)new ClrCustomAttribute[0]
                    : ClrCustomAttribute.OfImplementationFlags(flags).ToList();
            }
        }

        /// <summary>
        /// Every attribute source applied to this method: the rows, then the ones reconstructed
        /// from the row's own flags.
        /// </summary>
        public IReadOnlyList<ClrCustomAttribute> AllAttributes =>
            this.CustomAttributes.Concat(this.PseudoCustomAttributes).ToList();

        /// <summary>
        /// True when this MethodDesc carries its own signature instead of pointing at a metadata
        /// row - a dynamic method, an array accessor, or a delegate's runtime-implemented method.
        /// </summary>
        /// <remarks>
        /// These are the MethodDescs whose token names nothing useful. A dynamic method was never
        /// in any module's tables, and an array's Get, Set and Address are synthesised by the
        /// runtime for each array type rather than declared anywhere - so the runtime stores the
        /// signature blob on the MethodDesc itself, in the StoredSigMethodDesc that these three
        /// classifications extend.
        /// </remarks>
        public bool HasStoredSignature =>
            this.Classification == MethodClassification.EEImpl
            || this.Classification == MethodClassification.Array
            || this.Classification == MethodClassification.Dynamic;

        /// <summary>
        /// The raw ExtendedFlags of a MethodDesc that stores its own signature.
        /// </summary>
        /// <remarks>
        /// These distinguish the varieties of runtime-made method - an LCG method from an IL
        /// stub, and one kind of stub from another. The descriptor publishes the field but not
        /// the meaning of its bits, so the value is surfaced as it stands rather than decoded
        /// into names this build cannot verify.
        /// </remarks>
        public uint StoredSignatureFlags
        {
            get
            {
                if (!this.HasStoredSignature)
                    return 0;

                var descriptor = ContractDescriptor.Current;

                return descriptor.TryGetDataType("StoredSigMethodDesc", out var layout)
                       && layout.HasField("ExtendedFlags")
                    ? new MemoryReader(this.ClrPointer).ReadUInt(layout["ExtendedFlags"])
                    : 0;
            }
        }

        /// <summary>
        /// What kind of generic method this is, for an instantiated MethodDesc.
        /// </summary>
        /// <remarks>
        /// Read from InstantiatedMethodDesc.Flags2. All four kinds have been observed against a
        /// live runtime, by enumerating <see cref="ClrModule.InstantiatedMethods"/>, and they
        /// behave as their names claim: a value type argument produces
        /// <see cref="GenericMethodKind.UnsharedMethodInstantiation"/>, a <c>__Canon</c> argument
        /// <see cref="GenericMethodKind.SharedMethodInstantiation"/>, the concrete reference
        /// instantiation that wraps it
        /// <see cref="GenericMethodKind.WrapperStubWithInstantiations"/>, and the open definition
        /// found in a type's own chunks
        /// <see cref="GenericMethodKind.GenericMethodDefinition"/>.
        /// </remarks>
        public GenericMethodKind GenericKind
        {
            get
            {
                if (this.Classification != MethodClassification.Instantiated)
                    return GenericMethodKind.NotGeneric;

                var descriptor = ContractDescriptor.Current;
                if (!descriptor.TryGetDataType("InstantiatedMethodDesc", out var layout)
                    || !layout.HasField("Flags2"))
                {
                    return GenericMethodKind.NotGeneric;
                }

                var flags = new MemoryReader(this.ClrPointer).ReadUShort(layout["Flags2"]);

                return (GenericMethodKind)(flags & GenericKindMask);
            }
        }

        /// <summary>The raw Flags2 of an instantiated MethodDesc; zero for anything else.</summary>
        public ushort InstantiationFlags
        {
            get
            {
                if (this.Classification != MethodClassification.Instantiated)
                    return 0;

                var descriptor = ContractDescriptor.Current;

                return descriptor.TryGetDataType("InstantiatedMethodDesc", out var layout)
                       && layout.HasField("Flags2")
                    ? new MemoryReader(this.ClrPointer).ReadUShort(layout["Flags2"])
                    : (ushort)0;
            }
        }

        /// <summary>
        /// True for the open definition of a generic method - Echo&lt;T&gt; rather than
        /// Echo&lt;int&gt;.
        /// </summary>
        /// <remarks>
        /// These are the generic methods a MethodTable's chunks hold. The instantiations are not
        /// there at all - they live in the module's side table, which
        /// <see cref="ClrModule.InstantiatedMethods"/> enumerates.
        /// </remarks>
        public bool IsGenericMethodDefinition =>
            this.GenericKind == GenericMethodKind.GenericMethodDefinition;

        /// <summary>
        /// This method's signature with the declaring type's and the method's own type arguments
        /// substituted in, or the open signature when there is nothing to substitute.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is what <see cref="Signature"/> cannot be on its own: metadata records the open
        /// definition, so closing it needs the instantiation, which lives on the MethodTable
        /// (its PerInstInfo) and on the MethodDesc (its own PerInstInfo). A type variable with no
        /// real type behind it is left alone rather than guessed at, so the open definition of a
        /// generic type closes to itself.
        /// </para>
        /// <para>
        /// It closes against the <b>declaring</b> MethodTable, which for a shared instantiation
        /// is the canonical one - so <c>List&lt;string&gt;.Add</c> comes back as taking
        /// <c>System.__Canon</c> rather than <c>System.String</c>. That is not a shortfall: one
        /// body of code serves every reference instantiation, and <c>__Canon</c> is the type it
        /// was compiled against. Measured: <c>List&lt;int&gt;.Add</c> closes to
        /// <c>System.Int32</c>, because a value type argument gets its own code.
        /// </para>
        /// <para>
        /// To close against one exact instantiation instead, substitute that MethodTable's own
        /// <see cref="ClrMethodTable.TypeArguments"/>:
        /// <c>method.Signature.WithArguments(..., null)</c> - see
        /// <see cref="ClosedSignatureFor(ClrMethodTable)"/>, which does it for you.
        /// </para>
        /// </remarks>
        public ClrMethodSignature ClosedSignature
        {
            get
            {
                var signature = this.Signature;
                if (signature == null)
                    return null;

                return signature.WithArguments(
                    Resolve(this.DeclaringMethodTable?.TypeArguments),
                    Resolve(this.GenericArgumentMethodTables));
            }
        }

        /// <summary>
        /// This method's signature closed against one exact instantiation, rather than against
        /// the canonical MethodTable its code is shared through.
        /// </summary>
        /// <remarks>
        /// The difference matters for reference instantiations. <see cref="ClosedSignature"/>
        /// reports what the code was compiled against, which is <c>System.__Canon</c>; passing
        /// <c>List&lt;string&gt;</c>'s MethodTable here reports <c>System.String</c>, which is
        /// what a caller of that instantiation actually passes.
        /// </remarks>
        public ClrMethodSignature ClosedSignatureFor(ClrMethodTable instantiation)
        {
            var signature = this.Signature;

            if (signature == null)
                return null;

            return signature.WithArguments(
                Resolve(instantiation?.TypeArguments),
                Resolve(this.GenericArgumentMethodTables));
        }

        /// <summary>
        /// Turns type handles into signature nodes, dropping the ones that are not MethodTables.
        /// </summary>
        /// <remarks>
        /// A type variable or a TypeDesc has no MethodTable to name, so its slot is left null and
        /// substitution leaves that parameter as it was.
        /// </remarks>
        private static ClrSignatureType[] Resolve(IntPtr[] handles)
        {
            if (handles == null || handles.Length == 0)
                return null;

            var resolved = new ClrSignatureType[handles.Length];

            for (var i = 0; i < handles.Length; i++)
            {
                if (!ClrMethodTable.IsMethodTableHandle(handles[i]))
                    continue;

                try
                {
                    resolved[i] = ClrSignatureType.ForMethodTable(
                        ClrMethodTable.Create(new MemoryReader(handles[i])));
                }
                catch (Exception)
                {
                    // A handle that will not decode is left unsubstituted rather than fatal.
                }
            }

            return resolved;
        }

        /// <summary>The address of the signature blob stored on this MethodDesc, or zero.</summary>
        public IntPtr StoredSignatureAddress => this.StoredSignature().Address;

        /// <summary>The length of the stored signature blob, or zero.</summary>
        public uint StoredSignatureLength => this.StoredSignature().Length;

        /// <summary>
        /// The name a dynamic method carries on its own MethodDesc, or null for anything else.
        /// </summary>
        /// <remarks>
        /// A dynamic method has no MethodDef row, so there is no string heap to look its name up
        /// in - the DynamicMethodDesc holds a plain UTF-8 pointer instead. This is what lets an
        /// emitted method be named at all.
        /// </remarks>
        public string StoredName
        {
            get
            {
                if (this.Classification != MethodClassification.Dynamic)
                    return null;

                var descriptor = ContractDescriptor.Current;
                if (!descriptor.TryGetDataType("DynamicMethodDesc", out var layout)
                    || !layout.HasField("MethodName"))
                {
                    return null;
                }

                var name = new MemoryReader(this.ClrPointer).ReadIntPtr(layout["MethodName"]);

                return name == IntPtr.Zero || !ProcessMemoryRegions.IsReadable(name, 1)
                    ? null
                    : new MemoryReader(name).ReadNullTerminatedString(0);
            }
        }

        /// <summary>
        /// The signature blob this MethodDesc stores, when it stores one.
        /// </summary>
        /// <remarks>
        /// Guarded rather than trusted. The fields are only meaningful for the classifications
        /// that extend StoredSigMethodDesc, and reading a pointer out of a MethodDesc that does
        /// not have one would be an access violation rather than a wrong answer - so both the
        /// classification and the pointer itself are checked.
        /// </remarks>
        private (IntPtr Address, uint Length) StoredSignature()
        {
            if (!this.HasStoredSignature)
                return (IntPtr.Zero, 0);

            var descriptor = ContractDescriptor.Current;
            if (!descriptor.TryGetDataType("StoredSigMethodDesc", out var layout)
                || !layout.HasField("Sig")
                || !layout.HasField("cSig"))
            {
                return (IntPtr.Zero, 0);
            }

            var reader = new MemoryReader(this.ClrPointer);
            var signature = reader.ReadIntPtr(layout["Sig"]);
            var length = reader.ReadUInt(layout["cSig"]);

            if (signature == IntPtr.Zero || length == 0 || length > MaximumStoredSignature)
                return (IntPtr.Zero, 0);

            return ProcessMemoryRegions.IsReadable(signature, length)
                ? (signature, length)
                : (IntPtr.Zero, 0);
        }

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
        /// <remarks>
        /// The pointer is checked before it is followed. A MethodDesc reached from a bare address
        /// or a side table can carry one that does not point at a MethodTable, and decoding that
        /// would be an access violation rather than a wrong answer - which takes the process down
        /// instead of failing the call.
        /// </remarks>
        public ClrMethodTable DeclaringMethodTable
        {
            get
            {
                var table = this.MethodTablePointer;

                if (!ClrMethodTable.IsMethodTableHandle(table))
                    return null;

                var descriptor = ContractDescriptor.Current;
                var size = descriptor.GetDataType("MethodTable").Size ?? (uint)IntPtr.Size;

                return ProcessMemoryRegions.IsReadable(table, size)
                    ? ClrMethodTable.Create(new MemoryReader(table))
                    : null;
            }
        }

        /// <summary>The metadata of the module that declares this method.</summary>
        public ClrModuleMetadata Metadata
        {
            get
            {
                var methodTable = this.DeclaringMethodTable;
                if (methodTable == null || methodTable.Module == IntPtr.Zero)
                    return null;

                return ClrModuleMetadata.Of(ClrModule.At(methodTable.Module));
            }
        }

        /// <summary>
        /// This method's return type and parameters, decoded from its signature blob.
        /// </summary>
        /// <remarks>
        /// A MethodDesc stores no signature, only a token - as it stores no name. The signature
        /// lives in the declaring module's metadata and is decoded there, so this needs no
        /// <see cref="System.Type"/> or <see cref="System.Reflection.MethodBase"/> to exist and
        /// works for a constructed generic that reflection would refuse to resolve. Null when the
        /// declaring module has no mapped image, as a runtime-generated one does not.
        /// </remarks>
        public ClrMethodSignature Signature => ClrMethodSignature.Of(this);

        /// <summary>
        /// Whether the method has a body at all.
        /// </summary>
        /// <remarks>
        /// On an interface method this is the question "does it have a default implementation?" -
        /// an interface method with a body is one. Answered from the metadata's RVA, so it costs
        /// nothing to ask and does not read the IL.
        /// </remarks>
        public bool HasBody
        {
            get
            {
                var methodTable = this.DeclaringMethodTable;
                if (methodTable == null || methodTable.Module == IntPtr.Zero)
                    return false;

                return (this.Metadata?.MethodBodyRva(this.MetadataToken) ?? 0) != 0;
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

        /// <summary>
        /// Decodes the MethodDesc at <paramref name="address"/>, recovering the chunk it belongs
        /// to so its token can be reassembled.
        /// </summary>
        /// <remarks>
        /// A MethodDesc holds only the low bits of its own token; the high bits are on the
        /// MethodDescChunk that owns it. Normally the chunk is already in hand because the walk
        /// came through it - but an address arriving from somewhere else (a
        /// <see cref="RuntimeMethodHandle"/>, or a code address resolved through
        /// <see cref="Code.ClrCodeMap"/>) has no chunk with it.
        ///
        /// It can be recovered, because a MethodDesc records its own offset within its chunk:
        /// <see cref="ChunkIndex"/> in units of MethodDescAlignment, past the chunk header. So
        /// the chunk is found by stepping back exactly that far, and the step is then checked -
        /// the chunk must claim a count that actually covers this MethodDesc, or the address was
        /// not a MethodDesc and the token would be fiction.
        /// </remarks>
        public static ClrMethodDescription At(IntPtr address)
        {
            if (address == IntPtr.Zero)
                return null;

            var descriptor = ContractDescriptor.Current;
            var layout = descriptor.GetDataType("MethodDesc");
            var chunkLayout = descriptor.GetDataType("MethodDescChunk");

            var alignment = (int)descriptor.Globals.Number("MethodDescAlignment");
            var remainderBits = (int)descriptor.Globals.Number("MethodDescTokenRemainderBitCount");

            var chunkIndex = new MemoryReader(address).ReadByte(layout["ChunkIndex"]);
            var chunk = address - (int)chunkLayout.RequiredSize - (chunkIndex * alignment);

            var chunkReader = new MemoryReader(chunk);
            var count = chunkReader.ReadByte(chunkLayout["Count"]) + 1;

            // Count is a bias; a chunk holding fewer MethodDescs than this one's index means the
            // step back landed on something that is not a MethodDescChunk.
            if (chunkIndex / (uint)alignment >= (uint)count && chunkIndex != 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The MethodDesc at 0x{address.ToInt64():x} reports chunk index {chunkIndex}, " +
                    $"but the chunk it points back to at 0x{chunk.ToInt64():x} holds only {count} " +
                    $"methods. The address is not a MethodDesc.");

            var tokenRange = (ushort)(chunkReader.ReadUShort(chunkLayout["FlagsAndTokenRange"])
                                      & TokenRangeMask);

            var method = Create(new MemoryReader(address), tokenRange, remainderBits);
            method.MethodTablePointer = chunkReader.ReadIntPtr(chunkLayout["MethodTable"]);

            return method;
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
    /// What kind of generic method an instantiated MethodDesc is.
    /// </summary>
    /// <remarks>
    /// The values are the runtime's own, from the kind bits of InstantiatedMethodDesc.Flags2.
    /// Each has been observed against a live runtime - see
    /// <see cref="ClrMethodDescription.GenericKind"/> for what produces which.
    /// </remarks>
    public enum GenericMethodKind
    {
        /// <summary>Not an instantiated MethodDesc at all.</summary>
        NotGeneric = 0,

        /// <summary>The open definition, Echo&lt;T&gt;.</summary>
        GenericMethodDefinition = 1,

        /// <summary>
        /// An instantiation with its own code, which is what a value type argument gets.
        /// Measured: <c>Echo&lt;int&gt;</c>, <c>Echo&lt;double&gt;</c> and <c>Echo&lt;Small&gt;</c>.
        /// </summary>
        UnsharedMethodInstantiation = 2,

        /// <summary>
        /// The one body of code every reference instantiation shares, whose own type arguments
        /// are System.__Canon. Measured: <c>Echo&lt;System.__Canon&gt;</c> exists alongside
        /// <c>Echo&lt;string&gt;</c>, and is what the latter's code actually is.
        /// </summary>
        SharedMethodInstantiation = 3,

        /// <summary>
        /// The concrete reference instantiation, which is a stub carrying its real type
        /// arguments over the shared code. Measured: <c>Echo&lt;string&gt;</c> and
        /// <c>Pair&lt;string, double&gt;</c>.
        /// </summary>
        WrapperStubWithInstantiations = 4
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