using System;
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

        /// <summary>Reads the runtime's Assembly for the assembly declaring this type.</summary>
        public static ClrAssembly Of(Type typeInAssembly)
        {
            if (typeInAssembly == null) throw new ArgumentNullException(nameof(typeInAssembly));

            return At(ClrModule.Of(typeInAssembly).Assembly);
        }

        public static ClrAssembly At(IntPtr address)
        {
            if (address == IntPtr.Zero)
                return null;

            var layout = ContractDescriptor.Current.GetDataType("Assembly");
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
