using System;
using System.Linq;
using System.Runtime.CompilerServices;
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
        /// The heap segment this object lives in, or null when no segment covers its address.
        /// </summary>
        /// <remarks>
        /// The segment is what says which generation the object is in and whether it is on the
        /// large or pinned object heap - facts about placement that the object itself does not
        /// carry. The segment structure is read once and a collection rebuilds it, so refresh
        /// with <see cref="ClrGcHeap.Refresh"/> if the layout may have changed underneath.
        /// </remarks>
        public ClrHeapSegment Segment => this.SegmentIn(ClrGcHeap.Current);

        /// <summary>
        /// The segment of <paramref name="heap"/> this object lives in, or null when that
        /// snapshot covers no such address.
        /// </summary>
        /// <remarks>
        /// Prefer this over <see cref="Segment"/> when the object was allocated after the heap
        /// was last read: a fresh allocation can land in a segment that did not exist then, and
        /// the cached snapshot would report no segment at all.
        /// </remarks>
        public ClrHeapSegment SegmentIn(ClrGcHeap heap)
        {
            if (heap == null) throw new ArgumentNullException(nameof(heap));

            var address = this.Address.ToInt64();

            return heap.Segments.FirstOrDefault(
                segment => address >= segment.Mem.ToInt64() && address < segment.Committed.ToInt64());
        }

        /// <summary>
        /// The generation this object currently lives in, or -1 when no segment covers it.
        /// </summary>
        /// <remarks>
        /// This is the generation of the segment the object was found in - the GC's own
        /// placement, not a promotion count. <see cref="GC.GetGeneration(object)"/> answers the
        /// same question through the supported API without reading the heap.
        /// </remarks>
        public int Generation => this.Segment?.Generation ?? -1;

        /// <summary>The generation this object is in according to <paramref name="heap"/>.</summary>
        public int GenerationIn(ClrGcHeap heap) => this.SegmentIn(heap)?.Generation ?? -1;

        /// <summary>
        /// The GC's entry for a live object: its address, its size as the collector computes it,
        /// and its type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="ClrGcHeap.EnumerateObjects()"/> goes the other way - it finds every object
        /// by walking segments. This goes straight to one object already in hand, which is the
        /// usual thing to want when investigating a specific instance.
        /// </para>
        /// <para>
        /// The size is the part reflection cannot supply: it is what a heap walk advances by, and
        /// it accounts for the header, the component count of an array or string, and the GC's
        /// alignment rules.
        /// </para>
        /// <para>
        /// <b>An object's address is only true until the GC moves it.</b> Nothing here pins
        /// anything, so for more than a single read take a <see cref="GcWalkScope"/> and use
        /// <see cref="Of(object, GcWalkScope)"/>. The MethodTable decoded here is checked against
        /// the object's actual type either way, so a move is reported rather than returned as
        /// nonsense.
        /// </para>
        /// </remarks>
        public static ClrHeapObject Of(object instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            var address = AddressOf(instance);
            var layouts = ClrGcHeap.Current.Layouts;

            if (!layouts.IsReadableObjectHeader(address.ToInt64()))
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"0x{address.ToInt64():x} does not look like a readable object header, so the " +
                    "object cannot be decoded. A collection may have moved it since its address " +
                    "was taken.");

            var entry = Create(new MemoryReader(address), layouts);

            // The object knows its own type, so the decode can be checked rather than trusted: a
            // MethodTable that disagrees means the address no longer refers to this object.
            var expected = instance.GetType().TypeHandle.Value;
            if (entry.MethodTablePointer != expected)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The object at 0x{address.ToInt64():x} decodes to MethodTable " +
                    $"0x{entry.MethodTablePointer.ToInt64():x}, but {instance.GetType().Name} is " +
                    $"0x{expected.ToInt64():x}. A collection moved the object between taking its " +
                    "address and reading it - retry inside a GcWalkScope.");

            return entry;
        }

        /// <summary>
        /// The GC's entry for a live object, failing if <paramref name="scope"/> saw a collection
        /// while it was being read.
        /// </summary>
        public static ClrHeapObject Of(object instance, GcWalkScope scope)
        {
            var entry = Of(instance);

            scope?.ThrowIfInvalidated();

            return entry;
        }

        /// <summary>
        /// The address of a live object on the heap - where its MethodTable pointer sits, which
        /// is what a reference actually points at.
        /// </summary>
        /// <remarks>
        /// True at the moment it is read and no longer, since nothing pins the object.
        /// </remarks>
        public static IntPtr AddressOf(object instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            // A managed reference is the object's address. Reinterpreting the local that holds it
            // is the only way to read that value without the runtime handing out a copy.
            return Unsafe.As<object, IntPtr>(ref instance);
        }

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

        public object GetValue()
        {
            var address = Address;
            return Unsafe.Read<object>(&address);
        }
    }
}