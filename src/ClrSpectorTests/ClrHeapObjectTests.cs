using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ClrSpector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    public class GcSample
    {
        public int First;

        public int Second;

        public string Text = "hello";
    }

    /// <summary>
    /// Going from an object you already hold to the GC's own record of it, rather than walking
    /// the whole heap to find it.
    /// </summary>
    /// <remarks>
    /// An address is only true until a collection moves the object, so these run inside a
    /// <see cref="GcWalkScope"/> and check afterwards that no collection intervened. Without
    /// that, a failure here would be indistinguishable from a decode bug.
    /// </remarks>
    [NotInParallel]
    public class ClrHeapObjectTests
    {
        [Test]
        public async Task ReadsTheEntryOfAnOrdinaryObject()
        {
            using var scope = GcWalkScope.Enter();

            var instance = new GcSample();
            var entry = ClrHeapObject.Of(instance, scope);

            await Assert.That(entry.Address).IsEqualTo(ClrHeapObject.AddressOf(instance));
            await Assert.That(entry.IsFree).IsFalse();
            await Assert.That(entry.ComponentCount).IsEqualTo(0u);

            // Header, MethodTable pointer, two ints and a reference - rounded to the GC's
            // alignment. The exact number is the runtime's business; that it is sane is ours.
            await Assert.That(entry.Size).IsGreaterThanOrEqualTo(24L);

            await Assert.That(entry.MethodTablePointer).IsEqualTo(typeof(GcSample).TypeHandle.Value);
            await Assert.That(entry.MethodTable.Name).IsEqualTo(typeof(GcSample).FullName);
        }

        /// <summary>An array's size depends on a count that only the instance carries.</summary>
        [Test]
        public async Task SizesAnArrayFromItsComponentCount()
        {
            using var scope = GcWalkScope.Enter();

            var small = new int[10];
            var large = new int[100];

            var smallEntry = ClrHeapObject.Of(small, scope);
            var largeEntry = ClrHeapObject.Of(large, scope);

            await Assert.That(smallEntry.ComponentCount).IsEqualTo(10u);
            await Assert.That(largeEntry.ComponentCount).IsEqualTo(100u);

            // Ninety more four-byte elements, and nothing else changed.
            await Assert.That(largeEntry.Size - smallEntry.Size).IsEqualTo(90L * sizeof(int));
        }

        [Test]
        public async Task SizesAStringFromItsLength()
        {
            using var scope = GcWalkScope.Enter();

            var text = new string('x', 32);
            var entry = ClrHeapObject.Of(text, scope);

            await Assert.That(entry.ComponentCount).IsEqualTo(32u);
            await Assert.That(entry.MethodTable.IsString).IsTrue();
        }

        /// <summary>A big enough allocation goes on the large object heap, which the segment says.</summary>
        [Test]
        public async Task FindsTheSegmentAnObjectLivesIn()
        {
            using var scope = GcWalkScope.Enter();

            var large = new byte[100_000];
            var entry = ClrHeapObject.Of(large, scope);
            var segment = entry.Segment;

            await Assert.That(segment).IsNotNull();
            await Assert.That(segment.IsLargeObjectHeap).IsTrue();
            await Assert.That(entry.Generation).IsEqualTo(segment.Generation);

            scope.ThrowIfInvalidated();
        }

        [Test]
        public async Task AFreshObjectIsInAnEphemeralSegment()
        {
            using var scope = GcWalkScope.Enter();

            var instance = new GcSample();
            var segment = ClrHeapObject.Of(instance, scope).Segment;

            await Assert.That(segment).IsNotNull();
            await Assert.That(segment.IsLargeObjectHeap).IsFalse();

            scope.ThrowIfInvalidated();
        }

        /// <summary>
        /// The entry the extension reads must be the same one a heap walk would find at that
        /// address - the two routes are only worth having if they agree.
        /// </summary>
        [Test]
        public async Task AgreesWithADirectDecodeAtTheSameAddress()
        {
            using var scope = GcWalkScope.Enter();
            var heap = ClrGcHeap.Refresh();

            var instance = new GcSample();

            var viaFactory = ClrHeapObject.Of(instance, scope);
            var viaAddress = ClrHeapObject.Create(
                new MemoryReader(ClrHeapObject.AddressOf(instance)), heap.Layouts);

            await Assert.That(viaAddress.Address).IsEqualTo(viaFactory.Address);
            await Assert.That(viaAddress.Size).IsEqualTo(viaFactory.Size);
            await Assert.That(viaAddress.MethodTablePointer).IsEqualTo(viaFactory.MethodTablePointer);
        }

        [Test]
        public async Task BoxedValuesAndGenericsDecodeToTheirOwnTypes()
        {
            using var scope = GcWalkScope.Enter();

            object boxed = 42;
            object list = new List<string>();

            await Assert.That(ClrHeapObject.Of(boxed, scope).MethodTable.Name).IsEqualTo(typeof(int).FullName);
            await Assert.That(ClrHeapObject.Of(list, scope).MethodTablePointer)
                .IsEqualTo(typeof(List<string>).TypeHandle.Value);
        }

        [Test]
        public async Task RefusesNull()
        {
            await Assert.That(() => ClrHeapObject.Of(null)).Throws<ArgumentNullException>();
            await Assert.That(() => ClrHeapObject.AddressOf(null)).Throws<ArgumentNullException>();
        }
    }
}
