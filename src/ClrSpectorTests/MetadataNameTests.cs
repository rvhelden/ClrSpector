using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ClrSpector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    public class NamedOuter
    {
        public class NamedNested
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public int Twice(int x) => x * 2;
        }

        public int Stock = 3;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Add(int a, int b) => a + b;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Check(int wanted)
        {
            if (wanted > this.Stock)
                return string.Concat("short by ", (wanted - this.Stock).ToString());

            return "ok";
        }
    }

    /// <summary>
    /// Names and IL taken from the module's own metadata, without reflection resolving anything.
    /// </summary>
    /// <remarks>
    /// A MethodTable stores a TypeDef token and a MethodDesc a MethodDef token; neither stores a
    /// name. These check that the tokens are resolved against the metadata in the mapped image
    /// and give back what reflection would have said - which is the only way to know the walk
    /// through the PE and COR20 headers landed in the right place.
    /// </remarks>
    public class MetadataNameTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static;

        [Test]
        [Arguments(typeof(NamedOuter))]
        [Arguments(typeof(string))]
        [Arguments(typeof(int))]
        [Arguments(typeof(System.Collections.Generic.Dictionary<string, int>))]
        public async Task TypeNamesComeOutOfMetadata(Type type)
        {
            var methodTable = ClrObject.From(type).MethodTable;

            // A generic instantiation's metadata name is its definition's, which is what the
            // TypeDef row holds - so compare against the definition rather than the closed type.
            var expected = type.IsGenericType ? type.GetGenericTypeDefinition() : type;

            await Assert.That(methodTable.MetadataName).IsEqualTo(expected.FullName);
        }

        /// <summary>A nested type's row holds only its short name; the outer type has to be walked to.</summary>
        [Test]
        public async Task NestedTypeNamesIncludeTheirDeclaringType()
        {
            var methodTable = ClrObject.From(typeof(NamedOuter.NamedNested)).MethodTable;

            await Assert.That(methodTable.MetadataName).IsEqualTo(typeof(NamedOuter.NamedNested).FullName);
            await Assert.That(methodTable.MetadataName).Contains("+");
        }

        [Test]
        public async Task NamespacesComeOutOfMetadata()
        {
            await Assert.That(ClrObject.From(typeof(string)).MethodTable.MetadataNamespace)
                .IsEqualTo("System");

            await Assert.That(ClrObject.From(typeof(NamedOuter)).MethodTable.MetadataNamespace)
                .IsEqualTo(typeof(NamedOuter).Namespace);
        }

        /// <summary>
        /// Every method a type declares must name itself the same way reflection does - which
        /// also proves each MethodDesc found its own chunk's MethodTable.
        /// </summary>
        [Test]
        public async Task MethodNamesComeOutOfMetadata()
        {
            var methodTable = ClrObject.From(typeof(NamedOuter)).MethodTable;

            foreach (var method in typeof(NamedOuter).GetMethods(All | BindingFlags.DeclaredOnly))
            {
                var descriptor = methodTable.FindMethod(method);

                await Assert.That(descriptor).IsNotNull();
                await Assert.That(descriptor.Name).IsEqualTo(method.Name);
                await Assert.That(descriptor.DeclaringTypeName).IsEqualTo(typeof(NamedOuter).FullName);
            }
        }

        // ---------- IL from a MethodDesc ----------

        /// <summary>
        /// The bytes read out of the mapped image must be exactly what reflection hands over -
        /// anything else means the RVA, the header decode or the size is wrong.
        /// </summary>
        [Test]
        [Arguments("Add")]
        [Arguments("Check")]
        public async Task IlReadFromMemoryMatchesReflection(string name)
        {
            var method = typeof(NamedOuter).GetMethod(name, All);
            var descriptor = ClrObject.From(typeof(NamedOuter)).MethodTable.FindMethod(method);

            var fromMemory = descriptor.ReadIl();
            var fromReflection = method.GetMethodBody().GetILAsByteArray();

            await Assert.That(fromMemory).IsNotNull();
            await Assert.That(fromMemory.Il).IsEquivalentTo(fromReflection);
        }

        [Test]
        public async Task TheMethodBodyHeaderIsDecoded()
        {
            var tiny = ClrObject.From(typeof(NamedOuter)).MethodTable
                .FindMethod(typeof(NamedOuter).GetMethod(nameof(NamedOuter.Add), All))
                .ReadIl();

            // No locals, no handlers, a shallow stack: the one-byte header.
            await Assert.That(tiny.IsFatFormat).IsFalse();
            await Assert.That(tiny.MaxStack).IsEqualTo(8);

            var fat = ClrObject.From(typeof(NamedOuter)).MethodTable
                .FindMethod(typeof(NamedOuter).GetMethod(nameof(NamedOuter.Check), All))
                .ReadIl();

            // A local forces the twelve-byte header, which carries a real stack depth.
            await Assert.That(fat.IsFatFormat).IsTrue();
            await Assert.That(fat.LocalSignatureToken).IsNotEqualTo(0u);
        }

        /// <summary>
        /// Disassembling from a MethodDesc must produce the same instruction stream as
        /// disassembling through reflection, with the operands named rather than resolved.
        /// </summary>
        [Test]
        public async Task DisassemblyFromAMethodDescMatchesReflection()
        {
            var method = typeof(NamedOuter).GetMethod(nameof(NamedOuter.Check), All);
            var descriptor = ClrObject.From(typeof(NamedOuter)).MethodTable.FindMethod(method);

            var fromMemory = ClrMethodIl.Of(descriptor);
            var fromReflection = ClrMethodIl.Of(method);

            await Assert.That(fromMemory.Bytes).IsEquivalentTo(fromReflection.Bytes);
            await Assert.That(fromMemory.Instructions.Select(i => i.OpCode.Name).ToList())
                .IsEquivalentTo(fromReflection.Instructions.Select(i => i.OpCode.Name).ToList());
        }

        /// <summary>Operands are named from metadata, not resolved to reflection objects.</summary>
        [Test]
        public async Task OperandsAreNamedFromMetadata()
        {
            var method = typeof(NamedOuter).GetMethod(nameof(NamedOuter.Check), All);
            var descriptor = ClrObject.From(typeof(NamedOuter)).MethodTable.FindMethod(method);

            var il = ClrMethodIl.Of(descriptor);

            var tokens = il.Instructions.Select(i => i.Operand).OfType<ClrIlToken>().ToList();

            await Assert.That(tokens).IsNotEmpty();
            await Assert.That(tokens.Any(t => t.Name != null && t.Name.EndsWith("::Stock"))).IsTrue();

            // Nothing was turned into a reflection object on the way.
            await Assert.That(il.Instructions.Any(i => i.Operand is MemberInfo)).IsFalse();

            // A string literal still reads as its text, from the user string heap.
            await Assert.That(il.Instructions.Any(i => (i.Operand as string) == "ok")).IsTrue();

            await Assert.That(il.Dump()).Contains("::Stock");
        }

        [Test]
        public async Task AMethodWithNoBodyReadsAsNoBody()
        {
            var methodTable = ClrObject.From(typeof(Report)).MethodTable;
            var render = methodTable.FindMethod(typeof(Report).GetMethod(nameof(Report.Render), All));

            await Assert.That(render).IsNotNull();

            // Render has a body; its abstract declaration on the base type does not.
            var abstractTable = ClrObject.From(typeof(ReportBase)).MethodTable;
            var abstractMethod = abstractTable.FindMethod(typeof(ReportBase).GetMethod(nameof(ReportBase.Render), All));

            await Assert.That(abstractMethod.ReadIl()).IsNull();
            await Assert.That(ClrMethodIl.Of(abstractMethod)).IsNull();
        }

        [Test]
        public async Task MetadataIsFoundForEveryLoadedModuleWeTouch()
        {
            foreach (var type in new[] { typeof(object), typeof(NamedOuter), typeof(Uri) })
            {
                var metadata = ClrModuleMetadata.Of(ClrModule.Of(type));

                await Assert.That(metadata).IsNotNull();
                await Assert.That(metadata.MetadataSize).IsGreaterThan(0);
                await Assert.That(metadata.Reader.MetadataVersion).StartsWith("v");
            }
        }

        /// <summary>
        /// An emitted method has no mapped image, so the name and IL routes have nothing to read.
        /// They must say so rather than fault.
        /// </summary>
        [Test]
        public async Task AModuleWithNoImageReportsNoMetadata()
        {
            await Assert.That(ClrModuleMetadata.AtImageBase(IntPtr.Zero)).IsNull();
        }
    }
}
