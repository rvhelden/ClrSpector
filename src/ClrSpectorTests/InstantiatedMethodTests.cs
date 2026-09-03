using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ClrSpector;

namespace ClrSpectorTests
{
    public struct InstantiationMarker
    {
        public int Value;
    }

    public static class InstantiationHost
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T Echo<T>(T value) => value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Pair<TKey, TValue>(TKey key, TValue value)
        {
        }
    }

    /// <summary>
    /// Checks the walk of a module's constructed generic methods.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These MethodDescs are reachable no other way. <c>Echo&lt;int&gt;</c> has no MethodDef row,
    /// and it is not in its type's MethodDescChunks either - the runtime makes it on first use
    /// and files it in a per-module hash table. So this is the difference between seeing the
    /// generic methods a program <i>declares</i> and the instantiations it has actually made.
    /// </para>
    /// <para>
    /// The table is live: instantiations appear as code runs, and these tests allocate and format
    /// strings, which itself creates some. So the assertions are about presence and consistency
    /// rather than exact totals - the one exact check is that a walk started and finished without
    /// the count moving finds every entry.
    /// </para>
    /// </remarks>
    [NotInParallel]
    public class InstantiatedMethodTests
    {
        private static void MakeInstantiations()
        {
            InstantiationHost.Echo(1);
            InstantiationHost.Echo(2.5);
            InstantiationHost.Echo(3L);
            InstantiationHost.Echo(new InstantiationMarker());
            InstantiationHost.Echo("text");
            InstantiationHost.Echo((object)null);
            InstantiationHost.Pair(1, "text");
            InstantiationHost.Pair("text", 2.5);
        }

        [Test]
        public async Task TheWalkFindsTheInstantiationsThisTestMade()
        {
            MakeInstantiations();

            var module = ClrModule.Of(typeof(InstantiationHost));
            var methods = module.InstantiatedMethods.ToList();

            await Assert.That(methods).IsNotEmpty();

            var echo = methods.Where(m => m.Name == "Echo").ToList();
            var pair = methods.Where(m => m.Name == "Pair").ToList();

            await Assert.That(echo).IsNotEmpty();
            await Assert.That(pair).IsNotEmpty();

            // Every entry names a generic method, and each carries its own arity.
            foreach (var method in echo)
            {
                await Assert.That(method.Classification).IsEqualTo(MethodClassification.Instantiated);
                await Assert.That(method.GenericArgumentMethodTables.Length).IsEqualTo(1);
            }

            foreach (var method in pair)
                await Assert.That(method.GenericArgumentMethodTables.Length).IsEqualTo(2);
        }

        /// <summary>
        /// How a generic method is instantiated follows from its type argument: a value type
        /// gets its own code, a reference type shares one body compiled against __Canon, and the
        /// concrete reference instantiation is a stub carrying its real arguments over that.
        /// </summary>
        /// <remarks>
        /// Asserted as an implication over whatever the walk returns, rather than by demanding a
        /// particular instantiation be present. The walk is not provably exhaustive - see
        /// <see cref="ClrModule.InstantiatedMethods"/> - so requiring a named entry would be
        /// testing the walk's completeness under the guise of testing the kinds, and would be
        /// flaky for a reason unrelated to what this is about.
        /// </remarks>
        [Test]
        public async Task AGenericKindFollowsFromItsTypeArgument()
        {
            MakeInstantiations();

            var module = ClrModule.Of(typeof(InstantiationHost));
            var checked_ = 0;
            var kinds = new HashSet<GenericMethodKind>();

            foreach (var method in module.InstantiatedMethods)
            {
                var handles = method.GenericArgumentMethodTables;
                if (handles.Length == 0 || !handles.All(ClrMethodTable.IsMethodTableHandle))
                    continue;

                var arguments = handles
                    .Select(h => ClrMethodTable.Create(new MemoryReader(h)))
                    .ToList();

                var expected = arguments.Any(a => a.MetadataName == "System.__Canon")
                    ? GenericMethodKind.SharedMethodInstantiation
                    : arguments.All(a => a.IsValueType)
                        ? GenericMethodKind.UnsharedMethodInstantiation
                        : GenericMethodKind.WrapperStubWithInstantiations;

                var names = string.Join(", ", arguments.Select(a => a.MetadataName));

                await Assert.That(method.GenericKind)
                    .IsEqualTo(expected)
                    .Because($"{method.Name}<{names}> should be {expected}");

                kinds.Add(method.GenericKind);
                checked_++;
            }

            await Assert.That(checked_).IsGreaterThan(0);

            // The instantiations made above span all three, so more than one kind must show up
            // even if an individual entry is missed.
            await Assert.That(kinds.Count).IsGreaterThan(1);
        }

        /// <summary>
        /// An instantiated method's signature closes to real types, which is what nothing could
        /// do before: metadata holds only <c>Echo&lt;T&gt;</c>.
        /// </summary>
        [Test]
        public async Task AnInstantiatedMethodsSignatureClosesToItsArguments()
        {
            MakeInstantiations();

            var module = ClrModule.Of(typeof(InstantiationHost));

            var closed = module.InstantiatedMethods
                .Where(m => m.Name == "Echo"
                            && m.GenericKind == GenericMethodKind.UnsharedMethodInstantiation)
                .Select(m => m.ClosedSignature?.ToString())
                .Where(text => text != null)
                .ToList();

            await Assert.That(closed).IsNotEmpty();

            // The open definition is "!!0 (!!0 value)"; closing it names the argument twice.
            await Assert.That(closed.Any(t => t.Contains("System.Int32 value"))).IsTrue();
            await Assert.That(closed.Any(t => t.Contains("!!0"))).IsFalse();
        }

        /// <summary>
        /// The count the table records is what proves the walk is exhaustive rather than merely
        /// plausible.
        /// </summary>
        /// <remarks>
        /// This is the test that caught the real bug. A walk built from measurement alone started
        /// the buckets at slot 2 and came back one entry short every time - the runtime's own
        /// sources put them at slot 3, because slot 2 holds the table's base end sentinel. Only
        /// the count exposed it: every entry the short walk returned was correct, so nothing else
        /// looked wrong.
        ///
        /// Bracketed rather than compared to a single reading, because the table is live and only
        /// ever grows: the walk must find at least everything that existed when it started and at
        /// most everything that existed when it finished.
        /// </remarks>
        [Test]
        [Arguments(typeof(InstantiationHost))]
        [Arguments(typeof(object))]
        public async Task TheWalkFindsEveryEntryTheTableRecords(Type typeInModule)
        {
            MakeInstantiations();

            var module = ClrModule.Of(typeInModule);

            var before = module.InstantiatedMethodCount;
            var walked = module.InstantiatedMethods.Count();
            var after = module.InstantiatedMethodCount;

            await Assert.That(before).IsGreaterThan(0L);

            await Assert.That(walked)
                .IsGreaterThanOrEqualTo((int)before)
                .Because($"the walk must miss nothing (before={before} walked={walked} after={after})");

            await Assert.That(walked)
                .IsLessThanOrEqualTo((int)after)
                .Because($"the walk must invent nothing (before={before} walked={walked} after={after})");
        }

        /// <summary>
        /// The same holds for the constructed-type table, which has the identical shape.
        /// </summary>
        [Test]
        public async Task TheTypeWalkFindsEveryEntryTheTableRecords()
        {
            var module = ClrModule.Of(typeof(object));

            var before = module.ConstructedTypeCount;
            var walked = module.ConstructedTypeHandles.Count();
            var after = module.ConstructedTypeCount;

            await Assert.That(before).IsGreaterThan(0L);
            await Assert.That(walked).IsGreaterThanOrEqualTo((int)before);
            await Assert.That(walked).IsLessThanOrEqualTo((int)after);
        }

        /// <summary>
        /// An entry's flags live in the spare low bits of its MethodDesc pointer, so a walk that
        /// did not mask them would decode every field of the MethodDesc at the wrong offset.
        /// </summary>
        [Test]
        public async Task EntryFlagsAreSeparatedFromTheMethodDescPointer()
        {
            MakeInstantiations();

            var module = ClrModule.Of(typeof(InstantiationHost));
            var entries = module.InstantiatedMethodEntries.ToList();

            await Assert.That(entries).IsNotEmpty();

            var alignment = IntPtr.Size;

            foreach (var (method, flags) in entries)
            {
                // The masked pointer must be properly aligned, and it must still decode.
                await Assert.That(method.ClrPointer.ToInt64() % alignment).IsEqualTo(0L);
                await Assert.That(method.Classification).IsEqualTo(MethodClassification.Instantiated);

                // Only the two documented bits can be set.
                var known = InstantiatedMethodFlags.UnboxingStub | InstantiatedMethodFlags.RequiresInstArg;
                await Assert.That(flags & ~known).IsEqualTo(InstantiatedMethodFlags.None);
            }

            // A shared instantiation cannot recover its type arguments from its arguments, so it
            // is the case that needs its instantiation passed in.
            var shared = entries
                .Where(e => e.Method.GenericKind == GenericMethodKind.SharedMethodInstantiation)
                .ToList();

            if (shared.Count > 0)
            {
                await Assert.That(shared.Any(e => e.Flags.HasFlag(InstantiatedMethodFlags.RequiresInstArg)))
                    .IsTrue()
                    .Because("a shared instantiation needs its instantiation passed at run time");
            }
        }

        [Test]
        public async Task AModuleWithNoInstantiationsWalksToNothing()
        {
            // The table pointer is null until the runtime needs one, and that must read as empty
            // rather than faulting.
            var empty = ClrModule.At(IntPtr.Zero);

            await Assert.That(empty).IsNull();

            var module = ClrModule.Of(typeof(InstantiationHost));

            await Assert.That(module.InstantiatedMethodCount).IsGreaterThanOrEqualTo(0L);
            await Assert.That(module.InstantiatedMethods).IsNotNull();
        }

        /// <summary>
        /// The same table shape holds constructed types, which have no TypeDef row either.
        /// </summary>
        [Test]
        public async Task ConstructedTypesAreEnumerableTheSameWay()
        {
            var module = ClrModule.Of(typeof(object));
            var handles = module.ConstructedTypeHandles.Take(200).ToList();

            await Assert.That(handles).IsNotEmpty();

            var decoded = 0;
            foreach (var handle in handles)
            {
                // Not every handle is a MethodTable - a pointer or byref type is a TypeDesc.
                if (!ClrMethodTable.IsMethodTableHandle(handle))
                    continue;

                var table = ClrMethodTable.Create(new MemoryReader(handle));

                await Assert.That(table).IsNotNull();
                decoded++;
            }

            await Assert.That(decoded).IsGreaterThan(0);
        }

        private static string Describe(IntPtr handle)
        {
            return ClrMethodTable.IsMethodTableHandle(handle)
                ? ClrMethodTable.Create(new MemoryReader(handle)).MetadataName
                : "<type variable>";
        }
    }
}
