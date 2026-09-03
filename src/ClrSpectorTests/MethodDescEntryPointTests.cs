using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ClrSpector;
using ClrSpector.Detours;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    public class Native
    {
        public int Factor = 4;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Scale(int value) => value * this.Factor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual string Ship() => "shipped";
    }

    public class NativeProxy
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Scale(Native target, int value) => 1234;
    }

    /// <summary>
    /// Reaching a method through its MethodDesc rather than through reflection.
    /// </summary>
    /// <remarks>
    /// A MethodDesc address is exactly what a <see cref="RuntimeMethodHandle"/> wraps, so
    /// everything the runtime can do with a handle - jitting a method, taking its entry point -
    /// is reachable without a <see cref="Type"/> or a <see cref="MethodBase"/> having been
    /// created. These check that route gives the same answers as the reflection one.
    /// </remarks>
    [NotInParallel]
    public class MethodDescEntryPointTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                                             | BindingFlags.Instance | BindingFlags.Static;

        [Test]
        [Arguments("Scale")]
        [Arguments("Ship")]
        public async Task TheHandleGivesTheSameEntryPointAsReflection(string name)
        {
            var descriptor = ClrObject.From<Native>().MethodTable.FindMethod(name);
            var reflected = typeof(Native).GetMethod(name, All);

            RuntimeHelpers.PrepareMethod(reflected.MethodHandle);

            await Assert.That(descriptor).IsNotNull();
            await Assert.That(descriptor.EntryPoint).IsEqualTo(reflected.MethodHandle.GetFunctionPointer());
        }

        [Test]
        public async Task PrepareJitsWithoutReflection()
        {
            var descriptor = ClrObject.From<Native>().MethodTable.FindMethod("Scale");

            var handle = descriptor.Prepare();

            await Assert.That(handle.Value).IsEqualTo(descriptor.ClrPointer);
            await Assert.That(handle.GetFunctionPointer()).IsNotEqualTo(IntPtr.Zero);
        }

        /// <summary>The bridge back to reflection has to land on the same method.</summary>
        [Test]
        public async Task TheMethodDescResolvesBackToItsMethod()
        {
            var descriptor = ClrObject.From<Native>().MethodTable.FindMethod("Scale");

            await Assert.That(descriptor.Method).IsEqualTo(typeof(Native).GetMethod("Scale", All));
        }

        [Test]
        public async Task MethodsAreFoundByName()
        {
            var table = ClrObject.From<Native>().MethodTable;

            await Assert.That(table.FindMethod("Scale")?.Name).IsEqualTo("Scale");
            await Assert.That(table.FindMethod("NoSuchMethod")).IsNull();
            await Assert.That(table.FindMethods("Scale").Count()).IsEqualTo(1);
        }

        // ---------- precode and vtable ----------

        [Test]
        public async Task ThePrecodeIsTheSameFromEitherRoute()
        {
            var descriptor = ClrObject.From<Native>().MethodTable.FindMethod("Scale");
            var reflected = typeof(Native).GetMethod("Scale", All);

            var fromDescription = MethodPrecode.Of(descriptor);
            var fromReflection = MethodPrecode.Of(reflected);

            await Assert.That(fromDescription.EntryPoint).IsEqualTo(fromReflection.EntryPoint);
            await Assert.That(fromDescription.DispatchSlot).IsEqualTo(fromReflection.DispatchSlot);
            await Assert.That(fromDescription.IsFixupPrecode).IsEqualTo(fromReflection.IsFixupPrecode);

            // It still names itself, from metadata rather than from reflection.
            await Assert.That(fromDescription.ToString()).Contains("Scale");
        }

        /// <summary>
        /// The MethodDesc records its own slot number, so the vtable slot needs no token
        /// matching - and must land in the same place the reflection route does.
        /// </summary>
        [Test]
        public async Task TheVtableSlotIsTheSameFromEitherRoute()
        {
            var table = ClrObject.From<Native>().MethodTable;

            var ship = table.FindMethod("Ship");
            var scale = table.FindMethod("Scale");

            await Assert.That(MethodVtable.FindSlot(ship))
                .IsEqualTo(MethodVtable.FindSlot(typeof(Native).GetMethod("Ship", All)));

            // Scale is not virtual, so neither route finds it a slot.
            await Assert.That(MethodVtable.FindSlot(scale)).IsEqualTo(IntPtr.Zero);
        }

        // ---------- detours from a MethodDesc ----------

        [Test]
        public async Task ARedirectCanBeDrivenFromMethodDescsAlone()
        {
            var instance = new Native();

            var target = ClrObject.From<Native>().MethodTable.FindMethod("Scale");
            var standIn = ClrObject.From<NativeProxy>().MethodTable.FindMethod("Scale");

            await Assert.That(instance.Scale(2)).IsEqualTo(8);

            using (var detour = MethodDetour.Redirect(target, new NativeProxy(), standIn))
            {
                await Assert.That(instance.Scale(2)).IsEqualTo(1234);
                await Assert.That(detour.TargetDescription?.Name).IsEqualTo("Scale");
            }

            await Assert.That(instance.Scale(2)).IsEqualTo(8);
        }

        [Test]
        public async Task ABodyCanBeReplacedFromAMethodDescAlone()
        {
            var instance = new Native();
            var target = ClrObject.From<Native>().MethodTable.FindMethod("Scale");

            using (MethodDetour.ReplaceBody(target, il =>
                   {
                       il.Emit(OpCodes.Ldarg_1);
                       il.Emit(OpCodes.Ldc_I4, 10);
                       il.Emit(OpCodes.Mul);
                       il.Emit(OpCodes.Ret);
                   }))
            {
                await Assert.That(instance.Scale(2)).IsEqualTo(20);
            }

            await Assert.That(instance.Scale(2)).IsEqualTo(8);
        }

        /// <summary>A virtual target patched from a MethodDesc must still get both slots.</summary>
        [Test]
        public async Task AVirtualTargetPatchedFromAMethodDescGetsBothSlots()
        {
            var target = ClrObject.From<Native>().MethodTable.FindMethod("Ship");

            using var detour = MethodDetour.ReplaceBody(target, il =>
            {
                il.Emit(OpCodes.Ldstr, "held");
                il.Emit(OpCodes.Ret);
            });

            await Assert.That(new Native().Ship()).IsEqualTo("held");
            await Assert.That(detour.PatchedTargets)
                .IsEqualTo(DetourTargets.Precode | DetourTargets.Vtable);
        }

        [Test]
        public async Task RefusesANullMethodDesc()
        {
            await Assert.That(() => MethodDetour.ReplaceBody((ClrMethodDescription)null, il => il.Emit(OpCodes.Ret)))
                .Throws<ArgumentNullException>();
        }
    }
}