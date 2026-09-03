using System;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// A single object instance on the GC heap: its type, and how many bytes it occupies.
    /// </summary>
    /// <remarks>
    /// This is the instance-level view. <see cref="ClrObject"/> is the type-level one - it wraps
    /// a MethodTable reached from <c>Type.TypeHandle.Value</c> and describes a type, not
    /// something living on the heap.
    ///
    /// The size matters as much as the type: a heap walk advances from one object to the next by
    /// exactly this many bytes, so a wrong size does not produce one wrong answer, it
    /// desynchronises everything after it.
    /// </remarks>
    public sealed unsafe class ClrHeapObject
    {
        private ClrMethodTable methodTable;

        public void* BasePointer { get; private set; }

        public IntPtr Address => (IntPtr)this.BasePointer;

        /// <summary>The object's MethodTable, with the GC's mark and pin bits cleared.</summary>
        public IntPtr MethodTablePointer { get; private set; }

        /// <summary>The object's total size in bytes, including its header padding.</summary>
        public long Size { get; private set; }

        /// <summary>
        /// The element count for an array or string; zero for anything else.
        /// </summary>
        public uint ComponentCount { get; private set; }

        /// <summary>True when this is the free-space filler the GC leaves between objects.</summary>
        public bool IsFree { get; private set; }

        /// <summary>The decoded type. Free objects have one too - the runtime's Free type.</summary>
        public ClrMethodTable MethodTable =>
            this.methodTable ??= ClrMethodTable.Create(new MemoryReader(this.MethodTablePointer));

        /// <summary>
        /// Decodes the object at <paramref name="reader"/>'s address.
        /// </summary>
        /// <param name="reader">Positioned at the object, i.e. at its MethodTable pointer.</param>
        /// <param name="layouts">Shared layouts and globals; see <see cref="ClrHeapLayouts"/>.</param>
        public static ClrHeapObject Create(MemoryReader reader, ClrHeapLayouts layouts)
        {
            var instance = new ClrHeapObject();
            instance.BasePointer = reader.BasePointer;

            // The GC keeps mark and pin state in the low bits of the MethodTable pointer, so an
            // unmasked read yields an address that is wrong for part of every collection.
            var raw = reader.ReadIntPtr(layouts.MethodTablePointerOffset).ToInt64();
            instance.MethodTablePointer = new IntPtr(raw & ~layouts.MethodTableUnmask);
            instance.IsFree = instance.MethodTablePointer == layouts.FreeObjectMethodTable;

            // Sizing reads the two MethodTable fields it needs directly, rather than going
            // through ClrMethodTable.Create. That decode also resolves the EEClass, which is
            // both far more work per object and not reachable for every MethodTable found on the
            // heap - the Free type's, for one. The full decode stays available lazily via
            // MethodTable for callers that want it.
            var table = new MemoryReader(instance.MethodTablePointer);
            var flags = table.ReadUInt(layouts.MethodTableFlagsOffset);
            long size = table.ReadUInt(layouts.MethodTableBaseSizeOffset);

            if ((flags & ClrMethodTable.HasComponentSizeFlag) != 0)
            {
                // Arrays and strings carry their element count in the same slot, and the low word
                // of MTFlags is the element width. BaseSize already covers the header, so only
                // the elements are added.
                var componentSize = flags & ClrMethodTable.ComponentSizeMask;
                instance.ComponentCount = reader.ReadUInt(layouts.ComponentCountOffset);
                size += (long)instance.ComponentCount * componentSize;
            }

            instance.Size = layouts.AlignObjectSize(size);
            return instance;
        }

        public override string ToString()
        {
            var kind = this.IsFree ? " free" : string.Empty;
            var components = this.ComponentCount > 0 ? $" count={this.ComponentCount}" : string.Empty;
            return $"object @0x{this.Address.ToInt64():x} size={this.Size}{components}{kind} " +
                   $"mt=0x{this.MethodTablePointer.ToInt64():x}";
        }
    }
}