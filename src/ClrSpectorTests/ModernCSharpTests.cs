using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClrSpector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    /// <summary>
    /// The constructs of modern C# put through the round trip, one feature at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// None of these has an instruction of its own. A generic constraint is metadata that
    /// changes what the compiler may emit; a switch expression is a decision tree of branches; a
    /// union match is a type test per case; a pattern is a chain of tests and casts; and an
    /// exception filter is a block the runtime runs before it decides to catch. What each test
    /// asserts is the shape that comes back - which is the shape the compiler actually produced,
    /// not the syntax that produced it.
    /// </para>
    /// <para>
    /// The sample lives in ModernSample.cs, compiled in Debug like the rest of the test
    /// project, so its locals carry their source names from the PDB.
    /// </para>
    /// </remarks>
    public class ModernCSharpTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The open generic definition, which is where a constrained method lives.</summary>
        private static readonly Type Ledger = typeof(ModernLedger<int>).GetGenericTypeDefinition();

        private static string Structured(Type type, string method)
        {
            return ClrMethodCSharp.Of(type.GetMethod(method, All), ClrCSharpForm.Structured).Dump();
        }

        /// <summary>
        /// <c>new T()</c> under a <c>new()</c> constraint has no instruction: the compiler calls
        /// <see cref="Activator.CreateInstance{T}()"/>, and the comparison goes through the
        /// constraint rather than an interface dispatch.
        /// </summary>
        [Test]
        public async Task AGenericConstraintComesBackAsTheCallItCompilesTo()
        {
            var dump = Structured(Ledger, nameof(ModernLedger<int>.Largest));

            await Assert.That(dump).Contains("public T Largest(IEnumerable<T> items)");
            await Assert.That(dump).Contains("Activator.CreateInstance<T>()");
            await Assert.That(dump).Contains("item.CompareTo(best)");

            // The foreach is the enumerator loop it compiles to, and the if that guards the
            // assignment is the branch back to the test - which is a continue.
            await Assert.That(dump).Contains("MoveNext())");
            await Assert.That(dump).Contains("continue;");
            await Assert.That(dump).Contains("best = item;");
            await Assert.That(dump).Contains("return best;");
        }

        /// <summary>
        /// A switch expression is a decision tree: the arms are labelled statements the tests
        /// jump to, which is why the projection leaves them as jumps rather than inventing a
        /// switch it cannot prove.
        /// </summary>
        [Test]
        public async Task ASwitchExpressionComesBackAsItsDecisionTree()
        {
            var dump = Structured(Ledger, nameof(ModernLedger<int>.Classify));

            await Assert.That(dump).Contains("if (n < 0) goto");
            await Assert.That(dump).Contains("if (n >= 10) goto");
            await Assert.That(dump).Contains("if (n == 0) goto");

            // Every arm's value became the return it feeds.
            foreach (var arm in new[] { "negative", "zero", "small", "large" })
                await Assert.That(dump).Contains($"return \"{arm}\";");

            // The compiler brackets a switch expression with a branch on a constant, as
            // somewhere to hang a sequence point. It says nothing, and it is gone.
            await Assert.That(dump).DoesNotContain("if (1 != 0)");
        }

        /// <summary>
        /// A .NET 11 union is a type with a <c>Value</c> holding whichever case it is, so
        /// matching one is an <c>isinst</c> per case - which is what the round trip shows, and
        /// is not visible in the source at all.
        /// </summary>
        [Test]
        public async Task AUnionMatchComesBackAsATypeTestPerCase()
        {
            var dump = Structured(Ledger, nameof(ModernLedger<int>.Area));

            // The union's own storage, reached through the property the compiler gave it.
            await Assert.That(dump).Contains(".Value;");

            await Assert.That(dump).Contains("as ModernCircle");
            await Assert.That(dump).Contains("as ModernSquare");

            // And the arms, with the properties read back as properties.
            await Assert.That(dump).Contains("c.Radius");
            await Assert.That(dump).Contains("s.Side * s.Side");
        }

        /// <summary>
        /// A <c>when</c> clause is not part of the catch in IL: it is a filter block the runtime
        /// runs first, whose result decides whether the handler runs.
        /// </summary>
        [Test]
        public async Task AnExceptionFilterComesBackAsItsOwnBlock()
        {
            var lines = ClrMethodCSharp.Of(
                    Ledger.GetMethod(nameof(ModernLedger<int>.Guarded), All), ClrCSharpForm.Structured)
                .Lines.Select(line => line.ToString().Trim())
                .ToList();

            var text = string.Join("\n", lines);

            // Two filters, each followed by the handler it guards.
            await Assert.That(lines.Count(line => line == "filter")).IsEqualTo(2);
            await Assert.That(Regex.Matches(text, @"catch \(object ").Count).IsEqualTo(2);
            await Assert.That(Regex.Matches(text, @"/\* endfilter \*/").Count).IsEqualTo(2);

            // The conditions the source wrote as when clauses, inside those blocks.
            await Assert.That(text).Contains("e.Message.Length > 1");
            await Assert.That(text).Contains("n > 5");

            await Assert.That(lines).Contains("finally");
            await Assert.That(text).Contains("Console.Write(");
        }

        /// <summary>
        /// Type, property, relational and combinator patterns, which compile to a chain of
        /// tests, casts and reads.
        /// </summary>
        [Test]
        public async Task PatternsComeBackAsTheTestsTheyCompileTo()
        {
            var dump = Structured(typeof(ModernPatterns), nameof(ModernPatterns.Describe));

            // null, then a type test and the unboxing cast the int pattern needs.
            await Assert.That(dump).Contains("if (value == null) goto");
            await Assert.That(dump).Contains("(int)value");

            // The relational pattern, and the property pattern on a string.
            await Assert.That(dump).Contains("i > 100");
            await Assert.That(dump).Contains("s.Length == 0");
            await Assert.That(dump).Contains("as string");

            // The property pattern on a case type reads its property.
            await Assert.That(dump).Contains(".Radius > 10");

            // Two arms each declare an i, in scopes that never overlap, so the compiler gave
            // them a slot each - and the PDB the same name. They are numbered apart, or the
            // copy between them would read as a copy to itself.
            await Assert.That(dump).Contains("i_1 = i;");
        }

        /// <summary>
        /// A list pattern is length checks and indexing, and a slice is a call to the runtime
        /// helper that copies one.
        /// </summary>
        [Test]
        public async Task AListPatternComesBackAsLengthChecksAndASlice()
        {
            var dump = Structured(typeof(ModernPatterns), nameof(ModernPatterns.Sequence));

            await Assert.That(dump).Contains(".Length");
            await Assert.That(dump).Contains("values[0]");

            // [1, 2, .. var rest] - the slice is a copy the runtime makes.
            await Assert.That(dump).Contains("GetSubArray<int>(");
            await Assert.That(dump).Contains("new Range(");

            // [.., var last] - counted from the end.
            await Assert.That(dump).Contains("- 1]");

            foreach (var arm in new[] { "empty", "one ", "starts ", "ends " })
                await Assert.That(dump).Contains($"\"{arm}\"");
        }

        /// <summary>
        /// An <c>is</c> pattern with combinators outside a switch: the same branches, and a bool
        /// result written the way source writes one.
        /// </summary>
        [Test]
        public async Task AnIsPatternWithCombinatorsComesBackAsBranches()
        {
            var dump = Structured(typeof(ModernPatterns), nameof(ModernPatterns.IsSmall));

            await Assert.That(dump).Contains("public bool IsSmall(object value)");
            await Assert.That(dump).Contains("as int");
            await Assert.That(dump).Contains("as short");
            await Assert.That(dump).Contains("< 10");

            // A bool is an integer in IL; the signature is what says to spell it as one.
            await Assert.That(dump).Contains("return true;");
            await Assert.That(dump).Contains("return false;");
        }

        /// <summary>
        /// Positional patterns over a tuple read its fields. The element names the source gave
        /// them are metadata on the signature rather than anything in the body, so the fields
        /// come back as the fields they are.
        /// </summary>
        [Test]
        public async Task PositionalPatternsOverATupleComeBackAsFieldReads()
        {
            var dump = Structured(typeof(ModernPatterns), nameof(ModernPatterns.Pair));

            await Assert.That(dump).Contains("ValueTuple<int, int>");
            await Assert.That(dump).Contains(".Item1");
            await Assert.That(dump).Contains(".Item2");

            // var (low, high) when low > high
            await Assert.That(dump).Contains("low > high");
            await Assert.That(dump).Contains("return \"inverted\";");
        }

        /// <summary>
        /// Whatever the construct, both forms have to stay honest about it: every jump lands on
        /// a label, the blocks balance, every instruction is accounted for, and structuring
        /// changes none of that.
        /// </summary>
        [Test]
        [Arguments(ClrCSharpForm.Faithful)]
        [Arguments(ClrCSharpForm.Structured)]
        public async Task EveryConstructInTheSampleProjectsHonestly(ClrCSharpForm form)
        {
            var problems = new List<string>();
            var walked = 0;

            foreach (var type in new[] { Ledger, typeof(ModernPatterns), typeof(ModernCircle) })
            {
                foreach (var method in type.GetMethods(All | BindingFlags.DeclaredOnly))
                {
                    var il = ClrMethodIl.Of(method);

                    if (il == null)
                        continue;

                    walked++;

                    var projection = il.ToCSharp(form);
                    var where = $"{type.Name}.{method.Name}";

                    if (!projection.IsExact)
                        problems.Add($"{where}: approximate");

                    var labels = new HashSet<string>(
                        projection.Lines.Where(line => line.Offset.HasValue)
                            .Select(line => $"IL_{line.Offset.Value:x4}"));

                    foreach (Match jump in Regex.Matches(projection.Dump(), @"goto (IL_[0-9a-f]{4})"))
                    {
                        if (!labels.Contains(jump.Groups[1].Value))
                            problems.Add($"{where}: goto {jump.Groups[1].Value} has no label");
                    }

                    var braces = projection.Lines
                        .SelectMany(line => line.Tokens)
                        .Where(token => token.Kind == ClrCSharpTokenKind.Punctuation)
                        .SelectMany(token => token.Text)
                        .ToList();

                    if (braces.Count(c => c == '{') != braces.Count(c => c == '}'))
                        problems.Add($"{where}: braces do not balance");

                    // The structured form may say it differently, never less.
                    if (form == ClrCSharpForm.Structured && !Attributed(il.ToCSharp())
                            .SequenceEqual(Attributed(projection)))
                    {
                        problems.Add($"{where}: instructions not accounted for");
                    }
                }
            }

            await Assert.That(walked).IsGreaterThan(8);
            await Assert.That(problems).IsEmpty();
        }

        private static List<string> Attributed(ClrMethodCSharp projection)
        {
            return projection.Lines
                .Where(line => !string.IsNullOrEmpty(line.Comment))
                .SelectMany(line => line.Comment.Split(new[] { "; " }, StringSplitOptions.None))
                .OrderBy(text => text, StringComparer.Ordinal)
                .ToList();
        }
    }
}
