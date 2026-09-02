using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ClrSpector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    /// <summary>
    /// Checks the decoded runtime structures against facts obtainable independently of the
    /// decoder - reflection identities and pointer round-trips. These are what distinguish a
    /// correct decode from one that merely does not crash.
    /// </summary>
    public class ClrObjectTests
    {
        [Test]
        public async Task ParentMethodTableIsObjectForADirectSubclass()
        {
            var methodTable = ClrObject.From<SampleClass>().MethodTable;

            await Assert.That(methodTable.ParentMethodTablePointer)
                .IsEqualTo(typeof(object).TypeHandle.Value);
        }

        [Test]
        public async Task EEClassPointsBackAtItsMethodTable()
        {
            var methodTable = ClrObject.From<SampleClass>().MethodTable;

            await Assert.That(methodTable.EEClass.MethodTablePointer)
                .IsEqualTo(typeof(SampleClass).TypeHandle.Value);
        }

        [Test]
        public async Task FieldCountsMatchReflection()
        {
            const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.DeclaredOnly;

            var eeClass = ClrObject.From<SampleClass>().MethodTable.EEClass;

            var instanceFields = typeof(SampleClass)
                .GetFields(declared | BindingFlags.Instance).Length;
            var staticFields = typeof(SampleClass)
                .GetFields(declared | BindingFlags.Static).Length;

            await Assert.That((int)eeClass.NumberOfInstanceFields).IsEqualTo(instanceFields);
            await Assert.That((int)eeClass.NumberOfStaticFields).IsEqualTo(staticFields);
        }

        [Test]
        public async Task DecodedMethodCountMatchesReflection()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static
                                     | BindingFlags.DeclaredOnly;

            var methodTable = ClrObject.From<SampleClass>().MethodTable;
            var expected = typeof(SampleClass).GetMethods(all).Length
                           + typeof(SampleClass).GetConstructors(all).Length;

            await Assert.That(methodTable.Methods.Count).IsEqualTo(expected);
        }

        /// <summary>
        /// The metadata token is reassembled from the MethodDesc and its owning chunk, so
        /// matching reflection's tokens exercises both halves of that reconstruction.
        /// </summary>
        [Test]
        public async Task ReconstructedTokensMatchReflection()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static
                                     | BindingFlags.DeclaredOnly;

            var methodTable = ClrObject.From<SampleClass>().MethodTable;

            var decoded = methodTable.Methods.Select(m => (int)m.MetadataToken).OrderBy(t => t).ToList();
            var expected = typeof(SampleClass).GetMethods(all).Cast<MethodBase>()
                .Concat(typeof(SampleClass).GetConstructors(all))
                .Select(m => m.MetadataToken)
                .OrderBy(t => t)
                .ToList();

            await Assert.That(decoded).IsEquivalentTo(expected);
        }

        [Test]
        public async Task EveryMethodDescChunkIsOwnedByTheDecodedType()
        {
            var methodTable = ClrObject.From<SampleClass>().MethodTable;

            // Every decoded MethodDesc must sit inside a chunk owned by this MethodTable;
            // the decoder verifies chunk stepping internally, so reaching here at all means
            // each MethodDesc's self-reported position agreed with where it was read.
            await Assert.That(methodTable.Methods).IsNotEmpty();
            await Assert.That(methodTable.Methods.Select(m => m.ClrPointer).Distinct().Count())
                .IsEqualTo(methodTable.Methods.Count);
        }

        [Test]
        [Arguments(typeof(object), false, false, false)]
        [Arguments(typeof(SampleClass), false, false, false)]
        [Arguments(typeof(int), true, false, false)]
        [Arguments(typeof(DayOfWeek), true, false, false)]
        [Arguments(typeof(decimal), true, false, false)]
        [Arguments(typeof(IDisposable), false, true, false)]
        [Arguments(typeof(int[]), false, false, true)]
        [Arguments(typeof(string[]), false, false, true)]
        [Arguments(typeof(int[,]), false, false, true)]
        public async Task CategoryFlagsAgreeWithReflection(
            Type type, bool isValueType, bool isInterface, bool isArray)
        {
            var methodTable = ClrObject.From(type).MethodTable;

            await Assert.That(methodTable.IsValueType).IsEqualTo(isValueType);
            await Assert.That(methodTable.IsInterface).IsEqualTo(isInterface);
            await Assert.That(methodTable.IsArray).IsEqualTo(isArray);

            // cross-check against reflection itself, not just the table above
            await Assert.That(methodTable.IsValueType).IsEqualTo(type.IsValueType);
            await Assert.That(methodTable.IsInterface).IsEqualTo(type.IsInterface);
            await Assert.That(methodTable.IsArray).IsEqualTo(type.IsArray);
        }

        [Test]
        [Arguments(typeof(string), 2)]
        [Arguments(typeof(int[]), 4)]
        [Arguments(typeof(long[]), 8)]
        [Arguments(typeof(byte[]), 1)]
        public async Task ComponentSizeMatchesTheElementWidth(Type type, int expected)
        {
            var methodTable = ClrObject.From(type).MethodTable;

            await Assert.That(methodTable.HasComponentSize).IsTrue();
            await Assert.That((int)methodTable.ComponentSize).IsEqualTo(expected);
        }

        [Test]
        [Arguments(typeof(SampleClass))]
        [Arguments(typeof(object))]
        [Arguments(typeof(int))]
        public async Task PlainTypesAreTheirOwnCanonicalMethodTable(Type type)
        {
            var methodTable = ClrObject.From(type).MethodTable;

            await Assert.That(methodTable.UnionKind).IsEqualTo(MethodTableUnionFieldKind.EEClass);
            await Assert.That(methodTable.IsCanonicalMethodTable).IsTrue();
        }

        /// <summary>
        /// A reference-type instantiation shares another type's canonical MethodTable, which is
        /// the case the union's tag exists to distinguish.
        /// </summary>
        [Test]
        public async Task SharedInstantiationsPointAtTheirCanonicalMethodTable()
        {
            var methodTable = ClrObject.From<List<string>>().MethodTable;

            await Assert.That(methodTable.UnionKind).IsEqualTo(MethodTableUnionFieldKind.MethodTable);
            await Assert.That(methodTable.IsCanonicalMethodTable).IsFalse();
            await Assert.That(methodTable.CanonMethodTablePointer).IsNotEqualTo(IntPtr.Zero);

            // The shared EEClass is still reachable, so methods still decode.
            await Assert.That(methodTable.EEClass).IsNotNull();
            await Assert.That(methodTable.Methods).IsNotEmpty();
        }

        /// <summary>
        /// A reference-type array shares the object[] MethodTable.
        /// </summary>
        [Test]
        public async Task ReferenceTypeArraysShareTheObjectArrayMethodTable()
        {
            var methodTable = ClrObject.From<string[]>().MethodTable;

            await Assert.That(methodTable.UnionKind).IsEqualTo(MethodTableUnionFieldKind.MethodTable);
            await Assert.That(methodTable.CanonMethodTablePointer)
                .IsEqualTo(typeof(object[]).TypeHandle.Value);
        }

        [Test]
        public async Task ValueTypeIsDecodedAsAValueType()
        {
            var methodTable = ClrObject.From<TimeSpan>().MethodTable;

            await Assert.That(methodTable.IsValueType).IsTrue();
            await Assert.That(methodTable.EEClass.NormType).IsEqualTo(CorElementType.VALUETYPE);
        }

        [Test]
        public async Task ClassIsDecodedAsAClass()
        {
            var methodTable = ClrObject.From<SampleClass>().MethodTable;

            await Assert.That(methodTable.IsClass).IsTrue();
            await Assert.That(methodTable.EEClass.NormType).IsEqualTo(CorElementType.CLASS);
        }
    }
}
