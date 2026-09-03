using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ClrSpector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    /// <summary>
    /// The shapes a C# compiler turns into jumps and temporaries, compiled here in Debug so the
    /// scaffolding is all present - which is the state the projection has to undo.
    /// </summary>
    public class StructuringSample
    {
        public int Quantity = 3;

        /// <summary>A loop, a conditional expression, a compound assignment and a try/catch.</summary>
        public string Restock(int wanted)
        {
            var missing = 0;

            for (var i = 0; i < wanted; i++)
                missing += i < this.Quantity ? 0 : 1;

            try
            {
                return missing == 0 ? "ok" : "short " + missing;
            }
            catch (InvalidOperationException)
            {
                return "failed";
            }
        }

        public int WhileLoop(int n)
        {
            var total = 0;

            while (total < n)
                total += 2;

            return total;
        }

        public int IfNoElse(int n)
        {
            var x = n;

            if (n > 10)
                x = 10;

            return x;
        }

        public int Nested(int n)
        {
            var total = 0;

            for (var i = 0; i < n; i++)
            for (var j = 0; j < i; j++)
                total += j;

            return total;
        }

        public int Foreach(List<int> values)
        {
            var total = 0;

            foreach (var value in values)
                total += value;

            return total;
        }

        /// <summary>
        /// Three cases is few enough that the compiler tests them one at a time rather than with
        /// a switch instruction, so this is a chain of conditional jumps - and structures like
        /// any other chain of them.
        /// </summary>
        public string Switchy(int n)
        {
            switch (n)
            {
                case 0: return "zero";
                case 1: return "one";
                default: return "many";
            }
        }

        /// <summary>
        /// Dense enough that the compiler emits the <c>switch</c> instruction - a jump table -
        /// rather than a chain of comparisons. The projection does not model a jump table, so
        /// this is the shape it leaves alone.
        /// </summary>
        public string Dense(int n)
        {
            switch (n)
            {
                case 0: return "zero";
                case 1: return "one";
                case 2: return "two";
                case 3: return "three";
                case 4: return "four";
                case 5: return "five";
                default: return "many";
            }
        }

        public int Chained(int a, int b) => a > b ? a - b : b - a;
    }

    /// <summary>
    /// <see cref="ClrCSharpForm.Structured"/>: the passes that undo the compiler's scaffolding.
    /// </summary>
    /// <remarks>
    /// Two kinds of check here, and the second is the important one. The first is that a known
    /// method comes out reading like its source. The second is that the rewrites are honest
    /// across everything to hand: that no jump loses the label it needs, that no instruction
    /// stops being accounted for, and that a shape the passes cannot prove is left as it was.
    /// </remarks>
    public class CSharpStructuringTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static;

        private static readonly Type[] Corpus =
        {
            typeof(string), typeof(int), typeof(List<int>), typeof(Dictionary<string, int>),
            typeof(System.Text.StringBuilder), typeof(Uri), typeof(DateTime), typeof(decimal),
            typeof(System.Linq.Enumerable), typeof(Guid), typeof(Convert), typeof(Math),
            typeof(StructuringSample)
        };

        private static string Structured(string name)
        {
            return ClrMethodCSharp.Of(
                typeof(StructuringSample).GetMethod(name, All), ClrCSharpForm.Structured).Dump();
        }

        /// <summary>
        /// The whole point, on the method the shapes were chosen for: what comes out is the
        /// source it was compiled from, apart from the names - which are in the PDB, not the IL.
        /// </summary>
        [Test]
        public async Task StructuresAMethodBackIntoTheShapeOfItsSource()
        {
            var dump = Structured(nameof(StructuringSample.Restock));

            // var missing = 0;
            await Assert.That(dump).Contains("int missing = 0;");

            // for (var i = 0; i < wanted; i++)
            await Assert.That(dump).Contains("for (int i = 0; i < wanted; i++)");

            // missing += i < this.Quantity ? 0 : 1;
            await Assert.That(dump).Contains("missing += i < this.Quantity ? 0 : 1;");

            // return missing == 0 ? "ok" : "short " + missing;
            await Assert.That(dump).Contains(
                "return missing == 0 ? \"ok\" : \"short \" + missing.ToString();");

            // catch (InvalidOperationException) - no variable, because the source declared none
            await Assert.That(dump).Contains("catch (InvalidOperationException)");
            await Assert.That(dump).Contains("return \"failed\";");

            // Nothing is left jumping: every branch in the method became a loop or an expression.
            await Assert.That(dump).DoesNotContain("goto");
        }

        /// <summary>
        /// The faithful form is what a projection is unless something asks for more, so that a
        /// listing nothing asked to interpret is not interpreted.
        /// </summary>
        [Test]
        public async Task TheFaithfulFormIsStillTheDefault()
        {
            var method = typeof(StructuringSample).GetMethod(nameof(StructuringSample.Restock), All);

            var byDefault = ClrMethodCSharp.Of(method);

            await Assert.That(byDefault.Form).IsEqualTo(ClrCSharpForm.Faithful);
            await Assert.That(byDefault.Dump()).Contains("goto IL_");
            await Assert.That(byDefault.Dump()).DoesNotContain("for (");

            await Assert.That(ClrMethodIl.Of(method).ToCSharp().Dump()).IsEqualTo(byDefault.Dump());
        }

        [Test]
        public async Task RecognisesTheLoopsAndConditionalsTheCompilerFlattened()
        {
            await Assert.That(Structured(nameof(StructuringSample.WhileLoop)))
                .Contains("while (total < n)");

            // The test compiles to a bool temporary and a branch on it; both fold away.
            await Assert.That(Structured(nameof(StructuringSample.IfNoElse)))
                .Contains("if (n > 10)");

            var nested = Structured(nameof(StructuringSample.Nested));

            await Assert.That(nested).Contains("for (int i = 0; i < n; i++)");
            await Assert.That(nested).Contains("for (int j = 0; j < i; j++)");

            // The inner loop is inside the outer one, which means its lines are indented past it.
            var inner = nested.Split('\n').First(line => line.Contains("int j = 0"));
            var outer = nested.Split('\n').First(line => line.Contains("int i = 0"));

            await Assert.That(inner.IndexOf("for", StringComparison.Ordinal))
                .IsGreaterThan(outer.IndexOf("for", StringComparison.Ordinal));
        }

        /// <summary>
        /// A <c>foreach</c> is a while loop over an enumerator inside a try/finally, and reads
        /// as one - including the property the compiler calls as a method.
        /// </summary>
        [Test]
        public async Task AForeachReadsAsTheLoopItCompilesTo()
        {
            var dump = Structured(nameof(StructuringSample.Foreach));

            await Assert.That(dump).Contains(".GetEnumerator();");
            await Assert.That(dump).Contains("while (loc1.MoveNext())");

            // The enumerator is the compiler's own local and has no name; the loop variable is
            // the source's, so it is not folded into the statement that reads it.
            await Assert.That(dump).Contains("value = loc1.Current;");
            await Assert.That(dump).Contains("total += value;");
            await Assert.That(dump).Contains("finally");
            await Assert.That(dump).Contains("loc1.Dispose();");
        }

        [Test]
        public async Task ConditionalExpressionsKeepTheirSourceOrder()
        {
            // a > b ? a - b : b - a, not the negated form the branch is written as: the arms
            // come back the way round the source had them.
            await Assert.That(Structured(nameof(StructuringSample.Chained)))
                .Contains("return a > b ? a - b : b - a;");
        }

        /// <summary>
        /// The refusal case, which matters as much as the rewrites: a jump table is not a shape
        /// the passes model, so a switch that compiles to one keeps its jumps.
        /// </summary>
        [Test]
        public async Task LeavesAloneAShapeItCannotProve()
        {
            var dump = Structured(nameof(StructuringSample.Dense));

            // The switch instruction and its cases are printed as they are, and nothing tries
            // to lift the arms into them.
            await Assert.That(dump).Contains("switch (");
            await Assert.That(dump).Contains("case 0: goto IL_");
            await Assert.That(dump).Contains("case 5: goto IL_");

            // The returns still collapse, because that pass can prove its own shape.
            await Assert.That(dump).Contains("return \"zero\";");
        }

        /// <summary>
        /// A switch the compiler tests one case at a time is a chain of conditional jumps, and
        /// each arm is reached from exactly one of them - so the arms move into the tests and
        /// the method reads as the chain of returns it is.
        /// </summary>
        [Test]
        public async Task AChainOfComparisonsStructuresIntoItsArms()
        {
            var dump = Structured(nameof(StructuringSample.Switchy));

            await Assert.That(dump).Contains("if (loc0 == 0)");
            await Assert.That(dump).Contains("return \"zero\";");
            await Assert.That(dump).Contains("return \"many\";");
            await Assert.That(dump).DoesNotContain("goto");
        }

        /// <summary>
        /// Every jump has to keep a label to land on, in both forms and everywhere - the
        /// invariant a fold across a block boundary breaks.
        /// </summary>
        [Test]
        [Arguments(ClrCSharpForm.Faithful)]
        [Arguments(ClrCSharpForm.Structured)]
        public async Task EveryGotoHasALabelToLandOn(ClrCSharpForm form)
        {
            var dangling = new List<string>();
            var walked = 0;

            foreach (var (method, projection) in Project(form))
            {
                walked++;

                var labels = new HashSet<string>(
                    projection.Lines.Where(line => line.Offset.HasValue)
                        .Select(line => $"IL_{line.Offset.Value:x4}"));

                foreach (var jump in Jumps(projection))
                {
                    if (!labels.Contains(jump))
                        dangling.Add($"{method.DeclaringType?.Name}.{method.Name} -> {jump}");
                }
            }

            await Assert.That(walked).IsGreaterThan(500);
            await Assert.That(dangling).IsEmpty();
        }

        /// <summary>
        /// Structuring may rearrange what a statement says, but not what the method does: every
        /// instruction the faithful form accounts for has to still be accounted for.
        /// </summary>
        /// <remarks>
        /// This is the strongest check available without running the code. Each statement names
        /// the IL it came from, so comparing those names between the two forms catches a pass
        /// that dropped a statement, folded one twice, or quietly lost a jump.
        /// </remarks>
        [Test]
        public async Task StructuringAccountsForEveryInstructionTheFaithfulFormDoes()
        {
            var mismatches = new List<string>();
            var walked = 0;

            foreach (var type in Corpus)
            {
                foreach (var method in type.GetMethods(All | BindingFlags.DeclaredOnly))
                {
                    var il = ClrMethodIl.Of(method);

                    if (il == null)
                        continue;

                    walked++;

                    var faithful = Attributed(il.ToCSharp());
                    var structured = Attributed(il.ToCSharp(ClrCSharpForm.Structured));

                    if (!faithful.SequenceEqual(structured))
                        mismatches.Add($"{type.Name}.{method.Name}");
                }
            }

            await Assert.That(walked).IsGreaterThan(500);
            await Assert.That(mismatches).IsEmpty();
        }

        /// <summary>
        /// Braces have to balance in both forms: an unbalanced one means a block was opened
        /// across another block's edge, which is the failure a wrapping pass has to avoid.
        /// </summary>
        [Test]
        [Arguments(ClrCSharpForm.Faithful)]
        [Arguments(ClrCSharpForm.Structured)]
        public async Task BracesBalanceEverywhere(ClrCSharpForm form)
        {
            var unbalanced = new List<string>();

            foreach (var (method, projection) in Project(form))
            {
                // Only the punctuation counts: a string literal is allowed to contain a brace,
                // and a record's ToString is full of them.
                var braces = projection.Lines
                    .SelectMany(line => line.Tokens)
                    .Where(token => token.Kind == ClrCSharpTokenKind.Punctuation)
                    .SelectMany(token => token.Text)
                    .ToList();

                if (braces.Count(c => c == '{') != braces.Count(c => c == '}'))
                    unbalanced.Add($"{method.DeclaringType?.Name}.{method.Name}");
            }

            await Assert.That(unbalanced).IsEmpty();
        }

        /// <summary>
        /// Structuring must not turn a projection approximate: it rewrites what the faithful
        /// form already modelled, and models nothing new.
        /// </summary>
        [Test]
        public async Task StructuringNeverMakesAProjectionApproximate()
        {
            var approximate = Project(ClrCSharpForm.Structured)
                .Where(item => !item.Projection.IsExact)
                .Select(item => $"{item.Method.DeclaringType?.Name}.{item.Method.Name}")
                .ToList();

            await Assert.That(approximate).IsEmpty();
        }

        /// <summary>The IL each statement is attributed with, in a comparable order.</summary>
        private static List<string> Attributed(ClrMethodCSharp projection)
        {
            return projection.Lines
                .Where(line => !string.IsNullOrEmpty(line.Comment))
                .SelectMany(line => line.Comment.Split(new[] { "; " }, StringSplitOptions.None))
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToList();
        }

        private static IEnumerable<string> Jumps(ClrMethodCSharp projection)
        {
            return projection.Lines
                .SelectMany(line => line.Tokens)
                .Where(token => token.Kind == ClrCSharpTokenKind.ControlKeyword
                                && token.Text.StartsWith("IL_", StringComparison.Ordinal))
                .Select(token => token.Text);
        }

        private static IEnumerable<(MethodBase Method, ClrMethodCSharp Projection)> Project(ClrCSharpForm form)
        {
            foreach (var type in Corpus)
            {
                foreach (var method in type.GetMethods(All | BindingFlags.DeclaredOnly))
                {
                    var il = ClrMethodIl.Of(method);

                    if (il != null)
                        yield return (method, il.ToCSharp(form));
                }
            }
        }
    }
}
