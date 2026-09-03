using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// One heap segment, or one region on a region-based GC: a contiguous span of the heap that
    /// objects are laid out in, back to back.
    /// </summary>
    /// <remarks>
    /// The four bounds are nested: <c>Mem &lt;= Allocated &lt;= Committed &lt;= Reserved</c>.
    /// Objects live in <c>[Mem, Allocated)</c>, the pages up to <c>Committed</c> are backed by
    /// memory, and the span to <c>Reserved</c> is address space the GC has claimed but not yet
    /// committed. Walking past <see cref="ObjectsEnd"/> would read uninitialised memory.
    /// </remarks>
    public sealed class ClrHeapSegment
    {
        /// <summary>A linked list this long is taken as corrupt rather than walked forever.</summary>
        private const int MaximumSegmentsPerGeneration = 100_000;

        /// <summary>
        /// The GC's segment flags. The descriptor publishes the Flags field's offset but not the
        /// meaning of its bits, so these are the runtime's own values.
        /// </summary>
        /// <remarks>
        /// Only <see cref="ReadOnlyFlag"/> changes behaviour, and it does so in the safe
        /// direction: it relaxes a bounds check. If the value were ever wrong, the check would
        /// fire and say so rather than quietly accepting a bad segment.
        /// </remarks>
        private const ulong ReadOnlyFlag = 0x1;

        private const ulong LargeObjectHeapFlag = 0x8;

        private const ulong PinnedObjectHeapFlag = 0x200;

        private ClrHeapSegment()
        {
        }

        public IntPtr Address { get; private set; }

        /// <summary>The generation whose chain this segment was reached through.</summary>
        public int Generation { get; private set; }

        /// <summary>The first address objects can occupy.</summary>
        public IntPtr Mem { get; private set; }

        /// <summary>One past the last allocated byte, as the GC last recorded it.</summary>
        public IntPtr Allocated { get; private set; }

        public IntPtr Committed { get; private set; }

        public IntPtr Reserved { get; private set; }

        public IntPtr Used { get; private set; }

        public ulong Flags { get; private set; }

        /// <summary>
        /// True for a frozen segment: read-only data the GC never collects or moves.
        /// </summary>
        /// <remarks>
        /// These hold objects baked into a ReadyToRun image - literal strings and the like - and
        /// they are mapped outside the GC's own address range, which is why the bounds check
        /// treats them differently.
        /// </remarks>
        public bool IsReadOnly => (this.Flags & ReadOnlyFlag) != 0;

        public bool IsLargeObjectHeap => (this.Flags & LargeObjectHeapFlag) != 0;

        public bool IsPinnedObjectHeap => (this.Flags & PinnedObjectHeapFlag) != 0;

        /// <summary>
        /// One past the last byte holding a real object, which is not always
        /// <see cref="Allocated"/>.
        /// </summary>
        /// <remarks>
        /// The GC only writes <c>Allocated</c> back for the ephemeral segment when it collects,
        /// so on a live heap gen0's segment reports <c>Allocated == Mem</c> while holding plenty
        /// of objects. The running total is in the GC's <c>alloc_allocated</c> field instead, so
        /// that is what bounds the walk for the ephemeral segment.
        /// </remarks>
        public IntPtr ObjectsEnd { get; private set; }

        /// <summary>
        /// True for the segment threads are currently allocating into.
        /// </summary>
        /// <remarks>
        /// This one segment's contents are in motion while the walk runs: its end comes from a
        /// counter the allocator keeps advancing, and threads are writing objects into buffers
        /// inside it. So the walk treats a boundary it cannot make sense of here as the end of
        /// the segment, where in a settled segment the same thing is a decoding failure.
        /// </remarks>
        public bool IsEphemeral { get; private set; }

        public long LiveBytes => this.ObjectsEnd.ToInt64() - this.Mem.ToInt64();

        public long CommittedBytes => this.Committed.ToInt64() - this.Mem.ToInt64();

        public long ReservedBytes => this.Reserved.ToInt64() - this.Mem.ToInt64();

        /// <summary>
        /// Walks a generation's segment chain from <paramref name="first"/>.
        /// </summary>
        internal static List<ClrHeapSegment> ReadChain(
            IntPtr first,
            int generation,
            IntPtr ephemeralSegment,
            IntPtr allocAllocated,
            ClrHeapLayouts layouts)
        {
            var segments = new List<ClrHeapSegment>();
            var layout = layouts.Segment;

            var nextOffset = layout["Next"];
            var seen = 0;
            var address = first;

            while (address != IntPtr.Zero)
            {
                if (++seen > MaximumSegmentsPerGeneration)
                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"Generation {generation}'s segment chain exceeded " +
                        $"{MaximumSegmentsPerGeneration} entries, so it is being treated as " +
                        $"corrupt rather than followed further.");

                var reader = new MemoryReader(address);
                var segment = new ClrHeapSegment();
                segment.Address = address;
                segment.Generation = generation;
                segment.Mem = reader.ReadIntPtr(layout["Mem"]);
                segment.Allocated = reader.ReadIntPtr(layout["Allocated"]);
                segment.Committed = reader.ReadIntPtr(layout["Committed"]);
                segment.Reserved = reader.ReadIntPtr(layout["Reserved"]);
                segment.Used = reader.ReadIntPtr(layout["Used"]);
                segment.Flags = (ulong)reader.ReadNUInt(layout["Flags"]);

                segment.IsEphemeral = address == ephemeralSegment;
                segment.ObjectsEnd = segment.IsEphemeral && allocAllocated != IntPtr.Zero
                    ? allocAllocated
                    : segment.Allocated;

                Validate(segment, layouts);
                segments.Add(segment);

                var next = reader.ReadIntPtr(nextOffset);
                if (next == address)
                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"The heap segment at 0x{address.ToInt64():x} lists itself as its own " +
                        $"successor, so the chain is corrupt.");

                address = next;
            }

            return segments;
        }

        /// <summary>
        /// Rejects a segment whose bounds are not self-consistent or not inside the GC's range.
        /// </summary>
        /// <remarks>
        /// A wrong offset would yield a segment whose bounds look like addresses but are not, and
        /// a walk over that reads arbitrary memory. Checking here turns that into a diagnosable
        /// failure at the point the bad value was read.
        /// </remarks>
        private static void Validate(ClrHeapSegment segment, ClrHeapLayouts layouts)
        {
            // A frozen segment is mapped from an image rather than reserved by the GC, so it sits
            // outside GCLowestAddress/GCHighestAddress and that check does not apply to it.
            if (!segment.IsReadOnly && !layouts.IsPlausibleHeapAddress(segment.Mem.ToInt64()))
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The segment at 0x{segment.Address.ToInt64():x} starts at " +
                    $"0x{segment.Mem.ToInt64():x}, outside the GC's range " +
                    $"[0x{layouts.LowestAddress:x}, 0x{layouts.HighestAddress:x}) and is not " +
                    $"flagged read-only (flags=0x{segment.Flags:x}).");

            if (segment.ObjectsEnd.ToInt64() < segment.Mem.ToInt64()
                || segment.Committed.ToInt64() < segment.Mem.ToInt64()
                || segment.Reserved.ToInt64() < segment.Committed.ToInt64())
            {
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The segment at 0x{segment.Address.ToInt64():x} has inconsistent bounds: " +
                    $"mem=0x{segment.Mem.ToInt64():x} end=0x{segment.ObjectsEnd.ToInt64():x} " +
                    $"committed=0x{segment.Committed.ToInt64():x} " +
                    $"reserved=0x{segment.Reserved.ToInt64():x}.");
            }
        }

        public override string ToString()
        {
            return $"segment @0x{this.Address.ToInt64():x} gen={this.Generation} " +
                   $"mem=0x{this.Mem.ToInt64():x} live={this.LiveBytes} " +
                   $"committed={this.CommittedBytes} reserved={this.ReservedBytes} " +
                   $"flags=0x{this.Flags:x}";
        }
    }
}