using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// The GC heap of this process: its generations, their segments, and the objects in them.
    /// </summary>
    /// <remarks>
    /// This is the read side of the GC. It reports what the heap holds; it does not influence
    /// collection. Writing to these structures from inside the process being collected would
    /// corrupt the heap, and nothing here does.
    ///
    /// Requires .NET 11 or later, because earlier runtimes publish no GC contract - see
    /// <see cref="GcContractDescriptor"/>.
    ///
    /// A walk of a live heap races with every other thread. <see cref="GcWalkScope"/> is how a
    /// caller narrows that window; <see cref="EnumerateObjects"/> deliberately does not do it
    /// implicitly, because the right scope depends on how much of the heap is being read.
    /// </remarks>
    public sealed class ClrGcHeap
    {
        private static readonly object gate = new object();

        private static ClrGcHeap current;

        private ClrGcHeap(ClrHeapLayouts layouts, List<ClrGeneration> generations, string identifiers)
        {
            this.Layouts = layouts;
            this.Generations = generations;
            this.Identifiers = identifiers;
        }

        /// <summary>The heap of the running process, read once.</summary>
        /// <remarks>
        /// The generation and segment structure is read eagerly and cached; object contents are
        /// not. A collection rebuilds that structure, so call <see cref="Refresh"/> to re-read
        /// it - and read it from inside a <see cref="GcWalkScope"/>, since entering one collects.
        /// </remarks>
        public static ClrGcHeap Current
        {
            get
            {
                lock (gate)
                    return current ??= Create();
            }
        }

        public ClrHeapLayouts Layouts { get; }

        /// <summary>The generations, index 0 upwards: gen0..gen2, then LOH and POH.</summary>
        public IReadOnlyList<ClrGeneration> Generations { get; }

        /// <summary>The GC flavour the descriptor named, e.g. "workstation, regions, background,".</summary>
        public string Identifiers { get; }

        /// <summary>True when the process is running server GC, which keeps one heap per core.</summary>
        public bool IsServer => GcContractDescriptor.IsServer;

        /// <summary>
        /// How many GC heaps this process has: one under workstation GC, one per core under
        /// server GC. <see cref="Generations"/> holds every heap's generations flattened
        /// together, so this is what tells them apart.
        /// </summary>
        public int HeapCount
        {
            get
            {
                var heaps = 0;
                foreach (var generation in this.Generations)
                    heaps = Math.Max(heaps, generation.HeapIndex + 1);

                return heaps;
            }
        }

        /// <summary>The generations belonging to one heap, in generation order.</summary>
        public IEnumerable<ClrGeneration> GenerationsOfHeap(int heapIndex)
        {
            foreach (var generation in this.Generations)
            {
                if (generation.HeapIndex == heapIndex)
                    yield return generation;
            }
        }

        /// <summary>Every segment of every generation.</summary>
        public IEnumerable<ClrHeapSegment> Segments
        {
            get
            {
                foreach (var generation in this.Generations)
                {
                    foreach (var segment in generation.Segments)
                        yield return segment;
                }
            }
        }

        /// <summary>Re-reads the generation and segment structure.</summary>
        /// <remarks>
        /// The re-read also replaces what <see cref="Current"/> hands out. Without that, anything
        /// reached through <see cref="Current"/> - including
        /// <see cref="ClrHeapObject.Segment"/> - would keep answering from the snapshot taken at
        /// first use and could not see a segment allocated since, which is exactly the case a
        /// refresh exists to handle.
        /// </remarks>
        public static ClrGcHeap Refresh()
        {
            var heap = Create();

            lock (gate)
                current = heap;

            return heap;
        }

        /// <summary>
        /// Every object on the heap, in address order within each segment.
        /// </summary>
        /// <remarks>
        /// Free-space fillers are included and flagged via
        /// <see cref="ClrHeapObject.IsFree"/> rather than hidden, because a heap report wants
        /// them.
        /// </remarks>
        public IEnumerable<ClrHeapObject> EnumerateObjects() => this.EnumerateObjects((GcWalkScope)null);

        /// <summary>
        /// Every object on the heap, abandoning the walk if <paramref name="scope"/> observes a
        /// collection.
        /// </summary>
        /// <remarks>
        /// A collection moves objects, so every address a walk in progress holds stops meaning
        /// what it meant. Passing the scope lets the walk notice that as it happens and stop,
        /// rather than reading on into memory that has been reshaped underneath it. Without a
        /// scope the walk still runs; it just cannot tell.
        /// </remarks>
        public IEnumerable<ClrHeapObject> EnumerateObjects(GcWalkScope scope)
        {
            foreach (var segment in this.Segments)
            {
                if (scope != null && scope.CollectionOccurred)
                    yield break;

                foreach (var instance in this.EnumerateObjects(segment, scope))
                    yield return instance;
            }
        }

        /// <summary>
        /// The objects in one segment, walked by advancing from each object by its own size.
        /// </summary>
        /// <remarks>
        /// The gaps come first. The GC hands each thread its own zeroed allocation buffer, and
        /// only the part a thread has used holds objects; the rest sits in the middle of the range
        /// the walk covers. Those buffers are located up front by <see cref="AllocationHoles"/>
        /// and stepped over exactly, which is what lets the walk reach the far side of gen0 -
        /// treating the first gap as the end of the segment loses most of it.
        ///
        /// A zero MethodTable outside any known buffer ends the segment. The walk does not scan
        /// forward for the next non-zero word: if a thread allocates into that space while the
        /// walk is in progress, the object it writes starts at the buffer's own pointer, which
        /// may be behind where a scan has reached, and resuming there would land mid-object and
        /// read a field as a MethodTable.
        ///
        /// Anything else unexpected - a MethodTable that is not mapped, or a size that would not
        /// advance or would run past the end - is a real decoding failure in a settled segment
        /// and throws, because advancing past it by a guess would silently desynchronise the rest
        /// of the segment and report objects that were never there.
        ///
        /// In the ephemeral segment the same conditions end the walk instead. Its contents are
        /// genuinely in motion - its end comes from a counter the allocator advances, and threads
        /// are constructing objects inside it while the walk runs - so a boundary that cannot be
        /// made sense of there is expected, not evidence of a wrong layout. Reporting it as a
        /// failure would be crying wolf; walking past it would be fiction.
        /// </remarks>
        public IEnumerable<ClrHeapObject> EnumerateObjects(ClrHeapSegment segment) =>
            this.EnumerateObjects(segment, null);

        /// <summary>
        /// The objects in one segment, abandoning the walk if <paramref name="scope"/> observes a
        /// collection.
        /// </summary>
        public IEnumerable<ClrHeapObject> EnumerateObjects(ClrHeapSegment segment, GcWalkScope scope)
        {
            if (segment == null)
                throw new ArgumentNullException(nameof(segment));

            var layouts = this.Layouts;
            var holes = AllocationHoles.Read(layouts);

            var address = segment.Mem.ToInt64();

            // Never walk past what is committed. Objects can only exist in committed memory, and
            // the recorded end is a racing value that can momentarily sit beyond it - reading
            // there is an access violation, which would take the process down rather than fail.
            var end = Math.Min(segment.ObjectsEnd.ToInt64(), segment.Committed.ToInt64());

            // Checked periodically rather than per object: reading the collection counts is
            // cheap but not free, and a walk only needs to notice a collection promptly, not
            // instantly.
            const int CollectionCheckInterval = 4096;
            var sinceCheck = 0;

            while (address < end)
            {
                if (scope != null && ++sinceCheck >= CollectionCheckInterval)
                {
                    sinceCheck = 0;
                    if (scope.CollectionOccurred)
                        yield break;
                }

                // Too little room left for any object, so whatever is here is a tail fragment
                // rather than something to decode. The ephemeral segment's end is read from a
                // value the allocator keeps moving, so a short tail is normal, not a fault.
                if (end - address < layouts.MinimumObjectSize)
                    break;

                // An allocation buffer inside the walked range holds no constructed objects yet.
                if (holes.TryGetHoleEnd(address, out var resume))
                {
                    if (resume <= address)
                        break;

                    address = resume;
                    continue;
                }

                // The segment's own bounds are not proof the memory is still there. A collection
                // can return regions and decommit them while the walk runs, and reading a
                // decommitted page is fatal to the process, not just to the walk - so the page
                // is checked first. Finding it gone means the ground moved; that ends the
                // segment rather than being reported as a decoding failure.
                if (!layouts.IsReadableObjectHeader(address))
                    break;

                var reader = new MemoryReader(new IntPtr(address));

                var rawMethodTable = reader.ReadIntPtr(layouts.MethodTablePointerOffset).ToInt64();
                if ((rawMethodTable & ~layouts.MethodTableUnmask) == 0)
                {
                    // The unused tail of some thread's allocation buffer. The walk stops here
                    // rather than scanning forward for the next non-zero word: if a thread
                    // allocates into that buffer while the walk is in progress, the first object
                    // written starts at the buffer's own pointer, which may be behind where the
                    // scan has reached - so resuming would land mid-object and misread a field
                    // as a MethodTable. Stopping under-reports; guessing reports fiction.
                    break;
                }

                // The MethodTable is checked before it is followed, not after. Decoding the
                // object dereferences it, and a pointer that does not point at mapped memory
                // would be an access violation - which cannot be caught and would take the
                // process down rather than fail this walk.
                var methodTable = new IntPtr(rawMethodTable & ~layouts.MethodTableUnmask);
                if (!layouts.IsReadableMethodTable(methodTable))
                {
                    if (segment.IsEphemeral)
                        break;

                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"The object at 0x{address:x} in the segment at " +
                        $"0x{segment.Address.ToInt64():x} has MethodTable " +
                        $"0x{methodTable.ToInt64():x}, which is not an aligned, mapped address. " +
                        $"The walk is out of step, so it is stopped rather than reporting " +
                        $"objects that may not exist.");
                }

                var instance = ClrHeapObject.Create(reader, layouts);

                // An object that runs a little past the recorded end is the tail, not a fault.
                // Mem, Allocated and Committed are all read from a live heap, so the last object
                // in a segment is exactly where a small discrepancy surfaces. A walk that has
                // genuinely lost its place overshoots by far more than one object's worth, so
                // the tolerance is bounded rather than open-ended.
                var overrun = address + instance.Size - end;
                if (overrun > 0 && overrun <= layouts.MinimumObjectSize)
                {
                    yield return instance;
                    break;
                }

                if (instance.Size < layouts.MinimumObjectSize
                    || instance.Size % layouts.ObjectAlignment != 0
                    || overrun > 0)
                {
                    if (segment.IsEphemeral)
                        break;

                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"The object at 0x{address:x} in the segment at " +
                        $"0x{segment.Address.ToInt64():x} computed a size of {instance.Size}, " +
                        $"which is not a valid object size here (minimum " +
                        $"{layouts.MinimumObjectSize}, alignment {layouts.ObjectAlignment}, " +
                        $"segment ends at 0x{end:x}). The walk cannot advance without " +
                        $"desynchronising.");
                }

                yield return instance;
                address += instance.Size;
            }
        }

        private static ClrGcHeap Create()
        {
            var runtime = ContractDescriptor.Current;
            var gc = GcContractDescriptor.Current;
            gc.RequireContract("GC", 1);

            var layouts = new ClrHeapLayouts(runtime, gc);
            var generations = ClrGeneration.ReadAll(layouts);

            return new ClrGcHeap(layouts, generations, GcContractDescriptor.Identifiers);
        }

        public override string ToString()
        {
            var segments = 0;
            long live = 0;
            foreach (var segment in this.Segments)
            {
                segments++;
                live += segment.LiveBytes;
            }

            var heaps = this.IsServer ? $" heaps={this.HeapCount}" : string.Empty;

            return $"gc heap \"{this.Identifiers}\"{heaps} generations={this.Generations.Count} " +
                   $"segments={segments} live={live}";
        }
    }
}