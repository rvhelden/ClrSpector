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
    public struct SignaturePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>Every signature shape the decoder has to cope with, in one place.</summary>
    public class SignatureSubject
    {
        private int slot;

        public int Add(int a, string b) => a + b.Length;

        public static void Nothing()
        {
        }

        public ref int ByRef(ref long x, out double y)
        {
            y = 0;
            x = 0;

            return ref this.slot;
        }

        public List<string> Generic<T>(T[] items, Span<byte> span) => null;

        public Dictionary<string, List<int[]>> Nested(SignaturePoint p, SignaturePoint? q) => null;

        public unsafe byte* Pointers(int** pp, void* raw) => null;

        public int[,,] MultiDimension(int[][] jagged) => null;

        public void In(in SignaturePoint p)
        {
        }
    }

    /// <summary>
    /// Checks the signature decoder, which reads a MethodDefSig blob out of the mapped image with
    /// no <c>System.Reflection</c> and no <c>System.Reflection.Metadata</c> in the path.
    /// </summary>
    /// <remarks>
    /// The subject above is the point of these tests: a decoder that handles <c>int</c> and
    /// <c>string</c> proves very little, because the encoding only gets interesting at byrefs,
    /// pointers, multi-dimension arrays, nested instantiations and generic parameters. Each of
    /// those is a different branch of ECMA-335 II.23.2.12, and a wrong turn in any of them
    /// desynchronises the rest of the blob rather than producing one wrong type.
    /// </remarks>
    public class ClrMethodSignatureTests
    {
        private static ClrMethodSignature SignatureOf(string name)
        {
            var method = ClrObject.From<SignatureSubject>().MethodTable.Methods
                .FirstOrDefault(m => m.Name == name);

            if (method == null)
                Assert.Fail($"expected a MethodDesc named {name} on the subject");

            return method.Signature;
        }

        [Test]
        public async Task ReturnTypeAndParametersComeBackWithNamesAndTypes()
        {
            var signature = SignatureOf(nameof(SignatureSubject.Add));

            await Assert.That(signature.ReturnType.ElementType).IsEqualTo(CorElementType.I4);
            await Assert.That(signature.Parameters.Count).IsEqualTo(2);

            await Assert.That(signature.Parameters[0].Name).IsEqualTo("a");
            await Assert.That(signature.Parameters[0].Type.ElementType).IsEqualTo(CorElementType.I4);

            await Assert.That(signature.Parameters[1].Name).IsEqualTo("b");
            await Assert.That(signature.Parameters[1].Type.ElementType).IsEqualTo(CorElementType.STRING);
        }

        /// <summary>
        /// A signature blob has no names and no notion of <c>this</c>, so both have to come from
        /// elsewhere - the Param table and the calling convention respectively.
        /// </summary>
        [Test]
        public async Task HasThisAgreesWithWhetherTheMethodIsStatic()
        {
            var instance = SignatureOf(nameof(SignatureSubject.Add));
            var shared = SignatureOf(nameof(SignatureSubject.Nothing));

            await Assert.That(instance.HasThis).IsTrue();
            await Assert.That(shared.HasThis).IsFalse();
            await Assert.That(shared.ReturnType.ElementType).IsEqualTo(CorElementType.VOID);
            await Assert.That(shared.Parameters).IsEmpty();
        }

        /// <summary>
        /// The blob records <c>out</c> and <c>in</c> as plain byrefs. The direction is a Param
        /// table attribute, so a decoder that reads only the signature reports every one of them
        /// as <c>ref</c> - correctly, and uselessly.
        /// </summary>
        [Test]
        public async Task ByRefDirectionComesFromTheParamTableNotTheBlob()
        {
            var signature = SignatureOf(nameof(SignatureSubject.ByRef));

            await Assert.That(signature.ReturnType.IsByRef).IsTrue();
            await Assert.That(signature.ReturnType.Element.ElementType).IsEqualTo(CorElementType.I4);

            var x = signature.Parameters[0];
            await Assert.That(x.Type.IsByRef).IsTrue();
            await Assert.That(x.Type.Element.ElementType).IsEqualTo(CorElementType.I8);
            await Assert.That(x.IsOut).IsFalse();

            var y = signature.Parameters[1];
            await Assert.That(y.Type.IsByRef).IsTrue();
            await Assert.That(y.Type.Element.ElementType).IsEqualTo(CorElementType.R8);
            await Assert.That(y.IsOut).IsTrue();

            var input = SignatureOf(nameof(SignatureSubject.In)).Parameters[0];
            await Assert.That(input.Type.IsByRef).IsTrue();
            await Assert.That(input.IsIn).IsTrue();
        }

        /// <summary>
        /// A MethodDef signature is the open definition, so a method's own type parameters stay
        /// as MVAR with an index - substituting them needs an instantiation, which is not in
        /// metadata.
        /// </summary>
        [Test]
        public async Task GenericParametersStayOpenWithTheirIndex()
        {
            var signature = SignatureOf(nameof(SignatureSubject.Generic));

            await Assert.That(signature.IsGeneric).IsTrue();
            await Assert.That(signature.GenericParameterCount).IsEqualTo(1);

            var items = signature.Parameters[0].Type;
            await Assert.That(items.ElementType).IsEqualTo(CorElementType.SZARRAY);
            await Assert.That(items.Element.ElementType).IsEqualTo(CorElementType.MVAR);
            await Assert.That(items.Element.GenericParameterIndex).IsEqualTo(0);

            // The return type is a closed instantiation, so it decodes to a real argument.
            await Assert.That(signature.ReturnType.ElementType).IsEqualTo(CorElementType.GENERICINST);
            await Assert.That(signature.ReturnType.GenericArguments.Count).IsEqualTo(1);
            await Assert.That(signature.ReturnType.GenericArguments[0].ElementType)
                .IsEqualTo(CorElementType.STRING);
        }

        /// <summary>
        /// Nested instantiations are what make a signature a tree rather than a list.
        /// </summary>
        [Test]
        public async Task NestedInstantiationsDecodeAsATree()
        {
            var signature = SignatureOf(nameof(SignatureSubject.Nested));
            var returned = signature.ReturnType;

            await Assert.That(returned.ElementType).IsEqualTo(CorElementType.GENERICINST);
            await Assert.That(returned.GenericArguments.Count).IsEqualTo(2);
            await Assert.That(returned.GenericArguments[0].ElementType).IsEqualTo(CorElementType.STRING);

            // Dictionary<string, List<int[]>> - the second argument is itself an instantiation.
            var inner = returned.GenericArguments[1];
            await Assert.That(inner.ElementType).IsEqualTo(CorElementType.GENERICINST);
            await Assert.That(inner.GenericArguments[0].ElementType).IsEqualTo(CorElementType.SZARRAY);
            await Assert.That(inner.GenericArguments[0].Element.ElementType).IsEqualTo(CorElementType.I4);

            // A value type parameter carries a token, which resolves against the module.
            var point = signature.Parameters[0].Type;
            await Assert.That(point.ElementType).IsEqualTo(CorElementType.VALUETYPE);
            await Assert.That(point.TypeTable).IsEqualTo(MetadataTable.TypeDef);
            await Assert.That(point.TypeName).IsEqualTo("ClrSpectorTests.SignaturePoint");
        }

        [Test]
        public async Task PointersAndMultiDimensionArraysKeepTheirShape()
        {
            var pointers = SignatureOf(nameof(SignatureSubject.Pointers));

            await Assert.That(pointers.ReturnType.ElementType).IsEqualTo(CorElementType.PTR);
            await Assert.That(pointers.ReturnType.Element.ElementType).IsEqualTo(CorElementType.U1);

            var doublePointer = pointers.Parameters[0].Type;
            await Assert.That(doublePointer.ElementType).IsEqualTo(CorElementType.PTR);
            await Assert.That(doublePointer.Element.ElementType).IsEqualTo(CorElementType.PTR);
            await Assert.That(doublePointer.Element.Element.ElementType).IsEqualTo(CorElementType.I4);

            // void* is a pointer to VOID, which is only legal as a pointer target.
            await Assert.That(pointers.Parameters[1].Type.Element.ElementType)
                .IsEqualTo(CorElementType.VOID);

            var arrays = SignatureOf(nameof(SignatureSubject.MultiDimension));

            await Assert.That(arrays.ReturnType.ElementType).IsEqualTo(CorElementType.ARRAY);
            await Assert.That(arrays.ReturnType.Rank).IsEqualTo(3);
            await Assert.That(arrays.ReturnType.ToString()).IsEqualTo("int[,,]");

            // A jagged array is arrays of arrays, which is a different encoding entirely.
            var jagged = arrays.Parameters[0].Type;
            await Assert.That(jagged.ElementType).IsEqualTo(CorElementType.SZARRAY);
            await Assert.That(jagged.Element.ElementType).IsEqualTo(CorElementType.SZARRAY);
        }

        [Test]
        public async Task RenderedSignaturesReadLikeSource()
        {
            await Assert.That(SignatureOf(nameof(SignatureSubject.Add)).ToString())
                .IsEqualTo("int (int a, string b)");

            await Assert.That(SignatureOf(nameof(SignatureSubject.ByRef)).ToString())
                .IsEqualTo("ref int (ref long x, out double y)");
        }

        /// <summary>
        /// The breadth check: every method of a spread of framework types decodes.
        /// </summary>
        /// <remarks>
        /// CoreLib is the interesting target because its metadata is big enough to push the heap
        /// and table indexes to four bytes, which is the case a small assembly never exercises -
        /// and getting a column width wrong shifts every row after it.
        /// </remarks>
        [Test]
        [Arguments(typeof(object))]
        [Arguments(typeof(string))]
        [Arguments(typeof(System.Linq.Enumerable))]
        [Arguments(typeof(Dictionary<,>))]
        [Arguments(typeof(System.Threading.Tasks.Task<>))]
        [Arguments(typeof(Span<>))]
        [Arguments(typeof(Array))]
        [Arguments(typeof(Type))]
        public async Task EveryMethodOfAFrameworkTypeDecodes(Type type)
        {
            var methods = ClrObject.From(type).MethodTable.Methods;
            await Assert.That(methods).IsNotEmpty();

            var decoded = 0;
            foreach (var method in methods)
            {
                var signature = method.Signature;

                await Assert.That(signature).IsNotNull();
                await Assert.That(signature.ReturnType).IsNotNull();

                // A signature's parameter count is the count the blob declared, so a decode that
                // lost its place would disagree with the list it produced.
                await Assert.That(signature.Parameters.Count)
                    .IsGreaterThanOrEqualTo(signature.RequiredParameterCount);

                decoded++;
            }

            await Assert.That(decoded).IsEqualTo(methods.Count);
        }
    }
}
