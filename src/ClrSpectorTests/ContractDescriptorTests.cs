using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClrSpector.Cdac;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    /// <summary>
    /// Guards the contract descriptor layer: that the runtime describes what the decoder needs,
    /// and that anything it cannot describe fails loudly instead of being guessed at.
    /// </summary>
    public class ContractDescriptorTests
    {
        [Test]
        public async Task DescriptorLoadsFromTheHostingRuntime()
        {
            var descriptor = ContractDescriptor.Current;

            await Assert.That(descriptor).IsNotNull();
            await Assert.That(descriptor.Baseline).IsNotNull();
            await Assert.That(descriptor.Contracts).IsNotEmpty();
        }

        [Test]
        [Arguments("MethodTable")]
        [Arguments("EEClass")]
        [Arguments("MethodDesc")]
        [Arguments("MethodDescChunk")]
        [Arguments("MethodTableAuxiliaryData")]
        public async Task DescribesTheTypesTheDecoderNeeds(string typeName)
        {
            await Assert.That(ContractDescriptor.Current.TryGetDataType(typeName, out _)).IsTrue();
        }

        [Test]
        public async Task MethodTableFieldsAreDistinctAndWithinTheStructure()
        {
            var methodTable = ContractDescriptor.Current.GetDataType("MethodTable");
            var names = new[]
            {
                "MTFlags", "BaseSize", "MTFlags2", "NumVirtuals",
                "NumInterfaces", "ParentMethodTable", "Module", "AuxiliaryData",
                "EEClassOrCanonMT", "PerInstInfo"
            };

            var offsets = names.Select(n => methodTable[n]).ToList();

            await Assert.That(offsets.Distinct().Count()).IsEqualTo(offsets.Count);
            await Assert.That(methodTable.Size).IsNotNull();
            await Assert.That(offsets.All(o => o >= 0 && o < methodTable.Size)).IsTrue();
        }

        /// <summary>
        /// A pointer-typed global holds the address of the runtime variable, so it needs one
        /// dereference. Comparing against reflection's type handles pins that down - reading it
        /// without the dereference yields a plausible-looking but wrong pointer.
        /// </summary>
        [Test]
        public async Task PointerGlobalsDereferenceToTheExpectedMethodTables()
        {
            var globals = ContractDescriptor.Current.Globals;

            await Assert.That(globals.Dereference("ObjectMethodTable"))
                .IsEqualTo(typeof(object).TypeHandle.Value);
            await Assert.That(globals.Dereference("StringMethodTable"))
                .IsEqualTo(typeof(string).TypeHandle.Value);
            await Assert.That(globals.Dereference("ObjectArrayMethodTable"))
                .IsEqualTo(typeof(object[]).TypeHandle.Value);
        }

        [Test]
        public async Task LiteralGlobalsAreReadable()
        {
            var globals = ContractDescriptor.Current.Globals;

            // MethodDescs are pointer-aligned, and the token remainder must fit in the field.
            await Assert.That(globals.Number("MethodDescAlignment")).IsEqualTo((ulong)IntPtr.Size);
            await Assert.That(globals.Number("MethodDescTokenRemainderBitCount")).IsGreaterThan(0UL);
            await Assert.That(globals.Text("Architecture")).IsNotEmpty();
        }

        /// <summary>
        /// A global naming an array is already at the address the pointer-data table gives, so
        /// it is read with Address rather than Dereference.
        /// </summary>
        /// <remarks>
        /// This used to assert on MethodDescSizeTable, which .NET 11 removed. ArrayBoundsZero is
        /// the same shape - the runtime's shared all-zeroes array bounds - and is published by
        /// both runtimes.
        /// </remarks>
        [Test]
        public async Task TableGlobalsExposeTheirAddressWithoutDereferencing()
        {
            await Assert.That(ContractDescriptor.Current.Globals.Address("ArrayBoundsZero"))
                .IsNotEqualTo(IntPtr.Zero);
        }

        [Test]
        public async Task PointerWidthAgreesWithTheProcess()
        {
            // Loading validates this; reaching here at all means the descriptor and process agree.
            await Assert.That(ContractDescriptor.Current).IsNotNull();
            await Assert.That(IntPtr.Size is 4 or 8).IsTrue();
        }

        [Test]
        public async Task UnknownTypeFailsLoudly()
        {
            await Assert.That(() => ContractDescriptor.Current.GetDataType("NoSuchRuntimeType"))
                .Throws<ClrSpectorUnsupportedRuntimeException>();
        }

        [Test]
        public async Task UnknownFieldFailsLoudly()
        {
            var methodTable = ContractDescriptor.Current.GetDataType("MethodTable");

            await Assert.That(() => methodTable["NoSuchField"])
                .Throws<ClrSpectorUnsupportedRuntimeException>();
        }

        [Test]
        public async Task UnknownGlobalFailsLoudly()
        {
            await Assert.That(() => ContractDescriptor.Current.Globals.Number("NoSuchGlobal"))
                .Throws<ClrSpectorUnsupportedRuntimeException>();
        }

        [Test]
        public async Task UnsupportedContractVersionFailsLoudly()
        {
            await Assert.That(() => ContractDescriptor.Current.RequireContract("RuntimeTypeSystem", 9999))
                .Throws<ClrSpectorUnsupportedRuntimeException>();
        }

        /// <summary>
        /// Failure messages must name the runtime, so a layout mismatch report identifies where
        /// it came from.
        /// </summary>
        [Test]
        public async Task FailureMessagesIdentifyTheRuntime()
        {
            try
            {
                ContractDescriptor.Current.GetDataType("NoSuchRuntimeType");
            }
            catch (ClrSpectorUnsupportedRuntimeException e)
            {
                await Assert.That(e.Message).Contains(RuntimeInformation.FrameworkDescription);
                return;
            }

            Assert.Fail("expected a ClrSpectorUnsupportedRuntimeException");
        }
    }
}