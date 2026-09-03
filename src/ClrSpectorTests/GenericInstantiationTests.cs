using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClrSpector;

namespace ClrSpectorTests
{
    /// <summary>
    /// Checks the instantiation a generic type carries, and closing an open signature with it.
    /// </summary>
    /// <remarks>
    /// Metadata only ever records the open definition, so <c>!0</c> is as far as a signature can
    /// take you. The actual arguments are in the runtime's own structures - a MethodTable's
    /// PerInstInfo - and reaching them is the difference between reporting <c>void Add(!0)</c>
    /// and <c>void Add(System.Int32)</c>.
    /// </remarks>
    public class GenericInstantiationTests
    {
        [Test]
        public async Task AConstructedGenericReportsItsTypeArguments()
        {
            var list = ClrObject.From<List<int>>().MethodTable;

            await Assert.That(list.HasInstantiation).IsTrue();
            await Assert.That(list.GenericDictionaryCount).IsEqualTo((ushort)1);
            await Assert.That(list.GenericTypeArgumentCount).IsEqualTo((ushort)1);

            var arguments = list.TypeArguments;

            await Assert.That(arguments.Length).IsEqualTo(1);
            await Assert.That(ClrMethodTable.IsMethodTableHandle(arguments[0])).IsTrue();
            await Assert.That(arguments[0]).IsEqualTo(typeof(int).TypeHandle.Value);
        }

        [Test]
        public async Task TwoTypeParametersComeBackInOrder()
        {
            var map = ClrObject.From<Dictionary<string, int>>().MethodTable;

            await Assert.That(map.GenericTypeArgumentCount).IsEqualTo((ushort)2);

            var arguments = map.TypeArguments;

            await Assert.That(arguments.Length).IsEqualTo(2);
            await Assert.That(arguments[0]).IsEqualTo(typeof(string).TypeHandle.Value);
            await Assert.That(arguments[1]).IsEqualTo(typeof(int).TypeHandle.Value);
        }

        /// <summary>
        /// An open definition's arguments are type variables, which are TypeDescs rather than
        /// MethodTables - so they must not be decoded as one.
        /// </summary>
        [Test]
        public async Task AnOpenDefinitionsArgumentsAreTypeVariables()
        {
            var open = ClrObject.From(typeof(List<>)).MethodTable;

            await Assert.That(open.IsGenericTypeDefinition).IsTrue();
            await Assert.That(open.GenericTypeArgumentCount).IsEqualTo((ushort)1);
            await Assert.That(ClrMethodTable.IsMethodTableHandle(open.TypeArguments[0])).IsFalse();
        }

        [Test]
        public async Task ANonGenericTypeHasNoInstantiation()
        {
            var plain = ClrObject.From<object>().MethodTable;

            await Assert.That(plain.HasInstantiation).IsFalse();
            await Assert.That(plain.GenericTypeArgumentCount).IsEqualTo((ushort)0);
            await Assert.That(plain.TypeArguments).IsEmpty();
        }

        /// <summary>
        /// A value type argument gets its own code, so its signature closes to the real type.
        /// </summary>
        [Test]
        public async Task AValueTypeInstantiationClosesToTheRealType()
        {
            var add = ClrObject.From<List<int>>().MethodTable.Methods
                .FirstOrDefault(m => m.Name == "Add");

            await Assert.That(add).IsNotNull();

            var open = add.Signature;
            await Assert.That(open.Parameters[0].Type.ElementType).IsEqualTo(CorElementType.VAR);
            await Assert.That(open.IsClosed).IsFalse();

            var closed = add.ClosedSignature;
            await Assert.That(closed.IsClosed).IsTrue();
            await Assert.That(closed.Parameters[0].Type.TypeName).IsEqualTo("System.Int32");
            await Assert.That(closed.Parameters[0].Type.SubstitutedMethodTable)
                .IsEqualTo(typeof(int).TypeHandle.Value);
        }

        /// <summary>
        /// A reference instantiation shares one body of code, compiled against __Canon - so that
        /// is what closing against the declaring MethodTable honestly reports.
        /// </summary>
        [Test]
        public async Task AReferenceInstantiationClosesToCanonThroughItsDeclaringType()
        {
            var add = ClrObject.From<List<string>>().MethodTable.Methods
                .FirstOrDefault(m => m.Name == "Add");

            await Assert.That(add).IsNotNull();

            var closed = add.ClosedSignature;

            await Assert.That(closed.IsClosed).IsTrue();
            await Assert.That(closed.Parameters[0].Type.TypeName).IsEqualTo("System.__Canon");
        }

        /// <summary>
        /// Closing against one exact instantiation instead reports what a caller of it passes.
        /// </summary>
        [Test]
        public async Task ClosingAgainstAnExactInstantiationReportsTheRealArgument()
        {
            var instantiation = ClrObject.From<List<string>>().MethodTable;
            var add = instantiation.Methods.FirstOrDefault(m => m.Name == "Add");

            await Assert.That(add).IsNotNull();

            var closed = add.ClosedSignatureFor(instantiation);

            await Assert.That(closed.IsClosed).IsTrue();
            await Assert.That(closed.Parameters[0].Type.TypeName).IsEqualTo("System.String");
        }

        /// <summary>
        /// With nothing to substitute, an open signature must come back untouched rather than
        /// half-closed or guessed at.
        /// </summary>
        [Test]
        public async Task AnOpenDefinitionClosesToItself()
        {
            var add = ClrObject.From(typeof(List<>)).MethodTable.Methods
                .FirstOrDefault(m => m.Name == "Add");

            await Assert.That(add).IsNotNull();

            var closed = add.ClosedSignature;

            await Assert.That(closed.IsClosed).IsFalse();
            await Assert.That(closed.Parameters[0].Type.ElementType).IsEqualTo(CorElementType.VAR);
            await Assert.That(closed.ToString()).IsEqualTo(add.Signature.ToString());
        }

        /// <summary>
        /// A return type is substituted the same way a parameter is - Dictionary's indexer
        /// returns its second parameter, which is the value type argument.
        /// </summary>
        [Test]
        public async Task ReturnTypesAreSubstitutedToo()
        {
            var instantiation = ClrObject.From<Dictionary<string, int>>().MethodTable;
            var indexer = instantiation.Methods.FirstOrDefault(m => m.Name == "get_Item");

            await Assert.That(indexer).IsNotNull();
            await Assert.That(indexer.Signature.ReturnType.ElementType).IsEqualTo(CorElementType.VAR);

            var closed = indexer.ClosedSignatureFor(instantiation);

            await Assert.That(closed.ReturnType.TypeName).IsEqualTo("System.Int32");
            await Assert.That(closed.Parameters[0].Type.TypeName).IsEqualTo("System.String");
        }

        /// <summary>
        /// The generic methods a MethodTable's chunks hold are the open definitions, which is the
        /// one Flags2 kind measurement can confirm.
        /// </summary>
        [Test]
        public async Task GenericMethodsFoundThroughATypeAreDefinitions()
        {
            var instantiated = ClrObject.From<List<int>>().MethodTable.Methods
                .Where(m => m.Classification == MethodClassification.Instantiated)
                .ToList();

            await Assert.That(instantiated).IsNotEmpty();

            foreach (var method in instantiated)
            {
                await Assert.That(method.GenericKind)
                    .IsEqualTo(GenericMethodKind.GenericMethodDefinition);

                await Assert.That(method.IsGenericMethodDefinition).IsTrue();
                await Assert.That(method.InstantiationFlags).IsNotEqualTo((ushort)0);
            }
        }

        /// <summary>
        /// BaseSizePadding is what separates "this object is 24 bytes" from "its fields are".
        /// </summary>
        [Test]
        public async Task BaseSizePaddingAccountsForTheAllocatorsFloor()
        {
            var plain = ClrObject.From<object>().MethodTable;
            var eeClass = plain.EEClass;

            await Assert.That(eeClass).IsNotNull();

            // Object has no fields at all, so every byte of its base size is padding.
            await Assert.That(eeClass.BaseSizePadding).IsEqualTo((byte)plain.BaseSize);
            await Assert.That(eeClass.HasOptionalFields).IsFalse();
        }

        [Test]
        public async Task ATypesRuntimeStructuresRecordTheirLoaderModule()
        {
            var table = ClrObject.From<List<string>>().MethodTable;

            await Assert.That(table.AuxiliaryData).IsNotEqualTo(IntPtr.Zero);
            await Assert.That(table.LoaderModule).IsNotEqualTo(IntPtr.Zero);

            // All-CoreLib instantiation, so it is loaded from the module that declares the type.
            await Assert.That(table.LoaderModule).IsEqualTo(ClrModule.Of(typeof(object)).Address);
        }
    }
}
