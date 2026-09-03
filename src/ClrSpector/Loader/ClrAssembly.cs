using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// An assembly as the runtime sees it, and the loader allocator its runtime structures come
    /// from.
    /// </summary>
    /// <remarks>
    /// The loader allocator is the part worth having: it owns the heaps a type's MethodTables,
    /// MethodDescs and precodes are carved out of, and whether it is collectible decides whether
    /// any of that can be unloaded. Those heap bounds are also how a raw address can be checked
    /// for being a precode rather than a coincidence - see
    /// <see cref="ClrLoaderAllocator.OwnsPrecode"/>.
    /// </remarks>
    public sealed unsafe class ClrAssembly
    {
        private ClrAssembly()
        {
        }

        public IntPtr Address { get; private set; }

        /// <summary>The assembly's Module.</summary>
        public IntPtr Module { get; private set; }

        /// <summary>True for an assembly in a collectible load context, which can be unloaded.</summary>
        public bool IsCollectible { get; private set; }

        /// <summary>True for an assembly built at runtime rather than loaded from a file.</summary>
        public bool IsDynamic { get; private set; }

        public bool IsLoaded { get; private set; }

        /// <summary>
        /// The exception recorded against this assembly, or zero when it loaded cleanly.
        /// </summary>
        /// <remarks>
        /// A load failure is remembered on the Assembly rather than only thrown, so that every
        /// later attempt fails the same way. Non-zero here is the handle to that stored error.
        /// </remarks>
        public IntPtr Error { get; private set; }

        /// <summary>True when the assembly failed to load and the failure was recorded.</summary>
        public bool HasError => this.Error != IntPtr.Zero;

        /// <summary>
        /// Which load notifications the runtime has already raised for this assembly, so a
        /// profiler or debugger attaching later can tell what it missed.
        /// </summary>
        public uint NotifyFlags { get; private set; }

        /// <summary>The manifest module - the one carrying the assembly's own metadata.</summary>
        public ClrModule ManifestModule =>
            this.Module == IntPtr.Zero ? null : ClrModule.At(this.Module);

        /// <summary>
        /// The assembly's simple name, from the Assembly row in its manifest module's metadata.
        /// </summary>
        /// <remarks>
        /// The runtime's Assembly structure stores no name; what it has is a Module, and the name
        /// is a row in that module's metadata - so this is the same trick as everywhere else here,
        /// a token's worth of indirection instead of asking reflection. Null for a dynamic
        /// assembly, which has no mapped image to read.
        /// </remarks>
        public string Name => this.AssemblyRow(7);

        /// <summary>The assembly's culture, or null when it is culture-neutral.</summary>
        public string Culture
        {
            get
            {
                var culture = this.AssemblyRow(8);

                return string.IsNullOrEmpty(culture) ? null : culture;
            }
        }

        /// <summary>The version from the assembly's own metadata row.</summary>
        public Version Version
        {
            get
            {
                var metadata = this.ManifestModule == null ? null : ClrModuleMetadata.Of(this.ManifestModule);
                var image = metadata?.Image;

                if (image == null || image.RowCount(MetadataTable.Assembly) < 1)
                    return null;

                // Assembly: HashAlgId, Major, Minor, Build, Revision, Flags, PublicKey, Name, Culture.
                return new Version(
                    (int)image.ReadColumn(MetadataTable.Assembly, 1, 1),
                    (int)image.ReadColumn(MetadataTable.Assembly, 1, 2),
                    (int)image.ReadColumn(MetadataTable.Assembly, 1, 3),
                    (int)image.ReadColumn(MetadataTable.Assembly, 1, 4));
            }
        }

        /// <summary>
        /// The assembly's modules.
        /// </summary>
        /// <remarks>
        /// One, always, on this runtime: multi-module assemblies are a .NET Framework feature that
        /// .NET Core never carried forward, so the runtime's Assembly holds a single Module rather
        /// than a list. This is a list anyway because that is the shape callers expect, and
        /// because the File table it would come from still exists in metadata.
        /// </remarks>
        public IReadOnlyList<ClrModule> Modules
        {
            get
            {
                var manifest = this.ManifestModule;

                return manifest == null
                    ? new ClrModule[0]
                    : new[] { manifest };
            }
        }

        /// <summary>A string column of the single Assembly row, or null when there is none.</summary>
        private string AssemblyRow(int column)
        {
            var manifest = this.ManifestModule;
            if (manifest == null)
                return null;

            var image = ClrModuleMetadata.Of(manifest)?.Image;

            if (image == null || image.RowCount(MetadataTable.Assembly) < 1)
                return null;

            return image.String(image.ReadColumn(MetadataTable.Assembly, 1, column));
        }

        /// <summary>Reads the runtime's Assembly for the assembly declaring this type.</summary>
        /// <summary>
        /// The assembly-level attributes - what source writes as <c>[assembly: ...]</c>.
        /// </summary>
        /// <remarks>
        /// These live on the manifest module's single Assembly row rather than on any type, which
        /// is why they are reached from here and not from a MethodTable.
        /// </remarks>
        public IReadOnlyList<ClrCustomAttribute> CustomAttributes
        {
            get
            {
                var manifest = this.ManifestModule;

                return manifest == null || manifest.Base == IntPtr.Zero
                    ? new ClrCustomAttribute[0]
                    : ClrModuleMetadata.Of(manifest).AssemblyAttributes;
            }
        }

        public static ClrAssembly Of(Type typeInAssembly)
        {
            if (typeInAssembly == null) throw new ArgumentNullException(nameof(typeInAssembly));

            return At(ClrModule.Of(typeInAssembly).Assembly);
        }

        /// <summary>
        /// The Assembly at <paramref name="address"/>, or null when there is not one there.
        /// </summary>
        /// <remarks>
        /// Validated before it is read, for the same reason as
        /// <see cref="ClrModule.At"/>: an address taken out of another structure can be stale,
        /// and reading unmapped memory is fatal rather than catchable.
        /// </remarks>
        public static ClrAssembly At(IntPtr address)
        {
            var descriptor = ContractDescriptor.Current;

            if (!ClrModule.IsReadableStructure(address, descriptor, "Assembly"))
                return null;

            var layout = descriptor.GetDataType("Assembly");
            var reader = new MemoryReader(address);

            var assembly = new ClrAssembly
            {
                Address = address,
                Module = layout.HasField("Module") ? reader.ReadIntPtr(layout["Module"]) : IntPtr.Zero
            };

            // These are single bytes rather than flags in a word.
            if (layout.HasField("IsCollectible"))
                assembly.IsCollectible = reader.ReadByte(layout["IsCollectible"]) != 0;

            if (layout.HasField("IsDynamic"))
                assembly.IsDynamic = reader.ReadByte(layout["IsDynamic"]) != 0;

            if (layout.HasField("IsLoaded"))
                assembly.IsLoaded = reader.ReadByte(layout["IsLoaded"]) != 0;

            if (layout.HasField("Error"))
                assembly.Error = reader.ReadIntPtr(layout["Error"]);

            if (layout.HasField("NotifyFlags"))
                assembly.NotifyFlags = reader.ReadUInt(layout["NotifyFlags"]);

            return assembly;
        }

        public override string ToString()
        {
            return $"Assembly @0x{this.Address.ToInt64():x} collectible={this.IsCollectible} " +
                   $"dynamic={this.IsDynamic} loaded={this.IsLoaded}";
        }
    }

    /// <summary>
    /// The heaps a module's runtime structures are allocated from.
    /// </summary>
    /// <remarks>
    /// Precodes live in their own heaps, separate from both the jit's code heaps and ordinary
    /// loader memory. That makes the heap bounds a cheap sanity check on an address a detour is
    /// about to treat as a precode.
    /// </remarks>
    public sealed unsafe class ClrLoaderAllocator
    {
        private ClrLoaderAllocator()
        {
        }

        public IntPtr Address { get; private set; }

        public bool IsCollectible { get; private set; }

        /// <summary>How many things still reference this allocator, keeping it alive.</summary>
        public uint ReferenceCount { get; private set; }

        /// <summary>The heap FixupPrecodes are allocated from.</summary>
        public IntPtr FixupPrecodeHeap { get; private set; }

        /// <summary>The heap StubPrecodes are allocated from.</summary>
        public IntPtr NewStubPrecodeHeap { get; private set; }

        /// <summary>The heap for other executable stubs.</summary>
        public IntPtr StubHeap { get; private set; }

        /// <summary>The heap for structures written often, such as MethodTables.</summary>
        public IntPtr HighFrequencyHeap { get; private set; }

        public IntPtr LowFrequencyHeap { get; private set; }

        public IntPtr StaticsHeap { get; private set; }

        public IntPtr ExecutableHeap { get; private set; }

        /// <summary>
        /// The handle to the managed LoaderAllocator object that keeps this allocator alive.
        /// </summary>
        /// <remarks>
        /// This is the mechanism behind collectible unloading: a collectible allocator is kept
        /// alive by a managed object, and when nothing references that object any more the whole
        /// allocator - every MethodTable and precode carved out of its heaps - can go.
        /// </remarks>
        public IntPtr ObjectHandle { get; private set; }

        /// <summary>
        /// The stub manager that owns this allocator's interface dispatch stubs.
        /// </summary>
        /// <remarks>
        /// Interface calls resolve through stubs it allocates and rewrites as call sites warm up,
        /// which is why an interface dispatch cannot be redirected the way a precode can.
        /// </remarks>
        public IntPtr VirtualCallStubManager { get; private set; }

        /// <summary>
        /// A serial number, incremented per allocator created, so two allocators at the same
        /// reused address can still be told apart.
        /// </summary>
        public uint CreationNumber { get; private set; }

        /// <summary>Reads the loader allocator behind a type's module.</summary>
        public static ClrLoaderAllocator Of(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            return At(ClrModule.Of(type).LoaderAllocator);
        }

        public static ClrLoaderAllocator At(IntPtr address)
        {
            if (address == IntPtr.Zero)
                return null;

            var layout = ContractDescriptor.Current.GetDataType("LoaderAllocator");
            var reader = new MemoryReader(address);

            var allocator = new ClrLoaderAllocator { Address = address };

            if (layout.HasField("IsCollectible"))
                allocator.IsCollectible = reader.ReadByte(layout["IsCollectible"]) != 0;

            if (layout.HasField("ReferenceCount"))
                allocator.ReferenceCount = reader.ReadUInt(layout["ReferenceCount"]);

            allocator.FixupPrecodeHeap = Read(reader, layout, "FixupPrecodeHeap");
            allocator.NewStubPrecodeHeap = Read(reader, layout, "NewStubPrecodeHeap");
            allocator.StubHeap = Read(reader, layout, "StubHeap");
            allocator.HighFrequencyHeap = Read(reader, layout, "HighFrequencyHeap");
            allocator.LowFrequencyHeap = Read(reader, layout, "LowFrequencyHeap");
            allocator.StaticsHeap = Read(reader, layout, "StaticsHeap");
            allocator.ExecutableHeap = Read(reader, layout, "ExecutableHeap");
            allocator.ObjectHandle = Read(reader, layout, "ObjectHandle");
            allocator.VirtualCallStubManager = Read(reader, layout, "VirtualCallStubManager");

            if (layout.HasField("CreationNumber"))
                allocator.CreationNumber = reader.ReadUInt(layout["CreationNumber"]);

            return allocator;
        }

        /// <summary>
        /// Whether this allocator has a precode heap at all - the weakest useful check that an
        /// address claimed to be a precode could have come from here.
        /// </summary>
        /// <remarks>
        /// The heaps themselves are LoaderHeap structures whose committed ranges the contract does
        /// not publish, so this cannot confirm containment. It only reports that precodes are
        /// allocated from this allocator, which is still enough to tell a module that has
        /// produced precodes from one that has not.
        /// </remarks>
        public bool OwnsPrecode =>
            this.FixupPrecodeHeap != IntPtr.Zero || this.NewStubPrecodeHeap != IntPtr.Zero;

        private static IntPtr Read(MemoryReader reader, DataType layout, string field)
        {
            return layout.HasField(field) ? reader.ReadIntPtr(layout[field]) : IntPtr.Zero;
        }

        public override string ToString()
        {
            return $"LoaderAllocator @0x{this.Address.ToInt64():x} collectible={this.IsCollectible} " +
                   $"refs={this.ReferenceCount} fixupPrecodeHeap=0x{this.FixupPrecodeHeap.ToInt64():x}";
        }
    }
}