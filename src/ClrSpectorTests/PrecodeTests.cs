using System;
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
    public class PrecodeSample
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public virtual int Virtual() => 1;
        [MethodImpl(MethodImplOptions.NoInlining)] public int NonVirtual() => 2;
        [MethodImpl(MethodImplOptions.NoInlining)] public static int Static() => 3;
    }

    /// <summary>
    /// The precode and vtable surfaces - the two ways a method can be reached.
    /// </summary>
    public class PrecodeTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static;

        [Test]
        [Arguments("Virtual")]
        [Arguments("NonVirtual")]
        [Arguments("Static")]
        public async Task EntryPointIsARipRelativeJumpWithADispatchSlot(string name)
        {
            var precode = MethodPrecode.Of(typeof(PrecodeSample).GetMethod(name, All));

            await Assert.That(precode.EntryPoint).IsNotEqualTo(IntPtr.Zero);
            await Assert.That(precode.IsRipRelativeJump).IsTrue();
            await Assert.That(precode.EntryPointBytes[0]).IsEqualTo((byte)0xFF);
            await Assert.That(precode.EntryPointBytes[1]).IsEqualTo((byte)0x25);
            await Assert.That(precode.HasDispatchSlot).IsTrue();
            await Assert.That(precode.DispatchTarget).IsNotEqualTo(IntPtr.Zero);
            await Assert.That(precode.Disassembly).StartsWith("jmp qword [rip");
        }

        /// <summary>
        /// The slot sits one code page away from the stub, which is what StubCodePageSize
        /// describes - a useful cross-check that the displacement was decoded correctly.
        /// </summary>
        [Test]
        public async Task DispatchSlotSitsOneCodePageFromTheStub()
        {
            var precode = MethodPrecode.Of(typeof(PrecodeSample).GetMethod("NonVirtual", All));
            var distance = precode.DispatchSlot.ToInt64() - precode.EntryPoint.ToInt64();

            await Assert.That(distance).IsEqualTo((long)PrecodeMachineInfo.Current.StubCodePageSize);
        }

        [Test]
        public async Task MachineDescriptorReportsSaneValues()
        {
            var machine = PrecodeMachineInfo.Current;

            await Assert.That(machine.Address).IsNotEqualTo(IntPtr.Zero);
            await Assert.That(machine.StubCodePageSize).IsGreaterThan(0u);
            await Assert.That(machine.StubPrecodeSize).IsGreaterThan((byte)0);
            await Assert.That(machine.FixupStubPrecodeSize).IsGreaterThan((byte)0);

            // the precode kinds must be distinguishable from each other and from "invalid"
            await Assert.That(machine.FixupPrecodeType).IsNotEqualTo(machine.StubPrecodeType);
            await Assert.That(machine.FixupPrecodeType).IsNotEqualTo(machine.InvalidPrecodeType);

            // on x64 the fixup code follows the 6-byte rip-relative jump
            await Assert.That(machine.FixupCodeOffset).IsEqualTo((byte)6);
        }

        [Test]
        public async Task VirtualMethodsOccupyAVtableSlot()
        {
            var method = typeof(PrecodeSample).GetMethod("Virtual", All);
            var methodTable = ClrObject.From<PrecodeSample>().MethodTable;

            var slot = MethodVtable.FindSlotNumber(method);

            await Assert.That(slot).IsGreaterThanOrEqualTo(0);
            await Assert.That(slot).IsLessThan((int)methodTable.NumberOfVirtuals);
            await Assert.That(MethodVtable.FindSlot(method)).IsNotEqualTo(IntPtr.Zero);
        }

        [Test]
        [Arguments("NonVirtual")]
        [Arguments("Static")]
        public async Task NonVirtualMethodsOccupyNoVtableSlot(string name)
        {
            var method = typeof(PrecodeSample).GetMethod(name, All);

            await Assert.That(MethodVtable.FindSlotNumber(method)).IsEqualTo(-1);
            await Assert.That(MethodVtable.FindSlot(method)).IsEqualTo(IntPtr.Zero);
        }

        /// <summary>
        /// The vtable slot holds the real code, not the precode entry point. This is precisely
        /// why redirecting the precode alone leaves virtual calls running the original.
        /// </summary>
        [Test]
        public async Task VtableSlotHoldsTheDispatchTargetRatherThanTheEntryPoint()
        {
            var method = typeof(PrecodeSample).GetMethod("Virtual", All);
            new PrecodeSample().Virtual();   // ensure the slot is backpatched to real code

            var precode = MethodPrecode.Of(method);
            var vtableSlotValue = new MemoryReader(MethodVtable.FindSlot(method)).ReadIntPtr(0);

            await Assert.That(vtableSlotValue).IsNotEqualTo(precode.EntryPoint);
            await Assert.That(vtableSlotValue).IsEqualTo(precode.DispatchTarget);
        }

        [Test]
        public async Task VtableChunksHoldEightSlots()
        {
            await Assert.That(MethodVtable.SlotsPerVtableChunk).IsEqualTo(8);
        }
    }
}
