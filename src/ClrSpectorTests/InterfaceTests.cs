using System;
using System.Linq;
using System.Reflection.Metadata;
using System.Threading.Tasks;
using ClrSpector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    public interface ILocalContract
    {
        int Required(int x);

        /// <summary>A default implementation - a body declared on the interface itself.</summary>
        int Defaulted(int x) => x * 2;

        static int Shared = 7;
    }

    public interface IInherited : ILocalContract
    {
        int Extra();
    }

    public class Contracted : ILocalContract, IDisposable, IComparable<Contracted>
    {
        public int Required(int x) => x;

        public void Dispose()
        {
        }

        public int CompareTo(Contracted other) => 0;
    }

    public class InheritsContracts : IInherited
    {
        public int Required(int x) => x;

        public int Extra() => 1;
    }

    /// <summary>Implements an interface, so its own metadata row declares it.</summary>
    public class DeclaresItself : ILocalContract
    {
        public int Required(int x) => x;
    }

    /// <summary>Inherits the implementation, so its own row declares nothing.</summary>
    public class InheritsFromBase : DeclaresItself
    {
    }

    /// <summary>
    /// The interfaces a type declares, and what is inside them.
    /// </summary>
    /// <remarks>
    /// The runtime builds an interface map on every MethodTable but the contract publishes only
    /// the count, so these come from metadata instead. Each interface is then resolved back to
    /// its own MethodTable through the module's lookup maps, at which point everything else in
    /// the library - methods, fields, IL - applies to it unchanged.
    /// </remarks>
    public class InterfaceTests
    {
        [Test]
        public async Task ListsTheInterfacesATypeDeclares()
        {
            var declared = ClrObject.From<Contracted>().MethodTable.DeclaredInterfaces;

            await Assert.That(declared.Count).IsEqualTo(3);
            await Assert.That(declared.Select(i => i.Name)).Contains(typeof(ILocalContract).FullName);
            await Assert.That(declared.Select(i => i.Name)).Contains("System.IDisposable");
        }

        /// <summary>An interface in this module is a TypeDef and resolves through that map.</summary>
        [Test]
        public async Task AnInterfaceInTheSameModuleResolvesToItsMethodTable()
        {
            var local = ClrObject.From<Contracted>().MethodTable.DeclaredInterfaces
                .Single(i => i.Name == typeof(ILocalContract).FullName);

            await Assert.That(local.Kind).IsEqualTo(HandleKind.TypeDefinition);
            await Assert.That(local.MethodTablePointer)
                .IsEqualTo(ClrObject.From<ILocalContract>().MethodTable.Address);
            await Assert.That(local.Interface.IsInterface).IsTrue();
        }

        /// <summary>One in another module is a TypeRef, and resolves through the other map.</summary>
        [Test]
        public async Task AnInterfaceInAnotherModuleResolvesThroughTheTypeRefMap()
        {
            var disposable = ClrObject.From<Contracted>().MethodTable.DeclaredInterfaces
                .Single(i => i.Name == "System.IDisposable");

            await Assert.That(disposable.Kind).IsEqualTo(HandleKind.TypeReference);
            await Assert.That(disposable.MethodTablePointer)
                .IsEqualTo(ClrObject.From<IDisposable>().MethodTable.Address);
        }

        /// <summary>
        /// A constructed generic has a MethodTable per instantiation, which is in neither lookup
        /// map - so it is named from its signature but not reached.
        /// </summary>
        [Test]
        public async Task AConstructedGenericInterfaceIsNamedButNotResolved()
        {
            var comparable = ClrObject.From<Contracted>().MethodTable.DeclaredInterfaces
                .Single(i => i.IsConstructedGeneric);

            await Assert.That(comparable.Kind).IsEqualTo(HandleKind.TypeSpecification);
            await Assert.That(comparable.Name).IsEqualTo($"System.IComparable<{typeof(Contracted).FullName}>");
            await Assert.That(comparable.MethodTablePointer).IsEqualTo(IntPtr.Zero);
            await Assert.That(comparable.Interface).IsNull();
        }

        /// <summary>
        /// A class's own metadata row already carries the closure of the interfaces it names, so
        /// the two counts usually agree - the C# compiler writes both IDerived and its base.
        /// </summary>
        [Test]
        public async Task AClassDeclaresTheClosureOfItsOwnInterfaces()
        {
            var table = ClrObject.From<InheritsContracts>().MethodTable;

            await Assert.That(table.DeclaredInterfaces.Select(i => i.Name))
                .Contains(typeof(IInherited).FullName);
            await Assert.That(table.DeclaredInterfaces.Select(i => i.Name))
                .Contains(typeof(ILocalContract).FullName);
        }

        /// <summary>
        /// The counts diverge where the implementation is inherited: the derived class's own row
        /// declares nothing, while the runtime still counts the interface it has through its base.
        /// </summary>
        [Test]
        public async Task AnInterfaceInheritedFromABaseClassIsNotDeclared()
        {
            var baseTable = ClrObject.From<DeclaresItself>().MethodTable;
            var derived = ClrObject.From<InheritsFromBase>().MethodTable;

            await Assert.That(baseTable.DeclaredInterfaces.Count).IsEqualTo(1);

            await Assert.That(derived.DeclaredInterfaces).IsEmpty();
            await Assert.That(derived.NumberOfInterfaces).IsGreaterThan((ushort)0);
        }

        // ---------- what is inside an interface ----------

        [Test]
        public async Task AnInterfaceMethodWithABodyIsADefaultImplementation()
        {
            var contract = ClrObject.From<ILocalContract>().MethodTable;

            var required = contract.FindMethod(nameof(ILocalContract.Required));
            var defaulted = contract.FindMethod(nameof(ILocalContract.Defaulted));

            await Assert.That(required.HasBody).IsFalse();
            await Assert.That(required.ReadIl()).IsNull();

            await Assert.That(defaulted.HasBody).IsTrue();
            await Assert.That(defaulted.ReadIl()).IsNotNull();
        }

        /// <summary>The default implementation's IL is readable like any other method's.</summary>
        [Test]
        public async Task ADefaultImplementationsIlCanBeDumped()
        {
            var defaulted = ClrObject.From<ILocalContract>().MethodTable
                .FindMethod(nameof(ILocalContract.Defaulted));

            var il = ClrMethodIl.Of(defaulted);

            await Assert.That(il).IsNotNull();
            await Assert.That(il.Instructions).IsNotEmpty();

            // x * 2 - the multiply and the return have to be in there.
            var opcodes = il.Instructions.Select(i => i.OpCode.Name).ToList();
            await Assert.That(opcodes).Contains("mul");
            await Assert.That(opcodes).Contains("ret");

            await Assert.That(il.Dump()).Contains("Defaulted");
        }

        /// <summary>An interface can carry static fields, and they decode like any others.</summary>
        [Test]
        public async Task InterfaceFieldsAreListed()
        {
            var contract = ClrObject.From<ILocalContract>().MethodTable;

            var shared = contract.Fields
                .Single(f => contract.Metadata.FieldName(f.MetadataToken) == nameof(ILocalContract.Shared));

            await Assert.That(shared.IsStatic).IsTrue();
            await Assert.That(shared.ElementType).IsEqualTo(CorElementType.I4);
        }

        [Test]
        public async Task ATypeWithNoInterfacesDeclaresNone()
        {
            await Assert.That(ClrObject.From<object>().MethodTable.DeclaredInterfaces).IsEmpty();
        }
    }
}
