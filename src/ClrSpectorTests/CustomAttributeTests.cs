using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ClrSpector;

namespace ClrSpectorTests
{
    /// <summary>An attribute reaching every argument shape ECMA-335 II.23.3 can encode.</summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class SubjectAttribute : Attribute
    {
        public SubjectAttribute()
        {
        }

        public SubjectAttribute(string text) => this.Text = text;

        public SubjectAttribute(
            bool a, char b, sbyte c, byte d, short e, ushort f,
            int g, uint h, long i, ulong j, float k, double l)
        {
            this.Text = $"{a}{b}{c}{d}{e}{f}{g}{h}{i}{j}{k}{l}";
        }

        public SubjectAttribute(Type type) => this.Text = type?.Name;

        public SubjectAttribute(object boxed) => this.Text = boxed?.ToString();

        public SubjectAttribute(int[] numbers) => this.Text = numbers?.Length.ToString();

        public SubjectAttribute(SmallKind small, WideKind wide) => this.Text = $"{small}{wide}";

        public string Text;
        public char Letter;
        public double Ratio;

        public int Number { get; set; }

        public SmallKind Small { get; set; }

        public WideKind Wide { get; set; }

        public Ways Combination { get; set; }

        public Type Which { get; set; }

        public object Anything { get; set; }

        public string[] Names { get; set; }
    }

    /// <summary>A byte-backed enum: an int-width assumption would misread it.</summary>
    public enum SmallKind : byte
    {
        None = 0,
        Big = 250
    }

    /// <summary>A long-backed enum, for the same reason in the other direction.</summary>
    public enum WideKind : long
    {
        None = 0,
        Huge = 8_000_000_000L
    }

    [Flags]
    public enum Ways
    {
        None = 0,
        Up = 1,
        Down = 2,
        Sideways = 4
    }

    [Subject("on the type")]
    [Subject(true, 'z', -1, 200, -300, 40000, -5, 6u, -7L, 8UL, 1.5f, 2.25)]
    [Subject(typeof(Dictionary<string, int>))]
    [Subject(new[] { 1, 2, 3 })]
    [Subject((object)42)]
    [Subject(SmallKind.Big, WideKind.Huge)]
    [Subject(Number = -7, Small = SmallKind.Big, Wide = WideKind.Huge, Letter = 'q', Ratio = 0.5)]
    [Subject(Combination = Ways.Up | Ways.Sideways)]
    [Subject(Which = typeof(int[]), Names = new[] { "x", "y" }, Anything = "boxed")]
    [Subject((string)null)]
    [Subject(new int[0])]
    [Subject((int[])null)]
    public sealed class AttributeSubject
    {
        [Subject("on a field")]
        [Subject(Small = SmallKind.Big)]
        public int Field;

        [Subject("on a method")]
        public void Method()
        {
        }

        /// <summary>
        /// Carries both a real attribute and a pseudo-custom one, so the two paths can be told
        /// apart on the same method.
        /// </summary>
        [Subject("both kinds")]
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void Constrained()
        {
        }
    }

    /// <summary>
    /// Reads custom attributes out of metadata rather than by instantiating them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An attribute is a CustomAttribute row - who it was applied to, which constructor was
    /// named, and a blob of arguments. Reflection's <c>GetCustomAttributes</c> reads that row and
    /// then <i>constructs the attribute</i>, running its constructor in this process. Reading the
    /// row directly gives the same answer without running anything, so it works for an attribute
    /// whose type will not load.
    /// </para>
    /// <para>
    /// <c>GetCustomAttributesData</c> is the oracle throughout, because it reports the same
    /// as-written view rather than a constructed instance. Its one systematic difference is
    /// pseudo-custom attributes - <c>[Serializable]</c>, <c>[StructLayout]</c>,
    /// <c>[DllImport]</c>, <c>[MethodImpl]</c> and the rest of ECMA-335 II.21 - which are stored
    /// as bits in the defining table rather than as rows, and which reflection synthesises. Those
    /// are excluded from the row-to-row comparison rather than faked.
    /// </para>
    /// <para>
    /// One of them is recoverable: <c>[MethodImpl]</c> lives in <c>MethodDef.ImplFlags</c>, and
    /// <see cref="ClrMethodDescription.PseudoCustomAttributes"/> reconstructs it from there -
    /// kept apart from the rows so a reconstruction is never mistaken for a reading. Reflection
    /// does not report that one as an attribute either; it exposes the flags through
    /// <see cref="MethodBase.GetMethodImplementationFlags"/>, which is what
    /// <see cref="TheImplementationFlagsMatchTheOnesReflectionReports"/> checks against.
    /// <see cref="TheRemainingPseudoCustomAttributesAreTheKnownDifferenceFromReflection"/> is the
    /// rest of the gap.
    /// </para>
    /// </remarks>
    public class CustomAttributeTests
    {
        /// <summary>
        /// ECMA-335 II.21: attributes the compiler turns into table bits instead of rows.
        /// </summary>
        private static readonly HashSet<string> Pseudo = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.SerializableAttribute",
            "System.NonSerializedAttribute",
            "System.Runtime.InteropServices.ComImportAttribute",
            "System.Runtime.InteropServices.DllImportAttribute",
            "System.Runtime.InteropServices.StructLayoutAttribute",
            "System.Runtime.InteropServices.FieldOffsetAttribute",
            "System.Runtime.InteropServices.MarshalAsAttribute",
            "System.Runtime.InteropServices.InAttribute",
            "System.Runtime.InteropServices.OutAttribute",
            "System.Runtime.InteropServices.OptionalAttribute",
            "System.Runtime.InteropServices.PreserveSigAttribute",
            "System.Runtime.InteropServices.DefaultParameterValueAttribute",
            "System.Runtime.CompilerServices.MethodImplAttribute",
            "System.Runtime.CompilerServices.SpecialNameAttribute",
            "System.Reflection.AssemblyAlgorithmIdAttribute",
            "System.Reflection.AssemblyFlagsAttribute"
        };

        [Test]
        public async Task ATypeReportsTheAttributesAppliedToIt()
        {
            var table = ClrObject.From<AttributeSubject>().MethodTable;
            var attributes = table.CustomAttributes;

            await Assert.That(attributes.Count).IsEqualTo(12);
            await Assert.That(attributes.All(a => a.IsDecoded)).IsTrue();

            await Assert.That(attributes.All(a =>
                    a.TypeName == "ClrSpectorTests.SubjectAttribute"))
                .IsTrue();

            // The row names a constructor, and its signature is what typed the arguments.
            await Assert.That(attributes.All(a => a.Constructor != null)).IsTrue();
        }

        [Test]
        public async Task AMethodAndAFieldReportTheirOwnAttributes()
        {
            var table = ClrObject.From<AttributeSubject>().MethodTable;
            var method = table.FindMethod("Method");

            await Assert.That(method).IsNotNull();

            var onMethod = method.CustomAttributes;

            await Assert.That(onMethod.Count).IsEqualTo(1);
            await Assert.That(onMethod[0].ConstructorArguments[0].Value).IsEqualTo("on a method");

            // A FieldDesc carries its own FieldDef token, so it needs nothing from the type.
            var field = table.Fields.FirstOrDefault(f => f.Name == "Field");

            await Assert.That(field).IsNotNull();

            var onField = field.CustomAttributes;

            await Assert.That(onField.Count).IsEqualTo(2);
            await Assert.That(onField.Any(a =>
                    a.ConstructorArguments.Count == 1
                    && Equals(a.ConstructorArguments[0].Value, "on a field")))
                .IsTrue();
        }

        /// <summary>
        /// Every primitive width the encoding allows, read back exactly.
        /// </summary>
        /// <remarks>
        /// These are stored in their natural width, little-endian and unaligned, with no type tag
        /// of their own - so reading one at the wrong width returns a wrong value <i>and</i>
        /// leaves the cursor in the wrong place for everything after it. Twelve in a row is the
        /// case that would expose that.
        /// </remarks>
        [Test]
        public async Task EveryPrimitiveWidthDecodesToTheValueWritten()
        {
            var attribute = Find(a => a.ConstructorArguments.Count == 12);

            var values = attribute.ConstructorArguments.Select(a => a.Value).ToList();

            // Compared as boxed objects so the type each value came back as counts too: an int
            // read where a short was written compares unequal rather than quietly matching.
            var written = new object[]
            {
                true, 'z', (sbyte)-1, (byte)200, (short)-300, (ushort)40000,
                -5, 6u, -7L, 8UL, 1.5f, 2.25
            };

            await Assert.That(values).IsEquivalentTo(written);
        }

        /// <summary>
        /// An enum argument is a bare number whose width comes from the enum's own definition.
        /// </summary>
        /// <remarks>
        /// The blob does not say how wide an enum value is. Assuming <c>int</c> reads
        /// <see cref="SmallKind"/> three bytes too far and <see cref="WideKind"/> four bytes too
        /// short, so both are here: the byte-backed one is the case an int assumption gets
        /// wrong, and the long-backed one is the case it gets wrong in the other direction.
        /// </remarks>
        [Test]
        public async Task AnEnumIsReadAtTheWidthOfItsUnderlyingType()
        {
            var attribute = Find(a =>
                a.ConstructorArguments.Count == 2
                && a.ConstructorArguments[0].Type.IsEnum);

            var small = attribute.ConstructorArguments[0];
            var wide = attribute.ConstructorArguments[1];

            await Assert.That(small.Type.IsEnum).IsTrue();
            await Assert.That(small.Type.UnderlyingResolved).IsTrue();
            await Assert.That(small.Type.Underlying).IsEqualTo(CorElementType.U1);
            await Assert.That(small.Value).IsEqualTo((object)(byte)250);

            await Assert.That(wide.Type.UnderlyingResolved).IsTrue();
            await Assert.That(wide.Type.Underlying).IsEqualTo(CorElementType.I8);
            await Assert.That(wide.Value).IsEqualTo((object)8_000_000_000L);
        }

        /// <summary>
        /// A named enum argument's underlying type has to be resolved the same way, from a name.
        /// </summary>
        /// <remarks>
        /// A named argument's blob spells out the enum as a reflection-style name rather than as a
        /// table reference, so resolving it means matching that name against a TypeDef - which is
        /// a different path from the positional case above and fails differently.
        /// </remarks>
        [Test]
        public async Task ANamedEnumArgumentResolvesItsUnderlyingTypeToo()
        {
            var attribute = Find(a => a.NamedArguments.Any(n => n.Name == "Small"));

            var small = attribute.NamedArguments.First(n => n.Name == "Small");
            var wide = attribute.NamedArguments.First(n => n.Name == "Wide");

            await Assert.That(small.Type.Underlying).IsEqualTo(CorElementType.U1);
            await Assert.That(small.Value).IsEqualTo((object)(byte)250);

            await Assert.That(wide.Type.Underlying).IsEqualTo(CorElementType.I8);
            await Assert.That(wide.Value).IsEqualTo((object)8_000_000_000L);

            // A number is not what the source wrote; the member name is.
            await Assert.That(small.EnumMemberName).IsEqualTo("ClrSpectorTests.SmallKind.Big");
        }

        /// <summary>
        /// An enum member's name is recovered from the enum's literal fields, flags included.
        /// </summary>
        [Test]
        public async Task AnEnumValueRecoversTheMemberNameItWasWrittenAs()
        {
            var attribute = Find(a => a.NamedArguments.Any(n => n.Name == "Combination"));
            var combination = attribute.NamedArguments.First(n => n.Name == "Combination");

            await Assert.That(combination.Value).IsEqualTo((object)5);

            // Up | Sideways is 5, which no single member names.
            await Assert.That(combination.EnumMemberName)
                .IsEqualTo("ClrSpectorTests.Ways.Up | ClrSpectorTests.Ways.Sideways");
        }

        /// <summary>
        /// A named argument records whether it set a field or a property, which C# does not show.
        /// </summary>
        [Test]
        public async Task ANamedArgumentSaysWhetherItSetAFieldOrAProperty()
        {
            var attribute = Find(a => a.NamedArguments.Any(n => n.Name == "Letter"));

            var letter = attribute.NamedArguments.First(n => n.Name == "Letter");
            var number = attribute.NamedArguments.First(n => n.Name == "Number");

            await Assert.That(letter.Kind).IsEqualTo(ClrAttributeArgumentKind.Field);
            await Assert.That(number.Kind).IsEqualTo(ClrAttributeArgumentKind.Property);
        }

        /// <summary>
        /// An empty array and a null array are stored differently and must stay distinguishable.
        /// </summary>
        /// <remarks>
        /// A null array is the element count <c>0xFFFFFFFF</c>; an empty one is the count zero.
        /// Collapsing the two loses what the source said.
        /// </remarks>
        [Test]
        public async Task AnArrayArgumentKeepsItsElementsAndItsNullness()
        {
            var populated = Find(a =>
                a.ConstructorArguments.Count == 1
                && a.ConstructorArguments[0].Elements?.Count == 3);

            var elements = populated.ConstructorArguments[0].Elements;

            await Assert.That(elements.Select(e => e.Value).ToList())
                .IsEquivalentTo(new object[] { 1, 2, 3 });

            var empty = Find(a =>
                a.ConstructorArguments.Count == 1
                && a.ConstructorArguments[0].Elements?.Count == 0);

            await Assert.That(empty.ConstructorArguments[0].IsNull).IsFalse();

            var missing = Find(a =>
                a.ConstructorArguments.Count == 1
                && a.ConstructorArguments[0].Type.IsArray
                && a.ConstructorArguments[0].IsNull);

            await Assert.That(missing.ConstructorArguments[0].Elements).IsNull();
        }

        /// <summary>
        /// A typeof() argument is a name, and the name has to be the one that resolves.
        /// </summary>
        /// <remarks>
        /// The blob holds only a string, so this reports the string. Resolving it is the check
        /// that it is the right string - reflection's own view of the same argument is a Type,
        /// and the two have to agree.
        /// </remarks>
        [Test]
        public async Task ATypeofArgumentIsTheNameTheCompilerWrote()
        {
            var attribute = Find(a =>
                a.ConstructorArguments.Count == 1
                && a.ConstructorArguments[0].Type.ElementType == CorElementType.CLASS
                && !a.ConstructorArguments[0].IsNull);

            var name = (string)attribute.ConstructorArguments[0].Value;

            await Assert.That(Type.GetType(name, throwOnError: false))
                .IsEqualTo(typeof(Dictionary<string, int>));

            // Rendered as source would write it rather than as a bare string.
            await Assert.That(attribute.ConstructorArguments[0].Literal())
                .StartsWith("typeof(");
        }

        /// <summary>
        /// A boxed argument carries its own type inline, because the signature only says object.
        /// </summary>
        [Test]
        public async Task ABoxedArgumentCarriesItsOwnType()
        {
            var attribute = Find(a =>
                a.ConstructorArguments.Count == 1
                && Equals(a.ConstructorArguments[0].Value, 42));

            var boxed = attribute.ConstructorArguments[0];

            await Assert.That(boxed.Type.ElementType).IsEqualTo(CorElementType.I4);
            await Assert.That(boxed.Value).IsEqualTo((object)42);
        }

        /// <summary>
        /// The whole set for one type has to match reflection's, argument values included.
        /// </summary>
        /// <remarks>
        /// The per-argument tests above each pin one encoding; this is the one that would catch a
        /// cursor left in the wrong place, since a single misread width shifts everything after
        /// it and the rendered set stops matching.
        /// </remarks>
        [Test]
        [Arguments(typeof(AttributeSubject))]
        [Arguments(typeof(SubjectAttribute))]
        [Arguments(typeof(CustomAttributeTests))]
        public async Task ATypesAttributesMatchWhatReflectionReportsForIt(Type subject)
        {
            var metadata = ClrModuleMetadata.Of(ClrModule.Of(subject));

            var ours = metadata.CustomAttributes(subject.MetadataToken)
                .Select(Render)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            var theirs = subject.GetCustomAttributesData()
                .Where(d => !Pseudo.Contains(d.AttributeType.FullName))
                .Select(Render)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            await Assert.That(ours).IsEquivalentTo(theirs);
        }

        /// <summary>
        /// Assembly-level attributes hang off the Assembly row, not off any type.
        /// </summary>
        [Test]
        public async Task AnAssemblyReportsItsOwnAttributes()
        {
            var assembly = ClrAssembly.Of(typeof(object));
            var ours = assembly.CustomAttributes;

            await Assert.That(ours.Count).IsGreaterThan(0);
            await Assert.That(ours.All(a => a.IsDecoded)).IsTrue();

            var theirs = typeof(object).Assembly.GetCustomAttributesData()
                .Where(d => !Pseudo.Contains(d.AttributeType.FullName))
                .Select(Render)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            await Assert.That(ours.Select(Render).OrderBy(x => x, StringComparer.Ordinal).ToList())
                .IsEquivalentTo(theirs);
        }

        /// <summary>
        /// Every enum argument in every loaded assembly is read at the width its enum really has.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The single most dangerous thing a custom-attribute decoder can get wrong. The blob
        /// stores an enum as a bare number and never says how wide it is, so the width has to come
        /// from the enum's own definition - and a decoder that assumed <c>int</c> would read most
        /// of them correctly, which is exactly why the mistake survives casual testing.
        /// </para>
        /// <para>
        /// Reflection is the oracle: <c>Enum.GetUnderlyingType</c> on the real type says what the
        /// width is, and every enum argument found is checked against it. Both halves matter -
        /// <see cref="ClrAttributeArgumentType.UnderlyingResolved"/> being true everywhere says no
        /// width was guessed, and the width comparison says the ones that were read were read
        /// right. A run that resolved nothing and guessed <c>int</c> throughout would pass the
        /// second check and fail the first.
        /// </para>
        /// <para>
        /// Deliberately swept across several assemblies rather than one, because resolving a
        /// cross-assembly enum is a different code path from resolving a local one and only the
        /// cross-assembly path had the bugs: the reference map holds a Module where an Assembly
        /// was assumed, a facade assembly answers with a type forwarder rather than a definition,
        /// and a nested enum's reference carries only its short name.
        /// </para>
        /// </remarks>
        [Test]
        public async Task EveryEnumArgumentIsReadAtItsRealWidth()
        {
            var anchors = new[]
            {
                typeof(object), typeof(CustomAttributeTests), typeof(Uri), typeof(Enumerable),
                typeof(ClrObject), typeof(List<>)
            };

            var guessed = new List<string>();
            var wrong = new List<string>();
            var compared = 0;
            var widths = new HashSet<CorElementType>();

            foreach (var anchor in anchors)
            {
                var metadata = ClrModuleMetadata.Of(ClrModule.Of(anchor));

                if (metadata == null)
                    continue;

                foreach (var attribute in metadata.AllCustomAttributes)
                {
                    var arguments = attribute.ConstructorArguments.Concat(attribute.NamedArguments);

                    foreach (var argument in arguments)
                    {
                        if (argument.Type?.IsEnum != true)
                            continue;

                        if (!argument.Type.UnderlyingResolved)
                        {
                            guessed.Add($"{attribute.TypeName} -> {argument.Type.TypeName}");

                            continue;
                        }

                        var real = UnderlyingOf(argument.Type.TypeName);

                        if (real == CorElementType.END)
                            continue;

                        compared++;
                        widths.Add(real);

                        if (real != argument.Type.Underlying)
                        {
                            wrong.Add($"{argument.Type.TypeName}: " +
                                      $"read as {argument.Type.Underlying}, really {real}");
                        }
                    }
                }
            }

            await Assert.That(compared).IsGreaterThan(1000);

            await Assert.That(guessed.Distinct().ToList())
                .IsEmpty()
                .Because("an unresolved enum means the width was guessed rather than read");

            await Assert.That(wrong.Distinct().ToList()).IsEmpty();

            // If every enum in the sweep were int-backed, the check above would pass without
            // saying anything about the widths that are actually hard.
            await Assert.That(widths).Contains(CorElementType.U1);
            await Assert.That(widths).Contains(CorElementType.I8);
        }

        /// <summary>
        /// A cross-assembly enum resolves through a facade's type forwarder, not by luck.
        /// </summary>
        /// <remarks>
        /// <c>AttributeTargets</c> is referenced from this assembly through
        /// <c>System.Runtime</c>, which defines almost nothing: its manifest forwards the name on
        /// to CoreLib. A resolver that stopped when the reference assembly held no matching
        /// TypeDef would find nothing here.
        /// </remarks>
        [Test]
        public async Task ACrossAssemblyEnumResolvesThroughItsForwarder()
        {
            var metadata = ClrModuleMetadata.Of(ClrModule.Of(typeof(CustomAttributeTests)));

            await Assert.That(metadata.TryFindType("System.AttributeTargets", out var found))
                .IsTrue();

            await Assert.That(found.Table)
                .IsEqualTo(MetadataTable.TypeRef)
                .Because("this assembly references it rather than defining it");

            await Assert.That(metadata.EnumUnderlyingType(found.Table, found.RowId))
                .IsEqualTo(CorElementType.I4);
        }

        /// <summary>
        /// CoreLib is reachable from the contract descriptor alone, which is what makes the last
        /// resort of enum resolution independent of what the process has already loaded.
        /// </summary>
        /// <remarks>
        /// Every other route to another assembly goes through a map the runtime fills in lazily,
        /// so it resolves or does not depending on execution history. This one goes through the
        /// published MethodTable of <c>System.Object</c>, which is always there.
        /// </remarks>
        [Test]
        public async Task CoreLibIsReachableWithoutTheLoadersReferenceMaps()
        {
            var coreLib = ClrModuleMetadata.CoreLib;

            await Assert.That(coreLib).IsNotNull();

            await Assert.That(coreLib.ImageBase)
                .IsEqualTo(ClrModule.Of(typeof(object)).Base);

            // And it can answer the question the fallback asks of it.
            var token = coreLib.FindTypeDef("System.AttributeTargets");

            await Assert.That(token).IsNotEqualTo(0u);

            await Assert.That(coreLib.EnumUnderlyingType(MetadataTable.TypeDef, token & 0x00FFFFFF))
                .IsEqualTo(CorElementType.I4);
        }

        /// <summary>
        /// Every attribute in CoreLib decodes, which is the breadth this cannot be argued into.
        /// </summary>
        /// <remarks>
        /// Nothing here compares against reflection - it asserts only that no blob defeated the
        /// decoder. Because a wrong width leaves the cursor misplaced and the leftover bytes are
        /// reported as an error, "everything decoded" is a real statement about tens of thousands
        /// of blobs and not just about the ones this file writes.
        /// </remarks>
        [Test]
        public async Task EveryAttributeInCoreLibDecodes()
        {
            var metadata = ClrModuleMetadata.Of(ClrModule.Of(typeof(object)));

            var total = 0;
            var failures = new List<string>();

            foreach (var attribute in metadata.AllCustomAttributes)
            {
                total++;

                if (!attribute.IsDecoded && failures.Count < 10)
                    failures.Add($"{attribute.TypeName}: {attribute.DecodeError}");
            }

            await Assert.That(total).IsGreaterThan(10_000);
            await Assert.That(failures).IsEmpty();
        }

        /// <summary>
        /// <c>[MethodImpl]</c> has no row, and is recovered from the MethodDef row's own flags.
        /// </summary>
        /// <remarks>
        /// The compiler folds it into <c>ImplFlags</c> and writes nothing to the CustomAttribute
        /// table, so reading rows alone cannot see it however carefully it looks. Both flags are
        /// set here because the value is a bitmask: recovering one and dropping the other would
        /// still look plausible.
        /// </remarks>
        [Test]
        public async Task MethodImplIsRecoveredFromTheRowsImplementationFlags()
        {
            var table = ClrObject.From<AttributeSubject>().MethodTable;
            var method = table.FindMethod("Constrained");

            await Assert.That(method).IsNotNull();

            // NoInlining is 0x0008 and NoOptimization 0x0040.
            await Assert.That(method.ImplementationFlags & 0x0048).IsEqualTo(0x0048);

            // The row carries only the real attribute.
            await Assert.That(method.CustomAttributes.Count).IsEqualTo(1);
            await Assert.That(method.CustomAttributes[0].TypeName)
                .IsEqualTo("ClrSpectorTests.SubjectAttribute");

            var pseudo = method.PseudoCustomAttributes;

            await Assert.That(pseudo.Count).IsEqualTo(1);
            await Assert.That(pseudo[0].TypeName)
                .IsEqualTo("System.Runtime.CompilerServices.MethodImplAttribute");

            await Assert.That(pseudo[0].Arguments[0])
                .IsEqualTo("MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization");

            // A reconstruction says so, so it is never taken for something that was read.
            await Assert.That(pseudo[0].IsSynthesised).IsTrue();
            await Assert.That(pseudo[0].Token).IsEqualTo(0u);
            await Assert.That(method.CustomAttributes[0].IsSynthesised).IsFalse();

            // The combined view is the reflection-equivalent one.
            await Assert.That(method.AllAttributes.Count).IsEqualTo(2);
        }

        /// <summary>
        /// A method with no implementation flags reconstructs nothing, rather than something empty.
        /// </summary>
        [Test]
        public async Task AMethodWithNoImplementationFlagsReconstructsNothing()
        {
            var method = ClrObject.From<AttributeSubject>().MethodTable.FindMethod("Method");

            await Assert.That(method.ImplementationFlags).IsEqualTo((ushort)0);
            await Assert.That(method.PseudoCustomAttributes).IsEmpty();
            await Assert.That(method.AllAttributes.Count).IsEqualTo(method.CustomAttributes.Count);
        }

        /// <summary>
        /// The implementation flags read from the row are the ones reflection reports.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reflection does not surface <c>[MethodImpl]</c> through attribute data at all - it is
        /// absent from <c>GetCustomAttributesData</c> - and exposes the flags directly through
        /// <see cref="MethodBase.GetMethodImplementationFlags"/> instead. So that is the oracle
        /// here, and the reconstruction is a rendering of those same flags rather than a claim of
        /// parity with anything reflection calls an attribute.
        /// </para>
        /// <para>
        /// Which means the combined view is not "what reflection reports": for a method it is
        /// slightly more, because the rendered <c>[MethodImpl]</c> has no counterpart in the
        /// attribute data at all.
        /// </para>
        /// </remarks>
        [Test]
        [Arguments("Constrained")]
        [Arguments("Method")]
        public async Task TheImplementationFlagsMatchTheOnesReflectionReports(string name)
        {
            var method = ClrObject.From<AttributeSubject>().MethodTable.FindMethod(name);
            var oracle = typeof(AttributeSubject).GetMethod(name).GetMethodImplementationFlags();

            await Assert.That(method.ImplementationFlags).IsEqualTo((ushort)oracle);

            // Rows and reconstructions are disjoint, and together they are the combined view.
            await Assert.That(method.AllAttributes.Count)
                .IsEqualTo(method.CustomAttributes.Count + method.PseudoCustomAttributes.Count);

            await Assert.That(method.AllAttributes.Count(a => a.IsSynthesised))
                .IsEqualTo(method.PseudoCustomAttributes.Count);
        }

        /// <summary>
        /// The pseudo-custom attributes that stay out of reach, and why.
        /// </summary>
        /// <remarks>
        /// <c>[Serializable]</c> is a bit in TypeDef.Flags, not a CustomAttribute row, so there is
        /// nothing in the table to find and nothing reconstructs it - unlike
        /// <c>[MethodImpl]</c>, which <see cref="MethodImplIsRecoveredFromTheRowsImplementationFlags"/>
        /// does recover. Asserted rather than left as a comment, so that if a future change starts
        /// synthesising these the claim in the class remarks stops being true out loud.
        /// </remarks>
        [Test]
        public async Task TheRemainingPseudoCustomAttributesAreTheKnownDifferenceFromReflection()
        {
            var reflected = typeof(Guid).GetCustomAttributesData();

            await Assert.That(reflected.Any(d =>
                    d.AttributeType.FullName == "System.SerializableAttribute"))
                .IsTrue()
                .Because("reflection synthesises it from the type's flags");

            var ours = ClrObject.From<Guid>().MethodTable.CustomAttributes;

            await Assert.That(ours.Any(a => a.TypeName == "System.SerializableAttribute"))
                .IsFalse()
                .Because("no CustomAttribute row exists for it to be read from");
        }

        /// <summary>
        /// A method with no metadata row has no attributes, rather than throwing looking for them.
        /// </summary>
        [Test]
        public async Task AMethodWithoutAMetadataRowReportsNoAttributes()
        {
            foreach (var method in ClrObject.From<int[]>().MethodTable.Methods)
                await Assert.That(method.CustomAttributes).IsEmpty();
        }

        /// <summary>
        /// The width an enum named by an attribute argument really has, via reflection.
        /// </summary>
        /// <remarks>
        /// The name may be assembly-qualified, since a named argument spells it out that way.
        /// <see cref="CorElementType.END"/> means the type could not be resolved, which makes it
        /// no evidence either way rather than a failure.
        /// </remarks>
        private static CorElementType UnderlyingOf(string typeName)
        {
            if (typeName == null)
                return CorElementType.END;

            var comma = typeName.IndexOf(',');
            var bare = comma > 0 ? typeName.Substring(0, comma) : typeName;
            var type = Type.GetType(bare, throwOnError: false);

            if (type == null || !type.IsEnum)
                return CorElementType.END;

            switch (Type.GetTypeCode(Enum.GetUnderlyingType(type)))
            {
                case TypeCode.SByte: return CorElementType.I1;
                case TypeCode.Byte: return CorElementType.U1;
                case TypeCode.Int16: return CorElementType.I2;
                case TypeCode.UInt16: return CorElementType.U2;
                case TypeCode.Int32: return CorElementType.I4;
                case TypeCode.UInt32: return CorElementType.U4;
                case TypeCode.Int64: return CorElementType.I8;
                case TypeCode.UInt64: return CorElementType.U8;
                default: return CorElementType.END;
            }
        }

        /// <summary>The attribute carrying <paramref name="predicate"/> on the subject type.</summary>
        private static ClrCustomAttribute Find(Func<ClrCustomAttribute, bool> predicate)
        {
            var attributes = ClrObject.From<AttributeSubject>().MethodTable.CustomAttributes;
            var found = attributes.FirstOrDefault(predicate);

            if (found == null)
                throw new InvalidOperationException(
                    "No attribute on AttributeSubject matches, so the test cannot say anything.");

            return found;
        }

        /// <summary>A shape both this and reflection can be rendered into, for comparison.</summary>
        /// <remarks>
        /// A <c>typeof</c> argument is rendered as a placeholder: this reports the name the
        /// compiler wrote, which references the assemblies it compiled against, while reflection
        /// resolves the type and re-renders it under the runtime's own assembly identities. The
        /// two strings differ for the same argument, so
        /// <see cref="ATypeofArgumentIsTheNameTheCompilerWrote"/> compares them by resolving
        /// instead.
        /// </remarks>
        private static string Render(ClrCustomAttribute attribute)
        {
            var positional = attribute.ConstructorArguments.Select(Render);
            var named = attribute.NamedArguments
                .Select(a => $"{a.Name}={Render(a)}")
                .OrderBy(x => x, StringComparer.Ordinal);

            return $"{attribute.TypeName}({string.Join(",", positional)})" +
                   $"{{{string.Join(",", named)}}}";
        }

        private static string Render(ClrAttributeArgument argument)
        {
            if (argument.Elements != null)
                return $"[{string.Join(";", argument.Elements.Select(Render))}]";

            if (argument.Type?.ElementType == CorElementType.CLASS)
                return argument.IsNull ? "null" : "<type>";

            return Scalar(argument.Value);
        }

        private static string Render(CustomAttributeData data)
        {
            var positional = data.ConstructorArguments.Select(Render);
            var named = data.NamedArguments
                .Select(a => $"{a.MemberName}={Render(a.TypedValue)}")
                .OrderBy(x => x, StringComparer.Ordinal);

            return $"{data.AttributeType.FullName}({string.Join(",", positional)})" +
                   $"{{{string.Join(",", named)}}}";
        }

        private static string Render(CustomAttributeTypedArgument argument)
        {
            if (argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> elements)
                return $"[{string.Join(";", elements.Select(Render))}]";

            if (argument.ArgumentType == typeof(Type))
                return argument.Value == null ? "null" : "<type>";

            return Scalar(argument.Value);
        }

        private static string Scalar(object value)
        {
            switch (value)
            {
                case null:
                    return "null";

                case string text:
                    return $"\"{text}\"";

                case bool flag:
                    return flag ? "true" : "false";

                // An enum on reflection's side is an enum; here it is the underlying number.
                case Enum boxed:
                    return Convert.ToString(
                        Convert.ChangeType(boxed, Enum.GetUnderlyingType(boxed.GetType())),
                        System.Globalization.CultureInfo.InvariantCulture);

                case float number:
                    return number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

                case double number:
                    return number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

                default:
                    return Convert.ToString(
                        value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }
}
