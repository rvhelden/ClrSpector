using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// Walks the runtime's side tables of things that have no metadata row - constructed generic
    /// methods and types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>List&lt;int&gt;.Add</c> is in no module's MethodDef table, and neither is
    /// <c>List&lt;int&gt;</c> itself: metadata records only the open definitions, and the runtime
    /// builds the instantiations on demand. It keeps them in per-module hash tables instead, and
    /// those tables are the only place they can be enumerated from.
    /// </para>
    /// <para>
    /// The descriptor publishes the same four fields for both tables - <c>Buckets</c>,
    /// <c>Count</c>, and an entry's <c>VolatileEntryValue</c> and <c>VolatileEntryNextEntry</c> -
    /// so one walker serves both. What it does not publish is the bucket array's shape, and that
    /// part is not guessable, because two of its rules are counter-intuitive:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Three leading slots, not two.</b> Slot 0 is the bucket count and slot 1 links to the
    /// next array during a resize, but slot 2 holds the table's <i>base end sentinel</i> - so the
    /// buckets themselves start at slot 3. Starting at slot 2 reads the sentinel as a bucket and
    /// runs off the end one bucket early, which loses whatever was in the last one.
    /// </description></item>
    /// <item><description>
    /// <b>A chain ends on a sentinel, not on null.</b> The list can be mutated while it is being
    /// read, so each bucket ends with a value unique to that bucket rather than with NULL, marked
    /// by its low bit: <c>(bucketIndex &lt;&lt; 6) | baseSentinel</c>. Testing for null - or for
    /// "does this look like a readable pointer" - happens to work, but testing the low bit is
    /// what the runtime does and is what identifies the end unambiguously.
    /// </description></item>
    /// </list>
    /// <para>
    /// An entry is the value, then the next pointer, then a hash the descriptor does not publish.
    /// Both tables' values are a single pointer with flags in the spare low bits - for a method
    /// the MethodDesc plus <see cref="InstantiatedMethodFlags"/>, for a type the type handle -
    /// so a value has to be masked before it is followed.
    /// </para>
    /// <para>
    /// Cross-checked against the runtime's own sources (<c>dacenumerablehash.h</c>,
    /// <c>instmethhash.h</c>) after a walk built from measurement alone came up one entry short:
    /// it was the off-by-one leading slot above. A walk now returns exactly the number of entries
    /// the table's <c>Count</c> reports.
    /// </para>
    /// </remarks>
    internal static class DacEnumerableHashTable
    {
        /// <summary>Slot 0 of the bucket array: how many buckets follow.</summary>
        private const int LengthSlot = 0;

        /// <summary>Slot 1: the newer bucket array a resize has not finished moving into.</summary>
        private const int NextArraySlot = 1;

        /// <summary>
        /// Slot 2: the base sentinel this table's chain ends are derived from. Not read here -
        /// the low-bit test identifies an end without needing it - but it is the reason the
        /// buckets start at slot 3 rather than slot 2.
        /// </summary>
        private const int EndSentinelSlot = 2;

        /// <summary>The buckets themselves begin after those three.</summary>
        private const int FirstBucketSlot = 3;

        /// <summary>A bucket count beyond this is taken as a misread rather than a huge table.</summary>
        private const long MaximumBuckets = 1 << 24;

        /// <summary>A chain this long is taken as corrupt rather than followed further.</summary>
        private const int MaximumChain = 100_000;

        /// <summary>A resize leaves one newer array; more links than this means a cycle.</summary>
        private const int MaximumArrays = 64;

        /// <summary>
        /// The values held in <paramref name="table"/>, in bucket order, still carrying whatever
        /// flags their low bits hold.
        /// </summary>
        /// <param name="table">The hash table's address.</param>
        /// <param name="typeName">
        /// The descriptor type describing it - <c>InstMethodHashTable</c> or
        /// <c>EETypeHashTable</c>.
        /// </param>
        public static IEnumerable<IntPtr> Values(IntPtr table, string typeName)
        {
            if (table == IntPtr.Zero)
                yield break;

            var descriptor = ContractDescriptor.Current;
            if (!descriptor.TryGetDataType(typeName, out var layout)
                || !layout.HasField("Buckets")
                || !layout.HasField("VolatileEntryValue")
                || !layout.HasField("VolatileEntryNextEntry"))
            {
                yield break;
            }

            var valueOffset = layout["VolatileEntryValue"];
            var nextOffset = layout["VolatileEntryNextEntry"];
            var entrySize = Math.Max(valueOffset, nextOffset) + IntPtr.Size;

            if (!ProcessMemoryRegions.IsReadable(table, layout["Buckets"] + IntPtr.Size))
                yield break;

            var buckets = new MemoryReader(table).ReadIntPtr(layout["Buckets"]);
            var arrays = 0;

            // A resize publishes a new array and leaves the old one linked, so both are walked.
            while (buckets != IntPtr.Zero && arrays++ < MaximumArrays)
            {
                if (!ProcessMemoryRegions.IsReadable(buckets, FirstBucketSlot * IntPtr.Size))
                    yield break;

                var array = new MemoryReader(buckets);
                var length = (long)array.ReadIntPtr(LengthSlot * IntPtr.Size);

                if (length <= 0 || length > MaximumBuckets)
                    yield break;

                if (!ProcessMemoryRegions.IsReadable(buckets, (FirstBucketSlot + length) * IntPtr.Size))
                    yield break;

                for (var i = 0L; i < length; i++)
                {
                    var entry = array.ReadIntPtr((int)((FirstBucketSlot + i) * IntPtr.Size));
                    var depth = 0;

                    while (!IsEndSentinel(entry) && depth++ < MaximumChain)
                    {
                        // The sentinel test says where the chain ends; this says the entry can
                        // actually be read, so a torn or stale link fails the walk of one bucket
                        // rather than the process.
                        if (!ProcessMemoryRegions.IsReadable(entry, entrySize))
                            break;

                        var reader = new MemoryReader(entry);
                        var value = reader.ReadIntPtr(valueOffset);

                        if (value != IntPtr.Zero)
                            yield return value;

                        entry = reader.ReadIntPtr(nextOffset);
                    }
                }

                buckets = array.ReadIntPtr(NextArraySlot * IntPtr.Size);
            }
        }

        /// <summary>
        /// True when a chain link is an end marker rather than an entry.
        /// </summary>
        /// <remarks>
        /// Entries are pointer-aligned, so the runtime marks an end with the low bit and packs
        /// the bucket index and the array's age into the rest. Null counts as an end too - a
        /// table under construction can hold one.
        /// </remarks>
        private static bool IsEndSentinel(IntPtr link) => (link.ToInt64() & 1) != 0 || link == IntPtr.Zero;

        /// <summary>The bucket index a chain's end marker encodes, for diagnostics.</summary>
        public static long BucketIndexOf(IntPtr endSentinel) => endSentinel.ToInt64() >> 6;

        /// <summary>
        /// The entry count the table records, which a walk of it should match exactly.
        /// </summary>
        /// <remarks>
        /// A 32-bit field, not a pointer: reading it pointer-wide picks up whatever follows it.
        /// </remarks>
        public static long CountOf(IntPtr table, string typeName)
        {
            if (table == IntPtr.Zero)
                return 0;

            var descriptor = ContractDescriptor.Current;
            if (!descriptor.TryGetDataType(typeName, out var layout) || !layout.HasField("Count"))
                return 0;

            return ProcessMemoryRegions.IsReadable(table, layout["Count"] + sizeof(uint))
                ? new MemoryReader(table).ReadUInt(layout["Count"])
                : 0;
        }
    }

    /// <summary>
    /// The flags a module's instantiated-method table packs into an entry's MethodDesc pointer.
    /// </summary>
    /// <remarks>
    /// A MethodDesc is pointer-aligned, so the table stores these in the spare low bits rather
    /// than in a field of their own - which is why an entry's value has to be masked before it
    /// can be followed. Names and values are the runtime's own, from <c>instmethhash.h</c>.
    /// </remarks>
    [Flags]
    public enum InstantiatedMethodFlags
    {
        None = 0,

        /// <summary>The entry is the stub that unwraps a boxed value type before the real call.</summary>
        UnboxingStub = 0x01,

        /// <summary>
        /// The method needs its instantiation passed at run time, because it shares code with
        /// other instantiations and cannot recover the type arguments from its arguments alone.
        /// </summary>
        RequiresInstArg = 0x02
    }
}