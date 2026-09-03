using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ClrSpector;
using ClrSpector.Detours;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    public class SlotSample
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Stat(int quantity) => "static";

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int NonVirtual(int quantity) => quantity;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual string Virt(int id) => "virtual";
    }

    /// <summary>
    /// Checks the type-system decode against the shapes the runtime's own cdac reader
    /// (<c>MethodTableFlags_1</c>, <c>MethodDescFlags_1</c>, <c>MethodDescOptionalSlots</c>)
    /// defines, using types whose answers are known independently.
    /// </summary>
    public class RuntimeTypeSystemTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static;

        [Test]
        [Arguments(typeof(string), true)]
        [Arguments(typeof(int), false)]
        [Arguments(typeof(char[]), false)]
        [Arguments(typeof(object), false)]
        public async Task IdentifiesStrings(Type type, bool expected)
        {
            // A string carries a component size like an array does, so it is the one type the
            // low flag word cannot be read from - see the runtime's StringArrayValues.
            await Assert.That(ClrObject.From(type).MethodTable.IsString).IsEqualTo(expected);
        }

        [Test]
        [Arguments(typeof(int), true)]
        [Arguments(typeof(double), true)]
        [Arguments(typeof(decimal), false)]
        [Arguments(typeof(string), false)]
        public async Task IdentifiesPrimitives(Type type, bool expected)
        {
            await Assert.That(ClrObject.From(type).MethodTable.IsPrimitive).IsEqualTo(expected);
        }

        [Test]
        public async Task IdentifiesNullables()
        {
            await Assert.That(ClrObject.From(typeof(int?)).MethodTable.IsNullable).IsTrue();
            await Assert.That(ClrObject.From(typeof(int)).MethodTable.IsNullable).IsFalse();
        }

        [Test]
        public async Task IdentifiesByRefLikeTypes()
        {
            await Assert.That(ClrObject.From(typeof(Span<int>)).MethodTable.IsByRefLike).IsTrue();
            await Assert.That(ClrObject.From(typeof(SampleClass)).MethodTable.IsByRefLike).IsFalse();
        }

        /// <summary>
        /// Note this is about the fields, not the type: a class whose fields are all primitives
        /// holds nothing for the GC to trace, and a string's characters are not references.
        /// </summary>
        [Test]
        [Arguments(typeof(List<string>), true)]
        [Arguments(typeof(GcSample), true)]
        [Arguments(typeof(SampleClass), false)]
        [Arguments(typeof(int), false)]
        [Arguments(typeof(string), false)]
        public async Task IdentifiesTypesHoldingReferences(Type type, bool expected)
        {
            await Assert.That(ClrObject.From(type).MethodTable.ContainsGcPointers).IsEqualTo(expected);
        }

        [Test]
        public async Task IdentifiesGenericInstantiations()
        {
            var closed = ClrObject.From(typeof(List<string>)).MethodTable;

            await Assert.That(closed.HasInstantiation).IsTrue();
            await Assert.That(closed.IsGenericTypeDefinition).IsFalse();

            await Assert.That(ClrObject.From(typeof(string)).MethodTable.HasInstantiation).IsFalse();
        }

        /// <summary>
        /// The runtime packs the TypeDef row id into MTFlags2, which is the only route to a type's
        /// name that does not need a live type handle.
        /// </summary>
        [Test]
        [Arguments(typeof(string))]
        [Arguments(typeof(int))]
        [Arguments(typeof(SampleClass))]
        public async Task TypeDefTokenResolvesToTheType(Type type)
        {
            var methodTable = ClrObject.From(type).MethodTable;

            var resolved = type.Module.ResolveType((int)methodTable.TypeDefToken);

            await Assert.That(resolved).IsEqualTo(type);
            await Assert.That(methodTable.Name).IsEqualTo(type.FullName);
        }

        /// <summary>A generic instantiation's TypeDef is the definition, since that is what metadata holds.</summary>
        [Test]
        public async Task TypeDefTokenOfAnInstantiationNamesTheDefinition()
        {
            var methodTable = ClrObject.From(typeof(List<string>)).MethodTable;

            var resolved = typeof(List<string>).Module.ResolveType((int)methodTable.TypeDefToken);

            await Assert.That(resolved).IsEqualTo(typeof(List<>));
        }

        /// <summary>
        /// A static or non-virtual method dispatches through a slot packed in after its own
        /// MethodDesc, not through the vtable - so that slot holds its entry point.
        /// </summary>
        [Test]
        [Arguments("Stat")]
        [Arguments("NonVirtual")]
        public async Task NonVtableSlotHoldsTheEntryPoint(string name)
        {
            var method = typeof(SlotSample).GetMethod(name, All);
            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            var descriptor = ClrObject.From(typeof(SlotSample)).MethodTable.FindMethod(method);

            await Assert.That(descriptor).IsNotNull();
            await Assert.That(descriptor.HasNonVtableSlot).IsTrue();
            await Assert.That(descriptor.NonVtableSlotAddress).IsNotEqualTo(IntPtr.Zero);

            var held = ReadPointer(descriptor.NonVtableSlotAddress);

            await Assert.That(held).IsEqualTo(method.MethodHandle.GetFunctionPointer());
        }

        /// <summary>A virtual method has a vtable entry instead, so no slot of its own.</summary>
        [Test]
        public async Task AVirtualMethodHasNoNonVtableSlot()
        {
            var method = typeof(SlotSample).GetMethod(nameof(SlotSample.Virt), All);
            var descriptor = ClrObject.From(typeof(SlotSample)).MethodTable.FindMethod(method);

            await Assert.That(descriptor.HasNonVtableSlot).IsFalse();
            await Assert.That(descriptor.NonVtableSlotAddress).IsEqualTo(IntPtr.Zero);
            await Assert.That(MethodVtable.FindSlot(method)).IsNotEqualTo(IntPtr.Zero);
        }

        [Test]
        public async Task CountsNonVirtualSlotsAlongsideVirtuals()
        {
            var methodTable = ClrObject.From(typeof(SlotSample)).MethodTable;

            await Assert.That(MethodVtable.TotalSlotCount(methodTable))
                .IsGreaterThanOrEqualTo(methodTable.NumberOfVirtuals);
        }

        /// <summary>
        /// The runtime publishes the byte pattern its own precodes are built from, so a precode
        /// can be recognised without this library hardcoding an opcode.
        /// </summary>
        [Test]
        public async Task PrecodeMatchesTheRuntimesOwnTemplate()
        {
            var machine = PrecodeMachineInfo.Current;

            await Assert.That(machine.FixupBytes).IsNotNull();
            await Assert.That(machine.FixupStubPrecodeSize).IsNotNull();

            var method = typeof(SlotSample).GetMethod(nameof(SlotSample.NonVirtual), All);
            var precode = MethodPrecode.Of(method);

            await Assert.That(precode.EntryPointBytes.Length)
                .IsGreaterThanOrEqualTo(machine.FixupBytes.Length);
            await Assert.That(precode.IsFixupPrecode).IsTrue();
            await Assert.That(precode.IsStubPrecode).IsFalse();
        }

        /// <summary>
        /// Fields the runtime need not publish are read as optional, so a build that omits one
        /// reports its absence instead of failing the whole decode.
        /// </summary>
        [Test]
        public async Task OptionalPrecodeFieldsAreNotRequired()
        {
            var machine = PrecodeMachineInfo.Current;

            // Always published - without these nothing about a precode can be said.
            await Assert.That(machine.StubCodePageSize).IsGreaterThan(0u);

            // Published here, but typed as optional because other builds omit them. Reading them
            // must not throw either way.
            _ = machine.InterpreterPrecodeType;
            _ = machine.DynamicHelperPrecodeType;
            _ = machine.ThisPointerRetBufPrecodeType;

            await Assert.That(machine.ToString()).Contains("PrecodeMachineDescriptor");
        }

        /// <summary>
        /// The tiering guard reads a real per-method flag rather than guessing at configuration.
        /// This project disables tiering, so nothing here is eligible and the guard stays out of
        /// the way - which is the behaviour that matters for every other test in the suite.
        /// </summary>
        [Test]
        public async Task TieringGuardIsDormantWhileTieringIsDisabled()
        {
            var methodTable = ClrObject.From(typeof(SlotSample)).MethodTable;

            await Assert.That(methodTable.Methods.Any(m => m.IsEligibleForTieredCompilation)).IsFalse();

            // ...so an ordinary redirect is not refused by it.
            using var detour = MethodDetour.Redirect(
                typeof(SlotSample), nameof(SlotSample.Stat),
                typeof(SlotSampleProxy), nameof(SlotSampleProxy.Stat));

            await Assert.That(SlotSample.Stat(1)).IsEqualTo("proxy");
        }

        private static unsafe IntPtr ReadPointer(IntPtr address) => *(IntPtr*)address;
    }

    public static class SlotSampleProxy
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Stat(int quantity) => "proxy";
    }
}
