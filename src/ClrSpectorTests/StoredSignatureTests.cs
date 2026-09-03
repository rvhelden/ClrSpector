using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using ClrSpector;

namespace ClrSpectorTests
{
    public delegate int StoredSignatureDelegate(int a, string b);

    /// <summary>
    /// Checks the MethodDescs that carry their own signature instead of pointing at metadata.
    /// </summary>
    /// <remarks>
    /// These are the ones a metadata-only decoder cannot see at all. A dynamic method was never in
    /// any module's tables and an array's accessors are synthesised per array type, so their
    /// tokens name nothing - the runtime keeps the signature blob on the MethodDesc instead, and
    /// before this it was simply missing.
    /// </remarks>
    public class StoredSignatureTests
    {
        /// <summary>
        /// An array type's methods are Get, Set, Address and a constructor, none of them declared
        /// anywhere - the runtime makes them for each array type it builds.
        /// </summary>
        [Test]
        public async Task ArrayAccessorsDecodeFromTheirStoredSignature()
        {
            var methods = ClrObject.From<int[]>().MethodTable.Methods;

            await Assert.That(methods).IsNotEmpty();

            foreach (var method in methods)
            {
                await Assert.That(method.Classification).IsEqualTo(MethodClassification.Array);
                await Assert.That(method.HasStoredSignature).IsTrue();
                await Assert.That(method.StoredSignatureLength).IsGreaterThan(0U);

                var signature = method.Signature;

                await Assert.That(signature).IsNotNull();
                await Assert.That(signature.IsStored).IsTrue();

                // Every index is an int; the element type shows as a type variable, since the
                // signature is the runtime's own and generic over the array's element.
                foreach (var parameter in signature.Parameters.Take(1))
                    await Assert.That(parameter.Type.ElementType).IsEqualTo(CorElementType.I4);
            }

            // Get returns the element, Set returns void and takes one more, Address returns a
            // byref. Rank one means one index apiece.
            var rendered = methods.Select(m => m.Signature.ToString()).ToList();

            await Assert.That(rendered).Contains("!0 (int arg0)");
            await Assert.That(rendered).Contains("void (int arg0, !0 arg1)");
            await Assert.That(rendered).Contains("ref !0 (int arg0)");
        }

        /// <summary>A rank-two array takes two indexes everywhere, which the blob must show.</summary>
        [Test]
        public async Task ARankTwoArrayTakesTwoIndexes()
        {
            var methods = ClrObject.From<string[,]>().MethodTable.Methods;
            var get = methods.Select(m => m.Signature)
                .FirstOrDefault(s => s != null && s.ReturnType.ElementType == CorElementType.VAR);

            await Assert.That(get).IsNotNull();
            await Assert.That(get.Parameters.Count).IsEqualTo(2);
            await Assert.That(get.Parameters[0].Type.ElementType).IsEqualTo(CorElementType.I4);
            await Assert.That(get.Parameters[1].Type.ElementType).IsEqualTo(CorElementType.I4);
        }

        /// <summary>
        /// A dynamic method has neither a metadata row nor a string heap entry, so both its name
        /// and its signature have to come off the MethodDesc.
        /// </summary>
        /// <remarks>
        /// <c>DynamicMethod.MethodHandle</c> throws, so the MethodDesc address is obtained through
        /// the runtime's internal accessor. That is the only reflection here - everything read
        /// from the address onwards is ClrSpector's.
        /// </remarks>
        [Test]
        public async Task ADynamicMethodIsNamedAndSignedFromItsMethodDesc()
        {
            var dynamicMethod = new DynamicMethod(
                "EmittedForTest", typeof(int), new[] { typeof(int), typeof(string) });

            var generator = dynamicMethod.GetILGenerator();
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ret);

            // Forces the runtime to build the MethodDesc.
            var invoke = (StoredSignatureDelegate)dynamicMethod.CreateDelegate(
                typeof(StoredSignatureDelegate));

            await Assert.That(invoke(7, "x")).IsEqualTo(7);

            var accessor = typeof(DynamicMethod).GetMethod(
                "GetMethodDescriptor", BindingFlags.Instance | BindingFlags.NonPublic);

            await Assert.That(accessor).IsNotNull();

            var handle = (RuntimeMethodHandle)accessor.Invoke(dynamicMethod, null);
            var method = ClrMethodDescription.At(handle.Value);

            await Assert.That(method).IsNotNull();
            await Assert.That(method.Classification).IsEqualTo(MethodClassification.Dynamic);
            await Assert.That(method.StoredName).IsEqualTo("EmittedForTest");
            await Assert.That(method.Name).IsEqualTo("EmittedForTest");

            var signature = method.Signature;

            await Assert.That(signature).IsNotNull();
            await Assert.That(signature.IsStored).IsTrue();
            await Assert.That(signature.ReturnType.ElementType).IsEqualTo(CorElementType.I4);
            await Assert.That(signature.Parameters.Count).IsEqualTo(2);
            await Assert.That(signature.Parameters[0].Type.ElementType).IsEqualTo(CorElementType.I4);
            await Assert.That(signature.Parameters[1].Type.ElementType).IsEqualTo(CorElementType.STRING);

            GC.KeepAlive(invoke);
        }

        /// <summary>
        /// A delegate's runtime-implemented methods have a stored signature <i>and</i> a metadata
        /// row, and metadata is the better source - it is the one with parameter names.
        /// </summary>
        [Test]
        public async Task MetadataIsPreferredWhenAMethodHasBoth()
        {
            var invoke = ClrObject.From<StoredSignatureDelegate>().MethodTable.Methods
                .FirstOrDefault(m => m.Name == "Invoke");

            await Assert.That(invoke).IsNotNull();
            await Assert.That(invoke.Classification).IsEqualTo(MethodClassification.EEImpl);
            await Assert.That(invoke.HasStoredSignature).IsTrue();

            var signature = invoke.Signature;

            await Assert.That(signature).IsNotNull();
            await Assert.That(signature.IsStored).IsFalse();

            // The names are the proof it came from metadata; a stored blob carries none.
            await Assert.That(signature.Parameters[0].Name).IsEqualTo("a");
            await Assert.That(signature.Parameters[1].Name).IsEqualTo("b");
        }

        /// <summary>
        /// The point of the exercise: every MethodDesc of these types now yields a signature,
        /// where the array types previously yielded none at all.
        /// </summary>
        [Test]
        [Arguments(typeof(int[]))]
        [Arguments(typeof(string[,]))]
        [Arguments(typeof(StoredSignatureDelegate))]
        [Arguments(typeof(object))]
        public async Task EveryMethodDescYieldsASignature(Type type)
        {
            var methods = ClrObject.From(type).MethodTable.Methods;

            await Assert.That(methods).IsNotEmpty();

            foreach (var method in methods)
            {
                await Assert.That(method.Signature)
                    .IsNotNull()
                    .Because($"{type.Name} has a {method.Classification} method with no signature");
            }
        }

        /// <summary>
        /// A MethodDesc reached by bare address must reassemble the same token as one reached
        /// through its chunk, because that is the only thing proving the chunk was recovered.
        /// </summary>
        [Test]
        public async Task AMethodDescDecodedByAddressMatchesOneDecodedThroughItsChunk()
        {
            foreach (var expected in ClrObject.From<StoredSignatureTests>().MethodTable.Methods)
            {
                var byAddress = ClrMethodDescription.At(expected.ClrPointer);

                await Assert.That(byAddress.MetadataToken).IsEqualTo(expected.MetadataToken);
                await Assert.That(byAddress.SlotNumber).IsEqualTo(expected.SlotNumber);
                await Assert.That(byAddress.Name).IsEqualTo(expected.Name);
            }
        }
    }
}
