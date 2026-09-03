using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClrSpector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    /// <summary>A type whose MethodDescs carry async method data.</summary>
    public class AsyncSampleClass
    {
        public async Task<int> GetValueAsync()
        {
            await Task.Yield();

            return 1;
        }

        public async Task DoWorkAsync()
        {
            await Task.Yield();
        }

        public async ValueTask<string> GetTextAsync()
        {
            await Task.Yield();

            return "text";
        }

        public int Plain() => 1;
    }

    /// <summary>
    /// .NET 11 added a fourth optional slot to MethodDesc - async method data - and at 24 bytes
    /// it is the largest of them. Leaving it out of the size calculation undercounts every async
    /// method's MethodDesc and desynchronises the rest of its chunk, which took out 68 of ~2500
    /// CoreLib types. Cross-referenced against the runtime's own cdac reader, which adds
    /// <c>Data.AsyncMethodData.GetSize(target)</c> for <c>MethodDescFlags.HasAsyncMethodData</c>.
    /// </summary>
    public class AsyncMethodDescTests
    {
        /// <summary>MethodDesc.Flags bit 0x40 - HasAsyncMethodData.</summary>
        private const ushort HasAsyncMethodDataFlag = 0x0040;

        [Test]
        public async Task WalksATypeWhoseMethodsCarryAsyncMethodData()
        {
            var methodTable = ClrObject.From(typeof(AsyncSampleClass)).MethodTable;

            // The walk is cross-checked against each MethodDesc's own ChunkIndex, so getting
            // here at all means every step landed on a real MethodDesc boundary.
            await Assert.That(methodTable.Methods).IsNotEmpty();

            var withAsyncData = methodTable.Methods.Count(m => (m.Flags & HasAsyncMethodDataFlag) != 0);

            await Assert.That(withAsyncData).IsGreaterThan(0);
        }

        /// <summary>
        /// The framework's own async plumbing is where this fails first, so walk it rather than
        /// only a hand-written sample.
        /// </summary>
        [Test]
        [Arguments(typeof(Task))]
        [Arguments(typeof(Task<int>))]
        [Arguments(typeof(ValueTask))]
        [Arguments(typeof(IAsyncDisposable))]
        public async Task WalksFrameworkAsyncTypes(Type type)
        {
            var methodTable = ClrObject.From(type).MethodTable;

            await Assert.That(methodTable.Methods).IsNotEmpty();
        }

        /// <summary>
        /// A MethodDesc claiming async method data must be sized larger than the same MethodDesc
        /// without it - the assertion that would have caught the omission directly.
        /// </summary>
        [Test]
        public async Task AsyncMethodDataWidensTheMethodDesc()
        {
            var methodTable = ClrObject.From(typeof(AsyncSampleClass)).MethodTable;

            var ordered = methodTable.Methods.OrderBy(m => m.ChunkIndex).ToList();

            var asyncMethod = ordered.FirstOrDefault(m => (m.Flags & HasAsyncMethodDataFlag) != 0);
            await Assert.That(asyncMethod).IsNotNull();

            var next = ordered.SkipWhile(m => m != asyncMethod).Skip(1).FirstOrDefault();
            await Assert.That(next).IsNotNull();

            // ChunkIndex counts MethodDescAlignment units, so the gap is the async MethodDesc's
            // own size: a base MethodDesc plus a non-vtable slot, a native code slot and the
            // 24-byte async method data.
            var alignment = (int)ClrObject.Descriptor.Globals.Number("MethodDescAlignment");
            var size = (next.ChunkIndex - asyncMethod.ChunkIndex) * alignment;

            var asyncMethodDataSize = ClrObject.Descriptor.GetDataType("AsyncMethodData").RequiredSize;
            var baseSize = ClrObject.Descriptor.GetDataType("MethodDesc").RequiredSize;

            await Assert.That(size).IsGreaterThanOrEqualTo((int)(baseSize + asyncMethodDataSize));
        }
    }
}
