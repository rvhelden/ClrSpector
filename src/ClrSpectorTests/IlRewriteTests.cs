using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClrSpector;
using ClrSpector.Detours;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    public class Calc
    {
        public int Factor = 10;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Scale(int value) => value * this.Factor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Describe(int n) => n > 0 ? "positive" : "other";

        [MethodImpl(MethodImplOptions.NoInlining)]
        public decimal Money(int n) => n * 1.5m;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Money Total(int n) => new Money { Amount = n, Currency = 1 };

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Label(int n) => $"n={n}";

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Sum(int n)
        {
            var total = 0;
            for (var i = 0; i < n; i++)
                total += i % 2 == 0 ? i : -i;

            return total;
        }
    }

    /// <summary>
    /// Replacing a method's body with different IL, and rendering IL in colour.
    /// </summary>
    /// <remarks>
    /// A replacement patches the same process-wide dispatch slots a redirect does, so these are
    /// kept out of parallel runs for the same reason the detour tests are.
    /// </remarks>
    [NotInParallel]
    public class IlRewriteTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static;

        private static readonly Regex AnsiEscape = new Regex("\\[[0-9;]*m");

        // ---------- colouring ----------

        [Test]
        public async Task PlainDumpCarriesNoEscapeSequences()
        {
            var dump = ClrMethodIl.Of(typeof(Calc).GetMethod(nameof(Calc.Describe), All))
                .Dump(IlDumpStyle.Plain);

            await Assert.That(dump).DoesNotContain("");
        }

        /// <summary>
        /// Colour must be decoration only: stripping the escapes has to give back exactly the
        /// plain rendering, or the two dumps have drifted apart.
        /// </summary>
        [Test]
        public async Task ColouredDumpIsThePlainDumpPlusEscapes()
        {
            var method = typeof(Calc).GetMethod(nameof(Calc.Sum), All);
            var il = ClrMethodIl.Of(method);

            var plain = il.Dump(IlDumpStyle.Plain);
            var coloured = il.Dump(IlDumpStyle.Ansi);

            await Assert.That(coloured).Contains("[");
            await Assert.That(AnsiEscape.Replace(coloured, string.Empty)).IsEqualTo(plain);
        }

        /// <summary>Opcodes are coloured by what they do, so control flow stands out from loads.</summary>
        [Test]
        public async Task OpcodesAreColouredByWhatTheyDo()
        {
            await Assert.That(IlPalette.ColourFor(OpCodes.Br_S)).IsEqualTo(IlPalette.ControlFlow);
            await Assert.That(IlPalette.ColourFor(OpCodes.Ret)).IsEqualTo(IlPalette.ControlFlow);
            await Assert.That(IlPalette.ColourFor(OpCodes.Call)).IsEqualTo(IlPalette.Call);
            await Assert.That(IlPalette.ColourFor(OpCodes.Ldstr)).IsEqualTo(IlPalette.Literal);
            await Assert.That(IlPalette.ColourFor(OpCodes.Ldfld)).IsEqualTo(IlPalette.Member);
            await Assert.That(IlPalette.ColourFor(OpCodes.Add)).IsEqualTo(IlPalette.Opcode);
        }

        /// <summary>Auto must not emit escapes into a redirected stream, which is what a test host is.</summary>
        [Test]
        public async Task AutoDoesNotColourRedirectedOutput()
        {
            if (!Console.IsOutputRedirected)
                return;

            var il = ClrMethodIl.Of(typeof(Calc).GetMethod(nameof(Calc.Describe), All));

            await Assert.That(il.Dump(IlDumpStyle.Auto)).IsEqualTo(il.Dump(IlDumpStyle.Plain));
        }

        // ---------- replacing a body ----------

        /// <summary>
        /// The strongest check on the decoder and the emitter together: decode a body, emit it
        /// back unchanged, and the method must still do exactly what it did.
        /// </summary>
        [Test]
        [Arguments("Scale")]
        [Arguments("Describe")]
        [Arguments("Sum")]
        public async Task DecodingAndReEmittingABodyChangesNothing(string name)
        {
            var calc = new Calc();
            var method = typeof(Calc).GetMethod(name, All);
            var before = method.Invoke(calc, new object[] { 7 });

            var il = ClrMethodIl.Of(method);

            using (MethodDetour.ReplaceIl(method, il.Instructions, LocalTypes(il)))
            {
                await Assert.That(method.Invoke(calc, new object[] { 7 })).IsEqualTo(before);
            }

            await Assert.That(method.Invoke(calc, new object[] { 7 })).IsEqualTo(before);
        }

        /// <summary>A round-tripped body still reads the receiver's fields through its own slot 0.</summary>
        [Test]
        public async Task ARoundTrippedBodyStillSeesTheRealReceiver()
        {
            var calc = new Calc { Factor = 3 };
            var method = typeof(Calc).GetMethod(nameof(Calc.Scale), All);
            var il = ClrMethodIl.Of(method);

            using (MethodDetour.ReplaceIl(method, il.Instructions, LocalTypes(il)))
            {
                await Assert.That(calc.Scale(7)).IsEqualTo(21);

                // A different instance must be read, not the one that was around at patch time.
                await Assert.That(new Calc { Factor = 5 }.Scale(7)).IsEqualTo(35);
            }
        }

        [Test]
        public async Task AHandWrittenBodyReplacesTheOriginal()
        {
            var calc = new Calc();
            var method = typeof(Calc).GetMethod(nameof(Calc.Scale), All);

            using (var patch = MethodDetour.ReplaceBody(method, il =>
                   {
                       il.Emit(OpCodes.Ldarg_1);
                       il.Emit(OpCodes.Ldc_I4, 100);
                       il.Emit(OpCodes.Mul);
                       il.Emit(OpCodes.Ret);
                   }))
            {
                await Assert.That(calc.Scale(7)).IsEqualTo(700);
                await Assert.That(patch.IsActive).IsTrue();
                await Assert.That(patch.PatchedTargets).IsEqualTo(DetourTargets.Precode);
            }

            await Assert.That(calc.Scale(7)).IsEqualTo(70);
        }

        /// <summary>
        /// Editing decoded IL is the point of decoding it: swap the field load for a constant.
        /// </summary>
        [Test]
        public async Task AnEditedInstructionListTakesEffect()
        {
            var calc = new Calc();
            var method = typeof(Calc).GetMethod(nameof(Calc.Scale), All);
            var il = ClrMethodIl.Of(method);

            // ldarg.1; ldarg.0; ldfld Factor; mul; ret  ->  ldarg.1; ldc.i4 9; mul; ret
            var edited = il.Instructions
                .Where(i => i.OpCode != OpCodes.Ldarg_0 && i.OpCode != OpCodes.Ldfld)
                .ToList();

            edited.Insert(1, Instruction(OpCodes.Ldc_I4, 9, edited[1].Offset));

            using (MethodDetour.ReplaceIl(method, edited, LocalTypes(il)))
            {
                await Assert.That(calc.Scale(7)).IsEqualTo(63);
            }

            await Assert.That(calc.Scale(7)).IsEqualTo(70);
        }

        /// <summary>
        /// A short branch may not reach once instructions have moved, so branches are re-emitted
        /// in long form and must still land in the right place.
        /// </summary>
        [Test]
        public async Task BranchesSurviveReEmission()
        {
            var calc = new Calc();
            var method = typeof(Calc).GetMethod(nameof(Calc.Describe), All);
            var il = ClrMethodIl.Of(method);

            await Assert.That(il.Instructions.Any(i => i.Operand is ClrIlBranchTarget)).IsTrue();

            using (MethodDetour.ReplaceIl(method, il.Instructions, LocalTypes(il)))
            {
                await Assert.That(calc.Describe(1)).IsEqualTo("positive");
                await Assert.That(calc.Describe(-1)).IsEqualTo("other");
            }
        }

        /// <summary>
        /// A return value too large for a register travels through a hidden buffer passed after
        /// the receiver. The replacement is emitted as an instance method so that buffer keeps
        /// its place - get this wrong and the return value lands in the object instead.
        /// </summary>
        [Test]
        public async Task AStructReturnSurvivesAReplacedBody()
        {
            var calc = new Calc { Factor = 10 };
            var method = typeof(Calc).GetMethod(nameof(Calc.Money), All);

            using (MethodDetour.ReplaceBody(method, il =>
                   {
                       il.Emit(OpCodes.Ldc_I4, 42);
                       il.Emit(OpCodes.Newobj, typeof(decimal).GetConstructor(new[] { typeof(int) }));
                       il.Emit(OpCodes.Ret);
                   }))
            {
                await Assert.That(calc.Money(2)).IsEqualTo(42m);

                // The giveaway for a displaced return buffer is the receiver being written over.
                await Assert.That(calc.Factor).IsEqualTo(10);
            }

            await Assert.That(calc.Money(2)).IsEqualTo(3.0m);
        }

        [Test]
        public async Task AStaticMethodsBodyCanBeReplaced()
        {
            var method = typeof(Calc).GetMethod(nameof(Calc.Label), All);

            using (MethodDetour.ReplaceBody(method, il =>
                   {
                       il.Emit(OpCodes.Ldstr, "patched");
                       il.Emit(OpCodes.Ret);
                   }))
            {
                await Assert.That(Calc.Label(3)).IsEqualTo("patched");
            }

            await Assert.That(Calc.Label(3)).IsEqualTo("n=3");
        }

        // ---------- refusals ----------

        [Test]
        public async Task RefusesAnEmptyBody()
        {
            var method = typeof(Calc).GetMethod(nameof(Calc.Scale), All);

            await Assert.That(() => MethodDetour.ReplaceIl(method, new List<ClrIlInstruction>()))
                .Throws<MethodDetourException>();
        }

        /// <summary>
        /// A token the decoder could not resolve reads back as a bare integer, which means
        /// nothing in the new module - emitting it would reference whatever member happened to
        /// share that number.
        /// </summary>
        [Test]
        public async Task RefusesAnUnresolvedTokenOperand()
        {
            var method = typeof(Calc).GetMethod(nameof(Calc.Scale), All);

            var body = new List<ClrIlInstruction>
            {
                Instruction(OpCodes.Call, 0x06000001, 0),
                Instruction(OpCodes.Ret, null, 5)
            };

            await Assert.That(() => MethodDetour.ReplaceIl(method, body)).Throws<MethodDetourException>();
        }

        [Test]
        public async Task RefusesAnAbstractTarget()
        {
            var method = typeof(ReportBase).GetMethod(nameof(ReportBase.Render), All);

            await Assert.That(() => MethodDetour.ReplaceBody(method, il => il.Emit(OpCodes.Ret)))
                .Throws<MethodDetourException>();
        }

        [Test]
        public async Task RefusesABranchToNowhere()
        {
            var method = typeof(Calc).GetMethod(nameof(Calc.Scale), All);

            var body = new List<ClrIlInstruction>
            {
                Instruction(OpCodes.Br, new ClrIlBranchTarget(0x1234), 0),
                Instruction(OpCodes.Ret, null, 5)
            };

            await Assert.That(() => MethodDetour.ReplaceIl(method, body)).Throws<MethodDetourException>();
        }

        /// <summary>A failed replacement must leave the target running its own code.</summary>
        [Test]
        public async Task TheTargetStillWorksAfterARefusedReplacement()
        {
            var calc = new Calc();
            var method = typeof(Calc).GetMethod(nameof(Calc.Scale), All);

            try
            {
                MethodDetour.ReplaceIl(method, new List<ClrIlInstruction>());
            }
            catch (MethodDetourException)
            {
            }

            await Assert.That(calc.Scale(7)).IsEqualTo(70);
        }

        private static IReadOnlyList<Type> LocalTypes(ClrMethodIl il)
        {
            return il.Locals.Select(l => l.LocalType).ToList();
        }

        private static ClrIlInstruction Instruction(OpCode opCode, object operand, int offset)
        {
            return ClrIlInstruction.Create(opCode, operand, offset);
        }
    }
}
