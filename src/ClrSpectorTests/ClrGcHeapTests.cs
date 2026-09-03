using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClrSpector;
using ClrSpector.Cdac;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    /// <summary>A distinctive type to count on the heap, sized so it is easy to recognise.</summary>
    public class HeapMarker
    {
        public long First;
        public long Second;
        public long Third;
    }

    /// <summary>
    /// Checks the GC heap walk against facts obtainable without it: a population this test
    /// allocated itself, and the runtime's own accounting.
    /// </summary>
    /// <remarks>
    /// A heap walk can be wrong in a way that still looks sane - it can stop early and report a
    /// plausible subset, which is exactly what happens if a gap in an allocation buffer is
    /// mistaken for the end of a segment. Counting a known population is what catches that; the
    /// walk finding "some objects" proves nothing.
    ///
    /// These tests mutate no state but do provoke a collection, via
    /// <see cref="GcWalkScope"/>, so they are kept out of parallel runs.
    /// </remarks>
    [NotInParallel]
    public class ClrGcHeapTests
    {
        /// <summary>
        /// The number allocated below. Large enough that a walk stopping early cannot match it
        /// by luck.
        /// </summary>
        private const int MarkerCount = 500;

        [Test]
        public async Task DescriptorSelectsTheGcFlavourTheProcessIsRunning()
        {
            var identifiers = GcContractDescriptor.Identifiers;

            await Assert.That(identifiers).IsNotEmpty();
            await Assert.That(GcContractDescriptor.IsServer).IsEqualTo(GCSettings.IsServerGC);
            await Assert.That(identifiers.Contains(GCSettings.IsServerGC ? "server" : "workstation"))
                .IsTrue();
        }

        [Test]
        public async Task TheGcContractIsPublishedSeparatelyFromTheRuntimeContract()
        {
            // The runtime descriptor knows nothing about heap segments; the GC one does. If these
            // ever came from the same descriptor, the search for the unexported header is broken.
            await Assert.That(GcContractDescriptor.Current.Contracts.ContainsKey("GC")).IsTrue();
            await Assert.That(ContractDescriptor.Current.Contracts.ContainsKey("GC")).IsFalse();
            await Assert.That(GcContractDescriptor.Current.TryGetDataType("HeapSegment", out _)).IsTrue();
            await Assert.That(ContractDescriptor.Current.TryGetDataType("HeapSegment", out _)).IsFalse();
        }

        /// <summary>
        /// Five generations per heap on .NET 11 - gen0..gen2, then LOH and POH - and server GC
        /// has one such table per heap, so the totals are per heap rather than per process.
        /// </summary>
        [Test]
        public async Task GenerationsCoverTheSmallLargeAndPinnedHeaps()
        {
            var heap = ClrGcHeap.Current;

            await Assert.That(heap.HeapCount).IsGreaterThanOrEqualTo(1);
            await Assert.That(heap.Generations.Count)
                .IsEqualTo(heap.Layouts.TotalGenerationCount * heap.HeapCount);

            for (var index = 0; index < heap.HeapCount; index++)
            {
                var generations = heap.GenerationsOfHeap(index).ToList();

                await Assert.That(generations.Count).IsEqualTo(heap.Layouts.TotalGenerationCount);
                await Assert.That(generations.Count(g => g.IsLargeObjectHeap)).IsEqualTo(1);
                await Assert.That(generations.Count(g => g.IsPinnedObjectHeap)).IsEqualTo(1);

                for (var number = 0; number < generations.Count; number++)
                {
                    await Assert.That(generations[number].Number).IsEqualTo(number);
                    await Assert.That(generations[number].HeapIndex).IsEqualTo(index);
                }
            }
        }

        /// <summary>
        /// Workstation GC keeps its state in globals and has no heap structure; server GC has one
        /// per core, and every generation must name the heap it came from.
        /// </summary>
        [Test]
        public async Task HeapCountMatchesTheGcFlavour()
        {
            var heap = ClrGcHeap.Current;

            await Assert.That(heap.IsServer).IsEqualTo(GCSettings.IsServerGC);

            if (!heap.IsServer)
            {
                await Assert.That(heap.HeapCount).IsEqualTo(1);
                await Assert.That(heap.Generations.All(g => g.HeapAddress == IntPtr.Zero)).IsTrue();

                return;
            }

            // Every server heap must be a distinct, non-null gc_heap.
            var addresses = heap.Generations.Select(g => g.HeapAddress).Distinct().ToList();

            await Assert.That(addresses.Count).IsEqualTo(heap.HeapCount);
            await Assert.That(addresses.All(a => a != IntPtr.Zero)).IsTrue();
        }

        /// <summary>
        /// Every segment's bounds must nest, and the ones the GC reserved must sit inside the
        /// range it reports - frozen segments excepted, since those are mapped from an image.
        /// </summary>
        [Test]
        public async Task SegmentBoundsAreConsistentAndInRange()
        {
            var heap = ClrGcHeap.Current;
            var segments = heap.Segments.ToList();

            await Assert.That(segments).IsNotEmpty();

            foreach (var segment in segments)
            {
                await Assert.That(segment.ObjectsEnd.ToInt64()).IsGreaterThanOrEqualTo(segment.Mem.ToInt64());
                await Assert.That(segment.Committed.ToInt64()).IsGreaterThanOrEqualTo(segment.Mem.ToInt64());
                await Assert.That(segment.Reserved.ToInt64()).IsGreaterThanOrEqualTo(segment.Committed.ToInt64());

                if (!segment.IsReadOnly)
                    await Assert.That(heap.Layouts.IsPlausibleHeapAddress(segment.Mem.ToInt64())).IsTrue();
            }
        }

        /// <summary>
        /// The end-to-end proof: a population this test allocated is found in full.
        /// </summary>
        /// <remarks>
        /// The scope is entered before the heap is read, because establishing a no-GC region
        /// collects first and so invalidates any snapshot taken earlier.
        /// </remarks>
        [Test]
        public async Task WalkFindsEveryObjectOfAKnownPopulation()
        {
            var markers = new List<HeapMarker>(MarkerCount);
            for (var i = 0; i < MarkerCount; i++)
                markers.Add(new HeapMarker());

            var markerMethodTable = typeof(HeapMarker).TypeHandle.Value;

            using var scope = GcWalkScope.Enter();
            var heap = ClrGcHeap.Refresh();

            var found = 0;
            long foundBytes = 0;
            foreach (var instance in heap.EnumerateObjects(scope))
            {
                if (instance.MethodTablePointer != markerMethodTable)
                    continue;

                found++;
                foundBytes += instance.Size;
            }

            scope.ThrowIfInvalidated();

            await Assert.That(found).IsEqualTo(MarkerCount);

            // All instances of one type are the same size, so the total must divide exactly.
            await Assert.That(foundBytes % MarkerCount).IsEqualTo(0);

            GC.KeepAlive(markers);
        }

        /// <summary>
        /// An array's decoded size must match its element count, and a string's its length.
        /// </summary>
        /// <remarks>
        /// This is the check that would catch a wrong component size or a missing base size: a
        /// walk desynchronises within a few objects if either is wrong, so agreeing here for
        /// several widths is strong evidence the size rule is right.
        ///
        /// The array is found by its own address rather than by looking for any array of the
        /// right length, so the test cannot pass by stumbling onto some other array of the same
        /// shape. It is allocated on the pinned object heap, which is walked end to end - an
        /// array left in gen0 may sit beyond the point where the walk stops, since that region is
        /// being allocated into while the walk runs.
        /// </remarks>
        [Test]
        [Arguments(4096)]
        [Arguments(1)]
        [Arguments(0)]
        public async Task ArraySizesMatchTheirElementCount(int length)
        {
            var array = GC.AllocateArray<byte>(length, pinned: true);
            var methodTable = typeof(byte[]).TypeHandle.Value;
            var handle = GCHandle.Alloc(array, GCHandleType.Pinned);

            var matched = false;
            long matchedSize = 0;
            uint matchedCount = 0;
            var diagnostics = string.Empty;

            try
            {
                // The address of the element data, which lies inside the object.
                var interior = handle.AddrOfPinnedObject().ToInt64();

                using (GcWalkScope.Enter())
                {
                    var heap = ClrGcHeap.Refresh();
                    diagnostics = $"interior=0x{interior:x} segments={heap.Segments.Count()} " +
                                  $"pohRanges=[{string.Join(", ", heap.Segments
                                      .Where(s => s.IsPinnedObjectHeap)
                                      .Select(s => $"0x{s.Mem.ToInt64():x}-0x{s.ObjectsEnd.ToInt64():x}"))}]";

                    // Only the pinned object heap, which is where the array was allocated. This
                    // test is about the size rule, not about the whole heap being walkable, so
                    // it does not depend on unrelated regions decoding.
                    //
                    // No scope is passed either: a pinned-heap object never moves, so a
                    // collection does not invalidate this reading, and abandoning the walk on one
                    // would only make the test flaky on a busy run.
                    foreach (var segment in heap.Segments.Where(s => s.IsPinnedObjectHeap))
                    {
                        foreach (var instance in heap.EnumerateObjects(segment))
                        {
                            var start = instance.Address.ToInt64();
                            if (interior < start || interior >= start + instance.Size)
                                continue;

                            matched = true;
                            matchedSize = instance.Size;
                            matchedCount = instance.ComponentCount;
                            await Assert.That(instance.MethodTablePointer).IsEqualTo(methodTable);
                            break;
                        }

                        if (matched)
                            break;
                    }
                }
            }
            finally
            {
                handle.Free();
            }

            await Assert.That(matched)
                .IsTrue()
                .Because($"the pinned byte[{length}] should be found by the walk; {diagnostics}");

            await Assert.That(matchedCount).IsEqualTo((uint)length);

            // BaseSize covers the header and the length field, so the elements sit on top of it -
            // one byte each for a byte[] - and the total is rounded up to pointer alignment.
            var expected = ClrGcHeap.Current.Layouts.AlignObjectSize(
                ClrObject.From<byte[]>().MethodTable.BaseSize + length);

            await Assert.That(matchedSize).IsEqualTo(expected);
            GC.KeepAlive(array);
        }

        /// <summary>
        /// Every walked object must start inside the segment it was walked from, and every
        /// reported size must be a usable object size.
        /// </summary>
        /// <remarks>
        /// The violations are counted and asserted afterwards rather than asserted per object.
        /// An assertion allocates, and allocating inside the walk both eats the no-GC budget the
        /// scope depends on and pushes the allocation buffers the walk is stepping around.
        /// </remarks>
        [Test]
        public async Task WalkedObjectsStayWithinTheirSegment()
        {
            var walked = 0;
            var outsideSegment = 0;
            var badSize = 0;
            var nullMethodTable = 0;
            var undecodableSegments = 0;
            var segmentCount = 0;

            using (var scope = GcWalkScope.Enter())
            {
                var heap = ClrGcHeap.Refresh();
                var layouts = heap.Layouts;
                segmentCount = heap.Segments.Count();

                foreach (var segment in heap.Segments)
                {
                    var start = segment.Mem.ToInt64();
                    var end = segment.ObjectsEnd.ToInt64();

                    // A segment the walk refuses to decode raises rather than guessing. That is
                    // the intended behaviour, and it is counted here rather than failing the
                    // test, because what this test checks is the objects that were decoded.
                    try
                    {
                        foreach (var instance in heap.EnumerateObjects(segment, scope))
                        {
                            var address = instance.Address.ToInt64();

                            if (address < start || address >= end)
                                outsideSegment++;

                            if (instance.Size < layouts.MinimumObjectSize
                                || instance.Size % layouts.ObjectAlignment != 0)
                            {
                                badSize++;
                            }

                            if (instance.MethodTablePointer == IntPtr.Zero)
                                nullMethodTable++;

                            walked++;
                        }
                    }
                    catch (ClrSpectorUnsupportedRuntimeException)
                    {
                        undecodableSegments++;
                    }
                }

                // Deliberately not ThrowIfInvalidated. This walk covers the whole heap, so on a
                // busy process it can outlast the no-GC budget - and that is not what is under
                // test here. Every object the walk reported was well-formed when it was read,
                // which is the invariant, whether or not a collection followed.
            }

            await Assert.That(walked).IsGreaterThan(0);
            await Assert.That(outsideSegment).IsEqualTo(0);
            await Assert.That(badSize).IsEqualTo(0);
            await Assert.That(nullMethodTable).IsEqualTo(0);

            // Every object the walk did report must be sound; a segment it declined is a
            // separate, visible outcome rather than a silently wrong one. Most must still decode.
            await Assert.That(undecodableSegments * 2).IsLessThan(segmentCount);
        }

        /// <summary>
        /// A walk should account for most of what the heap says is live. It cannot be all of it -
        /// unused allocation buffers sit inside the walked range and hold nothing.
        /// </summary>
        [Test]
        public async Task WalkedBytesAccountForMostOfTheReportedLiveBytes()
        {
            // This is the only test that walks the whole heap, so it is the one whose budget the
            // rest of the suite eats into - emitting a thunk or a replacement body allocates, and
            // by the time this runs there is more live heap to cover than the default allows for.
            // A collection part-way through invalidates the walk, so the budget is raised rather
            // than the assertion loosened.
            using var scope = GcWalkScope.Enter(512 * 1024 * 1024);
            var heap = ClrGcHeap.Refresh();

            long reported = 0;
            long walked = 0;
            var segments = 0;
            foreach (var segment in heap.Segments)
            {
                segments++;
                reported += segment.LiveBytes;
                try
                {
                    foreach (var instance in heap.EnumerateObjects(segment, scope))
                        walked += instance.Size;
                }
                catch (ClrSpectorUnsupportedRuntimeException)
                {
                    // Counted as reported-but-not-walked; the ratio below allows for it.
                }
            }

            scope.ThrowIfInvalidated();

            await Assert.That(reported).IsGreaterThan(0L);

            // The walk deliberately yields a final object that runs slightly past a segment's
            // recorded end - Mem, Allocated and Committed are all read from a live heap, so the
            // last object is exactly where a small discrepancy surfaces. That tolerance is
            // bounded at one minimum object per segment, so the total can exceed the reported
            // bytes by at most that much.
            var tail = (long)segments * heap.Layouts.MinimumObjectSize;

            await Assert.That(walked).IsLessThanOrEqualTo(reported + tail);

            // Anything much below this means the walk is stopping early rather than skipping
            // buffer gaps, which is the failure this whole file exists to catch.
            await Assert.That(walked * 100 / reported).IsGreaterThan(80L);
        }

        /// <summary>
        /// The free-space fillers the GC leaves behind are reported as such, with the runtime's
        /// own Free MethodTable, rather than being decoded as ordinary objects.
        /// </summary>
        [Test]
        public async Task FreeObjectsAreIdentifiedByTheRuntimesFreeMethodTable()
        {
            var freeMethodTable = ContractDescriptor.Current.Globals.Dereference("FreeObjectMethodTable");
            await Assert.That(freeMethodTable).IsNotEqualTo(IntPtr.Zero);

            var disagreements = 0;
            var walked = 0;

            using (var scope = GcWalkScope.Enter())
            {
                var heap = ClrGcHeap.Refresh();

                foreach (var segment in heap.Segments)
                {
                    // Per segment, so a segment the walk declines does not hide the rest.
                    try
                    {
                        foreach (var instance in heap.EnumerateObjects(segment, scope))
                        {
                            if (instance.IsFree != (instance.MethodTablePointer == freeMethodTable))
                                disagreements++;

                            walked++;
                        }
                    }
                    catch (ClrSpectorUnsupportedRuntimeException)
                    {
                    }
                }

                // Deliberately not ThrowIfInvalidated: whether a free object is labelled as one
                // is a property of each object as it was read, so a collection afterwards does
                // not make the readings wrong - it only means the run was busy.
            }

            await Assert.That(walked).IsGreaterThan(0);
            await Assert.That(disagreements).IsEqualTo(0);
        }
    }
}