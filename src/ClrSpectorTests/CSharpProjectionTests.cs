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
    /// <summary>A type whose methods cover the shapes the projection has to get right.</summary>
    public class ProjectionSample
    {
        public int Quantity = 3;

        public string Sku = "A-1";

        public static int Running;

        public int Add(int a, int b) => a + b;

        /// <summary>A loop, a ternary, a try/catch/finally and a string concatenation.</summary>
        public string Branchy(int n)
        {
            var total = 0;

            for (var i = 0; i < n; i++)
                total += i % 3 == 0 ? i : -i;

            try
            {
                total += this.Quantity;
            }
            catch (InvalidOperationException)
            {
                total = -1;
            }
            finally
            {
                Running = total;
            }

            return total > 0 ? "positive " + this.Sku : "other";
        }

        public int[] Arrays(int n)
        {
            var values = new int[n];

            for (var i = 0; i < values.Length; i++)
                values[i] = i * 2;

            return values;
        }

        public string Switchy(int n)
        {
            switch (n)
            {
                case 0: return "zero";
                case 1: return "one";
                case 2: return "two";
                default: return n.ToString();
            }
        }

        public bool Nullable(string text) => text == null || text.Length == 0;

        /// <summary>A filtered catch, a second typed catch and a finally over all of it.</summary>
        public int Filtered(int n)
        {
            try
            {
                throw new InvalidOperationException(n.ToString());
            }
            catch (Exception e) when (e.Message.Length > 1)
            {
                return 1;
            }
            catch (InvalidOperationException)
            {
                return 2;
            }
            finally
            {
                Running++;
            }
        }
    }

    /// <summary>
    /// The IL-to-C# projection. What is being checked is not that the C# is pretty but that it
    /// says the same thing the IL does: the operands end up in the right order, the branches
    /// have somewhere to land, and nothing is quietly dropped.
    /// </summary>
    public class CSharpProjectionTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>
        /// Types whose methods are projected wholesale, as the check that this survives real
        /// compiler output rather than only the sample above.
        /// </summary>
        private static readonly Type[] Framework =
        {
            typeof(string), typeof(int), typeof(List<int>), typeof(Dictionary<string, int>),
            typeof(System.Text.StringBuilder), typeof(Uri), typeof(DateTime), typeof(decimal),
            typeof(System.Linq.Enumerable), typeof(Guid), typeof(Convert), typeof(Math)
        };

        private static ClrMethodCSharp Project(string name)
        {
            return ClrMethodCSharp.Of(typeof(ProjectionSample).GetMethod(name, All));
        }

        /// <summary>
        /// The whole point: four instructions that push, push, add and return become the one
        /// expression they amount to.
        /// </summary>
        [Test]
        public async Task FoldsTheStackMachineBackIntoAnExpression()
        {
            var projection = Project(nameof(ProjectionSample.Add));

            await Assert.That(projection.IsExact).IsTrue();

            var statements = projection.Lines.Where(line => line.IsLabelled).ToList();

            await Assert.That(statements.Count).IsEqualTo(1);
            await Assert.That(statements[0].ToString()).Contains("return a + b;");

            // The instructions the statement was folded from are still named, so the listing
            // does not hide what it did.
            await Assert.That(statements[0].Comment).IsEqualTo("ldarg.1; ldarg.2; add; ret");
        }

        [Test]
        public async Task DeclaresTheLocalsWithTheTypesReflectionKnows()
        {
            var dump = Project(nameof(ProjectionSample.Branchy)).Dump();

            await Assert.That(dump).Contains("int loc0;");
            await Assert.That(dump).Contains("bool loc2;");
            await Assert.That(dump).Contains("string loc3;");
        }

        /// <summary>The signature is projected too, so the arguments have their real names.</summary>
        [Test]
        public async Task NamesArgumentsAndTheSignature()
        {
            var dump = Project(nameof(ProjectionSample.Arrays)).Dump();

            await Assert.That(dump).Contains("int[] Arrays(int n)");
            await Assert.That(dump).Contains("new int[n]");
        }

        [Test]
        public async Task ProjectsFieldsArraysAndCallsAsSourceWouldWriteThem()
        {
            var dump = Project(nameof(ProjectionSample.Branchy)).Dump();

            await Assert.That(dump).Contains("this.Quantity");
            await Assert.That(dump).Contains("ProjectionSample.Running = loc0;");
            await Assert.That(dump).Contains("string.Concat(");

            var arrays = Project(nameof(ProjectionSample.Arrays)).Dump();

            await Assert.That(arrays).Contains("loc0[loc1] = loc1 * 2;");
            await Assert.That(arrays).Contains("loc0.Length");
        }

        /// <summary>
        /// <c>brtrue</c> and <c>brfalse</c> branch on a value being zero, which reads three
        /// different ways depending on what the value is. Getting this wrong is what makes a
        /// projection misleading rather than merely ugly.
        /// </summary>
        [Test]
        public async Task BranchesOnZeroReadAgainstWhatTheValueIs()
        {
            var branchy = Project(nameof(ProjectionSample.Branchy)).Dump();

            // A bool local branches as itself.
            await Assert.That(branchy).Contains("if (loc2) goto");

            // An int compares to zero.
            await Assert.That(branchy).Contains("% 3 == 0)");

            // A reference compares to null.
            await Assert.That(Project(nameof(ProjectionSample.Nullable)).Dump()).Contains("== null");
        }

        [Test]
        public async Task ExceptionRegionsBecomeBlocks()
        {
            var lines = Project(nameof(ProjectionSample.Branchy)).Lines
                .Select(line => line.ToString().Trim())
                .ToList();

            await Assert.That(lines).Contains("try");
            await Assert.That(lines).Contains("finally");
            await Assert.That(lines.Any(line => line.StartsWith("catch (InvalidOperationException"))).IsTrue();

            // The exception a catch block is entered with is on the stack; if the projection did
            // not know that, the first statement of the handler would come out empty.
            await Assert.That(lines.Any(line => line.Contains("ex"))).IsTrue();

            var text = string.Join("\n", lines);

            await Assert.That(text.Count(c => c == '{')).IsEqualTo(text.Count(c => c == '}'));
        }

        [Test]
        public async Task SwitchesKeepEveryCase()
        {
            var dump = Project(nameof(ProjectionSample.Switchy)).Dump();

            await Assert.That(dump).Contains("switch (loc0)");
            await Assert.That(dump).Contains("case 0: goto");
            await Assert.That(dump).Contains("case 2: goto");
        }

        /// <summary>
        /// Control flow is projected as gotos, which is only honest if every one of them names a
        /// label the listing actually prints - the invariant that a fold across a block boundary
        /// would break.
        /// </summary>
        [Test]
        public async Task EveryGotoHasALabelToLandOn()
        {
            var dangling = new List<string>();

            foreach (var (method, projection) in ProjectEverything())
            {
                var labels = new HashSet<string>(
                    projection.Lines.Where(line => line.Offset.HasValue)
                        .Select(line => $"IL_{line.Offset.Value:x4}"));

                foreach (Match match in Regex.Matches(projection.Dump(), @"goto (IL_[0-9a-f]{4})"))
                {
                    if (!labels.Contains(match.Groups[1].Value))
                        dangling.Add($"{method.DeclaringType?.Name}.{method.Name} -> {match.Groups[1].Value}");
                }
            }

            await Assert.That(dangling).IsEmpty();
        }

        /// <summary>
        /// Every instruction of every method walked is modelled, and none of them throws. An
        /// unmodelled one is not a crash - it becomes a comment and clears
        /// <see cref="ClrMethodCSharp.IsExact"/> - but it is a gap, and this is what finds one.
        /// </summary>
        [Test]
        public async Task ModelsEveryInstructionInTheFrameworkMethodsItWalks()
        {
            var projected = ProjectEverything().ToList();

            // A guard on the walk itself: if the reflection filter stopped matching, this test
            // would pass by checking nothing.
            await Assert.That(projected.Count).IsGreaterThan(500);

            var approximate = projected
                .Where(item => !item.Projection.IsExact)
                .Select(item => $"{item.Method.DeclaringType?.Name}.{item.Method.Name}")
                .ToList();

            await Assert.That(approximate).IsEmpty();
        }

        /// <summary>
        /// The projection carries no palette of its own: the same
        /// <see cref="IlDumpStyle"/> drives it, and colour only ever adds escape sequences to
        /// the text the plain dump already produced.
        /// </summary>
        [Test]
        public async Task IsColouredUnderTheSameRulesAsAnIlDump()
        {
            var projection = Project(nameof(ProjectionSample.Branchy));

            var plain = projection.Dump(IlDumpStyle.Plain);
            var coloured = projection.Dump(IlDumpStyle.Ansi);

            await Assert.That(plain).DoesNotContain("");
            await Assert.That(coloured).Contains("");

            // Stripping the colour has to give back exactly the plain dump - if it does not, a
            // coloured line is a different line, and column alignment is the first casualty.
            var stripped = Regex.Replace(coloured, "\\[[0-9;]*m", string.Empty);

            await Assert.That(stripped).IsEqualTo(plain);

            // Auto decides by whether the output looks like a terminal, which under a test host
            // it does not.
            await Assert.That(projection.Dump(IlDumpStyle.Auto)).IsEqualTo(plain);
        }

        /// <summary>
        /// The same method projected from its MethodDesc, with no reflection in the way: the
        /// names come out of metadata and a call's argument count out of its signature blob,
        /// which is the only reason the stack can be modelled at all on this path.
        /// </summary>
        [Test]
        public async Task ProjectsIlReadFromAMethodDescWithoutReflection()
        {
            var description = ClrObject.From<ProjectionSample>().MethodTable
                .FindMethod(nameof(ProjectionSample.Branchy));

            var projection = ClrMethodCSharp.Of(description);

            await Assert.That(projection).IsNotNull();

            var dump = projection.Dump();

            await Assert.That(dump).Contains("String.Concat(");
            await Assert.That(dump).Contains("this.Quantity");

            // No local signature is decoded on this path, so the locals are declared untyped
            // rather than guessed at.
            await Assert.That(dump).Contains("types unknown");

            // The handler table is read out of the body's own sections, so the blocks are
            // there without reflection having been asked anything.
            await Assert.That(dump).Contains("catch (InvalidOperationException");
            await Assert.That(dump).Contains("finally");
            await Assert.That(projection.IsExact).IsTrue();
        }

        /// <summary>
        /// The exception table is read from the mapped image by hand, so the check that matters
        /// is against the one reader that is known to be right: reflection's own clauses, over
        /// every method of a pile of framework types.
        /// </summary>
        [Test]
        public async Task ExceptionRegionsReadFromMemoryMatchReflections()
        {
            var mismatches = new List<string>();
            var withRegions = 0;

            foreach (var type in Framework.Concat(new[] { typeof(ProjectionSample) }))
            {
                var methodTable = ClrObject.From(type).MethodTable;

                foreach (var method in type.GetMethods(All | BindingFlags.DeclaredOnly))
                {
                    var fromReflection = ClrMethodIl.Of(method);
                    if (fromReflection == null)
                        continue;

                    ClrMethodIl fromMemory;

                    try
                    {
                        fromMemory = ClrMethodIl.Of(methodTable.FindMethod(method));
                    }
                    catch (Exception)
                    {
                        // Finding the MethodDesc is not what is under test here.
                        continue;
                    }

                    if (fromMemory == null)
                        continue;

                    if (fromReflection.ExceptionRegions.Count > 0)
                        withRegions++;

                    var expected = Describe(fromReflection.ExceptionRegions);
                    var read = Describe(fromMemory.ExceptionRegions);

                    if (expected != read)
                        mismatches.Add($"{type.Name}.{method.Name}: {expected} != {read}");
                }
            }

            // A guard on the walk: handlers are rare enough that a broken filter here would
            // otherwise leave this test comparing nothing but empty lists.
            await Assert.That(withRegions).IsGreaterThan(20);
            await Assert.That(mismatches).IsEmpty();
        }

        /// <summary>
        /// A filter is the handler kind whose shape differs most between IL and C#: the
        /// condition is a block of its own, entered with the exception, and the catch it guards
        /// sees that same exception - so both have to be named the same thing.
        /// </summary>
        [Test]
        public async Task FilteredCatchesProjectAsAFilterBlockAndACatch()
        {
            foreach (var projection in new[]
                     {
                         Project(nameof(ProjectionSample.Filtered)),
                         ClrMethodCSharp.Of(ClrObject.From<ProjectionSample>().MethodTable
                             .FindMethod(nameof(ProjectionSample.Filtered)))
                     })
            {
                var lines = projection.Lines.Select(line => line.ToString().Trim()).ToList();
                var text = string.Join("\n", lines);

                await Assert.That(lines).Contains("filter");
                await Assert.That(text).Contains("/* endfilter */");
                await Assert.That(text).Contains("catch (object ex)");
                await Assert.That(text).Contains("catch (InvalidOperationException ex1)");
                await Assert.That(text).Contains("finally");

                // The filter tests the exception the catch binds, so it has to be the same name.
                await Assert.That(text).Contains("ex as Exception");

                await Assert.That(text.Count(c => c == '{')).IsEqualTo(text.Count(c => c == '}'));
            }
        }

        /// <summary>
        /// The regions a body carries, rendered so two readers of them can be compared. The
        /// caught type's name is left out: reflection resolves it to a type and the metadata
        /// reader to a name, and the two spell a nested or generic type differently.
        /// </summary>
        private static string Describe(IEnumerable<ClrIlExceptionRegion> regions)
        {
            return string.Join(
                " | ",
                regions.Select(region =>
                    $"{region.Kind} {region.TryOffset:x4}+{region.TryLength:x4} " +
                    $"{region.HandlerOffset:x4}+{region.HandlerLength:x4} {region.FilterOffset:x4}"));
        }

        /// <summary>
        /// The signature behind that: a call token's parameter count, which is what a
        /// MethodDesc-sourced projection needs and reflection is not asked for.
        /// </summary>
        [Test]
        public async Task ReadsACallTokensParameterCountFromMetadata()
        {
            var description = ClrObject.From<ProjectionSample>().MethodTable
                .FindMethod(nameof(ProjectionSample.Branchy));

            var calls = ClrMethodIl.Of(description).Instructions
                .Select(instruction => instruction.Operand as ClrIlToken)
                .Where(token => token?.Signature != null)
                .ToList();

            await Assert.That(calls).IsNotEmpty();

            // string.Concat(string, string) - two parameters and no this.
            var concat = calls.FirstOrDefault(token => token.Name?.EndsWith("::Concat") == true);

            await Assert.That(concat).IsNotNull();
            await Assert.That(concat.Signature.Parameters.Count).IsEqualTo(2);
            await Assert.That(concat.Signature.HasThis).IsFalse();
        }

        [Test]
        public async Task AMethodWithNoBodyProjectsToNothing()
        {
            await Assert.That(ClrMethodCSharp.Of(typeof(IDisposable).GetMethod("Dispose"))).IsNull();
            await Assert.That(ClrMethodCSharp.Of((ClrMethodIl)null)).IsNull();
        }

        /// <summary>Every method of the sample and of the framework types, projected once.</summary>
        private static IEnumerable<(MethodBase Method, ClrMethodCSharp Projection)> ProjectEverything()
        {
            foreach (var type in Framework.Concat(new[] { typeof(ProjectionSample) }))
            {
                foreach (var method in type.GetMethods(All | BindingFlags.DeclaredOnly))
                {
                    ClrMethodIl il;

                    try
                    {
                        il = ClrMethodIl.Of(method);
                    }
                    catch (Exception)
                    {
                        // Reading the IL is not what is under test here.
                        continue;
                    }

                    if (il == null)
                        continue;

                    yield return (method, il.ToCSharp());
                }
            }
        }
    }
}
