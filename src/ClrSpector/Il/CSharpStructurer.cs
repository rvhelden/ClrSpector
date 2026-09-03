using System;
using System.Collections.Generic;
using System.Linq;

namespace ClrSpector
{
    /// <summary>
    /// Rewrites a faithful projection into something closer to the source it was compiled from:
    /// the compiler's temporaries folded back into the expressions they carry, its conditional
    /// jumps back into conditional expressions, and its bottom-tested jumps back into loops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every pass here is a pattern match with a proof obligation, and the proof is always the
    /// same shape: the run of statements being rewritten has to be entered only where the
    /// rewrite says it is entered. That is why each one counts the jumps that land on the labels
    /// it wants to remove, and refuses when the count is not what the pattern requires. A pass
    /// that cannot prove its shape does nothing, and the faithful gotos stand - which is the
    /// point: what comes out is either source-shaped or honest, never a guess.
    /// </para>
    /// <para>
    /// Statements inside an exception region are only rewritten where the region's own edges are
    /// left alone. A handler is entered by the runtime rather than by a jump, so an offset the
    /// exception table names cannot be folded away: the passes treat those offsets as fixed.
    /// </para>
    /// <para>
    /// What this deliberately does not do is rename anything. A local is called whatever
    /// <see cref="ClrMethodIl.LocalVariables"/> says it is - the name from the module's PDB
    /// where there is one, the slot number where there is not - and no pass invents or improves
    /// on that.
    /// </para>
    /// </remarks>
    internal sealed class CSharpStructurer
    {
        /// <summary>
        /// How many rewrites are allowed per statement before the loop gives up.
        /// </summary>
        /// <remarks>
        /// A round applies one rewrite and starts again, because a fold often exposes the next
        /// one - a conditional expression only becomes foldable once the temporary feeding it
        /// has been. So the budget has to scale with the method: a fixed dozen rounds silently
        /// stopped halfway through anything larger than an example. Each rewrite removes at
        /// least one statement, so this is a stop on a pass undoing another's work, not on
        /// ordinary progress.
        /// </remarks>
        private const int RoundsPerStatement = 4;

        private readonly List<CSharpNode> nodes;

        private readonly HashSet<int> pinnedOffsets;

        private readonly Func<string, string> localTypeOf;

        private readonly Func<string, bool> isSourceNamed;

        /// <summary>Names this declared inside a loop header, so nothing declares them again.</summary>
        private readonly HashSet<string> declaredInPlace = new HashSet<string>();

        private CSharpStructurer(
            List<CSharpNode> nodes,
            IEnumerable<int> pinnedOffsets,
            Func<string, string> localTypeOf,
            Func<string, bool> isSourceNamed)
        {
            this.nodes = nodes;
            this.pinnedOffsets = new HashSet<int>(pinnedOffsets);
            this.localTypeOf = localTypeOf;
            this.isSourceNamed = isSourceNamed;
        }

        /// <summary>
        /// Rewrites <paramref name="nodes"/> in place, as far as it can prove it may.
        /// </summary>
        /// <param name="nodes">The faithful projection's statements.</param>
        /// <param name="pinnedOffsets">
        /// Offsets that must keep their own statement: the edges of the exception regions, which
        /// the runtime enters and leaves without any jump saying so.
        /// </param>
        /// <param name="localTypeOf">How to spell a local's type, for a loop variable declared
        /// in the loop that uses it.</param>
        /// <param name="isSourceNamed">
        /// Whether a name is one the source wrote. Those are left alone: folding a variable the
        /// author declared into the expression that reads it moves further from the source than
        /// the jumps did, and the whole point of the pass is to move closer to it.
        /// </param>
        /// <returns>The names declared inside a loop header, which nothing else should declare.</returns>
        public static IReadOnlyCollection<string> Apply(
            List<CSharpNode> nodes,
            IEnumerable<int> pinnedOffsets,
            Func<string, string> localTypeOf,
            Func<string, bool> isSourceNamed = null)
        {
            var structurer = new CSharpStructurer(nodes, pinnedOffsets, localTypeOf, isSourceNamed);
            structurer.Run();

            return structurer.declaredInPlace;
        }

        private void Run()
        {
            var budget = this.nodes.Count * RoundsPerStatement + 16;

            for (var round = 0; round < budget; round++)
            {
                var changed = this.SimplifyConstantBranches()
                              || this.DropRedundantJumps()
                              || this.FoldConditionalExpressions()
                              || this.InlineSingleUseValues()
                              || this.CollapseReturnTemporaries()
                              || this.DropUnusedExceptionDiscards()
                              || this.CompoundAssignments();

                if (!changed)
                    break;
            }

            // Loops and conditionals are structured after the expression work: a loop whose test
            // is still spread over two statements does not match the pattern, and a conditional
            // whose arms have not been folded is not one.
            for (var round = 0; round < budget; round++)
            {
                if (!this.StructureLoops() && !this.StructureConditionals())
                    break;
            }

            // Declarations move last, once the statements they would move onto have stopped
            // being rewritten and the loops have taken the variables that belong to them.
            for (var round = 0; round < budget; round++)
            {
                if (!this.DeclareAtFirstAssignment())
                    break;
            }
        }

        // ---------- the passes ----------

        /// <summary>
        /// Rewrites a branch whose condition is a constant into the jump it always is, or
        /// removes it when it never jumps.
        /// </summary>
        /// <remarks>
        /// The compiler emits <c>ldc.i4.1; brtrue</c> around a switch expression and a few other
        /// constructs, as somewhere to hang a sequence point. Read literally it is
        /// <c>if (1 != 0) goto next</c>, which says nothing about the method: the jump is always
        /// taken, and once it is unconditional the pass that drops jumps to the next statement
        /// takes it away entirely.
        /// </remarks>
        private bool SimplifyConstantBranches()
        {
            for (var i = 0; i < this.nodes.Count; i++)
            {
                var test = this.nodes[i];

                if (test.IsFixed || test.Control != CSharpControl.ConditionalGoto || test.Condition == null)
                    continue;

                var taken = AlwaysTaken(test.Condition);

                if (taken == null)
                    continue;

                if (taken.Value)
                {
                    test.Tokens = Join(
                        Keyword("goto"),
                        Space(),
                        Keyword($"IL_{test.Target ?? 0:x4}"),
                        Semicolon());
                    test.Control = CSharpControl.Goto;
                    test.Condition = null;

                    return true;
                }

                // Never taken: the statement is the branch and nothing else, so it goes - its
                // instructions stay named on whatever follows it, which is also where anything
                // that jumped to it was going to end up.
                var next = this.NextStatement(i, allowClose: true, allowOpen: true);

                if (next < 0 || this.nodes[next].Offset == null)
                    continue;

                this.RedirectJumps(test.Offset, this.nodes[next].Offset.Value);

                this.MergeComment(this.nodes[next], test);
                this.nodes.RemoveAt(i);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a condition is a constant, and which way - or null when it depends on
        /// something.
        /// </summary>
        /// <remarks>
        /// The two shapes a branch on a constant is projected as: the comparison against zero
        /// that <c>brtrue</c> and <c>brfalse</c> become, and a bare number.
        /// </remarks>
        private static bool? AlwaysTaken(List<ClrCSharpToken> condition)
        {
            if (condition.Count == 1 && IsNumber(condition[0]))
                return condition[0].Text != "0";

            if (condition.Count != 3 || !IsNumber(condition[0]) || !IsNumber(condition[2]))
                return null;

            var zero = condition[2].Text == "0";
            var nonZero = condition[0].Text != "0";

            switch (condition[1].Text)
            {
                case " != " when zero:
                    return nonZero;

                case " == " when zero:
                    return !nonZero;

                default:
                    return null;
            }
        }

        private static bool IsNumber(ClrCSharpToken token)
        {
            return token.Kind == ClrCSharpTokenKind.Number;
        }

        /// <summary>
        /// Drops a jump to the statement that already follows it. The compiler emits these at
        /// the end of nearly every block; in C# they are what falling off the end already does.
        /// </summary>
        private bool DropRedundantJumps()
        {
            for (var i = 0; i < this.nodes.Count; i++)
            {
                var jump = this.nodes[i];

                if (jump.IsFixed || jump.Control != CSharpControl.Goto || jump.Target == null)
                    continue;

                // Closing braces may be skipped over - falling out of a block goes to the same
                // place the jump did. An opening one may not: the target would be inside it.
                var next = this.NextStatement(i, allowClose: true, allowOpen: false);

                if (next < 0 || this.nodes[next].Offset != jump.Target)
                    continue;

                // Anything that jumped here now jumps where this jump went: arriving at this
                // statement only ever meant going there, so the label goes with it rather than
                // disappearing along with the statement.
                this.RedirectJumps(jump.Offset, jump.Target.Value);

                this.MergeComment(this.nodes[next], jump);
                this.nodes.RemoveAt(i);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Folds the diamond a conditional expression compiles to - jump over one arm, assign,
        /// jump past the other, assign - back into <c>x = c ? a : b;</c>.
        /// </summary>
        private bool FoldConditionalExpressions()
        {
            for (var i = 0; i + 4 < this.nodes.Count; i++)
            {
                var test = this.nodes[i];

                if (test.IsFixed || test.Control != CSharpControl.ConditionalGoto || test.Condition == null)
                    continue;

                var whenFalse = this.nodes[i + 1];
                var skip = this.nodes[i + 2];
                var whenTrue = this.nodes[i + 3];
                var after = this.nodes[i + 4];

                if (!this.IsPlainAssignment(whenFalse) || !this.IsPlainAssignment(whenTrue))
                    continue;

                if (whenFalse.AssignedName != whenTrue.AssignedName)
                    continue;

                if (skip.IsFixed || skip.Control != CSharpControl.Goto)
                    continue;

                // The two arms have to be the only ways to reach what follows them, and the
                // branch has to be the only way into the second arm.
                if (whenTrue.Offset != test.Target || after.Offset != skip.Target)
                    continue;

                if (this.JumpsTo(test.Target.Value) != 1 || this.JumpsTo(skip.Target.Value) != 1)
                    continue;

                if (this.JumpsTo(whenFalse.Offset) != 0)
                    continue;

                // Only the statements that go have to be unpinned: the test survives as the
                // assignment, and the statement after the arms is not touched at all. Refusing
                // on those would refuse every conditional at the start of a try block, whose
                // first statement carries the region's own offset.
                if (!this.SameDepth(i, i + 4) || this.AnyPinned(i + 1, i + 3))
                    continue;

                // The branch is taken when the condition holds, so the arm it jumps to is the
                // one that belongs first in the conditional expression - unless the condition is
                // a negation, in which case dropping it and swapping the arms says the same
                // thing the way the source did.
                var condition = test.Condition;
                var first = whenTrue.Value;
                var second = whenFalse.Value;

                if (TryDropNegation(condition, out var positive))
                {
                    condition = positive;
                    first = whenFalse.Value;
                    second = whenTrue.Value;
                }

                test.Tokens = Assignment(whenFalse.AssignedName, Conditional(condition, first, second));
                test.Value = Conditional(condition, first, second);
                test.AssignedName = whenFalse.AssignedName;
                test.Control = CSharpControl.None;
                test.Target = null;
                test.Condition = null;

                this.MergeComment(test, whenFalse, skip, whenTrue);
                this.nodes.RemoveRange(i + 1, 3);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Substitutes a value assigned once and read once into the statement that reads it -
        /// which is what the compiler's own temporaries and this projection's spill slots are.
        /// </summary>
        private bool InlineSingleUseValues()
        {
            for (var i = 0; i < this.nodes.Count; i++)
            {
                var producer = this.nodes[i];

                if (!this.IsPlainAssignment(producer) && !IsDeclaration(producer))
                    continue;

                var name = producer.AssignedName;

                // A variable the source declared stays a variable.
                if (this.isSourceNamed?.Invoke(name) == true)
                    continue;

                var next = this.NextStatement(i, allowClose: false, allowOpen: false);

                if (next != i + 1)
                    continue;

                var consumer = this.nodes[next];

                if (consumer.IsFixed || this.JumpsTo(consumer.Offset) != 0)
                    continue;

                // The producer is the statement that goes, so it is the one that may not sit
                // at an offset the exception table names - and the consumer keeps its own label
                // if that label is one, rather than taking the producer's over it.
                if (!this.SameDepth(i, next) || this.AnyPinned(i, i) || this.AnyPinned(next, next))
                    continue;

                if (consumer.ReadNames.Count(read => read == name) != 1)
                    continue;

                if (!this.IsReadOnlyWhereWritten(name))
                    continue;

                if (!Substitute(consumer, name, producer.Value))
                    continue;

                // The producer's label is the earlier one, and something may jump to it, so the
                // merged statement takes it over.
                consumer.Offset = producer.Offset ?? consumer.Offset;

                this.MergeComment(consumer, producer);
                this.nodes.RemoveAt(i);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether every read of <paramref name="name"/> in the method sits directly after a
        /// statement that writes it.
        /// </summary>
        /// <remarks>
        /// This is what makes folding one write into the read after it sound without tracing
        /// every path through the method. A name used this way - which is what a compiler
        /// temporary and this projection's own spill slots are - can only ever be read from the
        /// write immediately before it, however many other writes there are elsewhere. A name
        /// read anywhere else could be reached from a different write, and is left alone.
        /// </remarks>
        private bool IsReadOnlyWhereWritten(string name)
        {
            for (var i = 0; i < this.nodes.Count; i++)
            {
                var node = this.nodes[i];

                if (node.Kind != CSharpNodeKind.Statement || !node.ReadNames.Contains(name))
                    continue;

                if (!this.IsFedByTheStatementBefore(i, name))
                    return false;

                // A read something jumps to is reached more than one way, so every one of those
                // ways has to write the name too - which is what a value carried to a shared
                // statement by two branches looks like.
                for (var j = 0; j < this.nodes.Count; j++)
                {
                    if (this.nodes[j].Target != node.Offset)
                        continue;

                    if (!this.IsFedByTheStatementBefore(j, name))
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether the statement before <paramref name="index"/> writes <paramref name="name"/>,
        /// at the same nesting - so control arriving here has just given the name its value.
        /// </summary>
        private bool IsFedByTheStatementBefore(int index, string name)
        {
            var previous = this.PreviousStatement(index);

            return previous >= 0
                   && this.nodes[previous].AssignedName == name
                   && this.nodes[previous].Depth == this.nodes[index].Depth;
        }

        /// <summary>
        /// Turns the compiler's single-exit return - every branch assigning a temporary and
        /// jumping to one <c>return</c> of it - back into a return in each branch.
        /// </summary>
        private bool CollapseReturnTemporaries()
        {
            for (var r = 0; r < this.nodes.Count; r++)
            {
                var exit = this.nodes[r];

                if (exit.IsFixed || exit.Control != CSharpControl.Return || exit.Offset == null)
                    continue;

                if (exit.Value == null || exit.Value.Count != 1
                    || exit.Value[0].Kind != ClrCSharpTokenKind.Identifier)
                {
                    continue;
                }

                var name = exit.Value[0].Text;

                // Nothing may read the temporary except this return, or a branch would lose the
                // value it was going to read.
                if (this.Statements().Count(node => node.ReadNames.Contains(name)) != 1)
                    continue;

                var writers = new List<int>();

                for (var i = 0; i < this.nodes.Count; i++)
                {
                    if (this.nodes[i].AssignedName == name)
                        writers.Add(i);
                }

                if (writers.Count == 0)
                    continue;

                // Each writer either jumps to the shared return or falls into it; either way,
                // returning its value where it is written means the same thing.
                var jumping = writers.Where(i => this.JumpsToExitFrom(i, exit)).ToList();
                var falling = writers.Where(i => this.FallsIntoExitFrom(i, exit)).ToList();

                if (jumping.Count + falling.Count != writers.Count)
                    continue;

                // Once every writer returns, nothing jumps to the shared return and nothing
                // falls into it, so it can go. If anything else could reach it, it stays.
                if (this.JumpsTo(exit.Offset) != jumping.Count)
                    continue;

                var before = this.PreviousStatement(r);

                if (before >= 0 && !writers.Contains(before) && !EndsControl(this.nodes[before]))
                    continue;

                foreach (var i in writers)
                {
                    var writer = this.nodes[i];

                    writer.Tokens = Join(Keyword("return"), Space(), writer.Value, Semicolon());
                    writer.Control = CSharpControl.Return;
                    writer.AssignedName = null;

                    if (jumping.Contains(i))
                        this.MergeComment(writer, this.nodes[i + 1]);
                }

                this.MergeComment(this.nodes[writers[writers.Count - 1]], exit);

                // Removed back to front so the earlier indices stay valid.
                foreach (var i in jumping.OrderByDescending(index => index))
                    this.nodes.RemoveAt(i + 1);

                this.nodes.Remove(exit);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Drops the discard of an exception a <c>catch (T)</c> without a variable compiles to,
        /// and with it the name in the handler's header.
        /// </summary>
        private bool DropUnusedExceptionDiscards()
        {
            for (var i = 0; i < this.nodes.Count; i++)
            {
                var discard = this.nodes[i];

                if (discard.IsFixed || discard.AssignedName != "_" || discard.Value == null)
                    continue;

                if (discard.Value.Count != 1 || discard.Value[0].Kind != ClrCSharpTokenKind.Identifier)
                    continue;

                var name = discard.Value[0].Text;

                // Only the discard may mention it; a filter that tests the exception, or a
                // handler that uses it, keeps both the statement and the name. The handler's own
                // header mentions it too, but that is where it is declared, not a use.
                if (this.Statements().Count(node => node.ReadNames.Contains(name)) != 1)
                    continue;

                if (!this.DropNameFromHeader(i, name))
                    continue;

                var next = this.NextStatement(i, allowClose: true, allowOpen: true);

                if (next < 0)
                    continue;

                this.MergeComment(this.nodes[next], discard);
                this.nodes.RemoveAt(i);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Rewrites an assignment that reads its own target back as the compound assignment or
        /// the increment the source almost certainly was.
        /// </summary>
        private bool CompoundAssignments()
        {
            var operators = new[] { " + ", " - ", " * ", " / ", " % ", " & ", " | ", " ^ ", " << ", " >> " };

            foreach (var node in this.nodes)
            {
                if (!this.IsPlainAssignment(node) || node.Value == null || node.Value.Count < 3)
                    continue;

                var name = node.AssignedName;

                // The target has to be the whole of the left operand, or the rest is not what
                // the compound assignment would apply to.
                if (node.Value[0].Kind != ClrCSharpTokenKind.Identifier || node.Value[0].Text != name)
                    continue;

                var symbol = node.Value[1];

                if (symbol.Kind != ClrCSharpTokenKind.Punctuation || !operators.Contains(symbol.Text))
                    continue;

                var rest = node.Value.Skip(2).ToList();

                // The remainder has to be one whole expression, or the operator it binds
                // against may be looser than the compound assignment's implicit one:
                // x = x - a - b is not x -= a - b.
                if (rest.Count != 1)
                {
                    if (IsBracketed(rest))
                        rest = rest.Skip(1).Take(rest.Count - 2).ToList();
                    else if (HasTopLevelOperator(rest))
                        continue;
                }

                var step = symbol.Text == " + " ? "++" : symbol.Text == " - " ? "--" : null;

                node.Value = step != null && rest[0].Text == "1"
                    ? Join(Identifier(name), Punctuation(step))
                    : Join(Identifier(name), Punctuation($" {symbol.Text.Trim()}= "), rest);

                node.Tokens = Join(node.Value, Semicolon());
                node.AssignedName = null;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether <paramref name="tokens"/> has an operator outside any brackets, which is what
        /// makes it more than one expression.
        /// </summary>
        /// <remarks>
        /// A member access or an index is a single expression however many tokens it takes -
        /// <c>this.Quantity</c>, <c>values[i]</c>, <c>reader.Read()</c> - and moving one to the
        /// right of a compound assignment changes nothing. An operator at the top level would.
        /// </remarks>
        private static bool HasTopLevelOperator(List<ClrCSharpToken> tokens)
        {
            var operators = new[]
            {
                " + ", " - ", " * ", " / ", " % ", " & ", " | ", " ^ ", " << ", " >> ",
                " == ", " != ", " < ", " > ", " <= ", " >= ", " ? ", " : ", " as "
            };

            var depth = 0;

            foreach (var token in tokens)
            {
                depth += token.Text.Count(c => c == '(' || c == '[')
                         - token.Text.Count(c => c == ')' || c == ']');

                if (depth == 0 && operators.Contains(token.Text))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether <paramref name="tokens"/> is one bracketed expression, brackets included.
        /// </summary>
        private static bool IsBracketed(List<ClrCSharpToken> tokens)
        {
            if (tokens.Count < 2 || tokens[0].Text != "(" || tokens[tokens.Count - 1].Text != ")")
                return false;

            var depth = 0;

            for (var i = 0; i < tokens.Count; i++)
            {
                depth += tokens[i].Text.Count(c => c == '(') - tokens[i].Text.Count(c => c == ')');

                // Back to nothing before the end means the first bracket closed early, so the
                // brackets are around a part of the run rather than all of it.
                if (depth == 0 && i < tokens.Count - 1)
                    return false;
            }

            return depth == 0;
        }

        /// <summary>
        /// Moves a local's declaration onto the statement that first assigns it, which is where
        /// the source declared it.
        /// </summary>
        /// <remarks>
        /// Only at the top level of the method: an assignment inside a block would put the
        /// declaration in that block's scope, and the reads after it are not all in there.
        /// </remarks>
        private bool DeclareAtFirstAssignment()
        {
            foreach (var node in this.nodes)
            {
                if (node.Kind != CSharpNodeKind.Statement || node.Depth != 1)
                    continue;

                if (!this.IsPlainAssignment(node))
                    continue;

                var name = node.AssignedName;
                var type = this.localTypeOf?.Invoke(name);

                if (type == null || this.declaredInPlace.Contains(name))
                    continue;

                if (!ReferenceEquals(node, this.FirstMentionOf(name)))
                    continue;

                node.Tokens = Join(new ClrCSharpToken(ClrCSharpTokenKind.Type, type), Space(), node.Tokens);

                this.declaredInPlace.Add(name);

                return true;
            }

            return false;
        }

        /// <summary>The first line that mentions <paramref name="name"/> at all.</summary>
        private CSharpNode FirstMentionOf(string name)
        {
            return this.nodes.FirstOrDefault(
                node => node.AssignedName == name
                        || node.Tokens.Any(token => token.Kind == ClrCSharpTokenKind.Identifier
                                                    && token.Text == name));
        }

        /// <summary>
        /// Turns the shape a <c>for</c> or <c>while</c> compiles to - jump to the test, body,
        /// test, jump back - into the loop it came from.
        /// </summary>
        private bool StructureLoops()
        {
            // Widest first. An inner loop structured before the loop around it still has the
            // outer back edge pointing at its initialiser, which keeps that initialiser out of
            // the for header it belongs in.
            foreach (var test in this.LoopTestsWidestFirst())
            {
                var back = this.nodes[test];

                if (back.IsFixed || back.Control != CSharpControl.ConditionalGoto
                    || back.Condition == null || back.Offset == null)
                {
                    continue;
                }

                var body = this.IndexOfOffset(back.Target);

                if (body < 0 || body >= test)
                    continue;

                // The compiler enters the loop by jumping to its test, which is what says the
                // test belongs at the top rather than the bottom.
                var preheader = body - 1;

                if (preheader < 0)
                    continue;

                var entry = this.nodes[preheader];

                if (entry.IsFixed || entry.Control != CSharpControl.Goto || entry.Target != back.Offset)
                    continue;

                // Where the loop leaves for when its test fails, which is what a break goes to.
                var after = this.NextStatement(test, allowClose: true, allowOpen: false);
                var exit = after < 0 ? null : this.nodes[after].Offset;

                // A jump from inside the loop to its test is a continue, and one to what follows
                // it is a break - so those two are the targets a self-contained loop may name
                // outside itself.
                if (!this.IsSelfContained(body, test, new[] { entry }, new[] { back.Offset, exit }))
                    continue;

                // From outside, the only jump into the loop may be the one that enters it: the
                // test's label goes when the test does, and the body's is the back edge's.
                if (this.JumpsFromOutside(back.Offset, body, test, entry) != 0
                    || this.JumpsFromOutside(this.nodes[body].Offset, body, test, entry) != 0)
                {
                    continue;
                }

                var increment = this.IncrementOf(test - 1, back.Condition, body);
                var initialiser = increment == null ? -1 : this.InitialiserOf(preheader - 1, increment);

                var header = increment == null
                    ? Join(Keyword("while"), Space(), Punctuation("("), back.Condition, Punctuation(")"))
                    : Join(
                        Keyword("for"),
                        Space(),
                        Punctuation("("),
                        initialiser < 0
                            ? new List<ClrCSharpToken>()
                            : this.Initialisation(this.nodes[initialiser], body, test),
                        Punctuation("; "),
                        back.Condition,
                        Punctuation("; "),
                        Expression(increment),
                        Punctuation(")"));

                // Rewritten before the wrap, while the offsets still say what they say.
                this.RewriteLoopJumps(body, test - 1, back.Offset, exit);

                var last = increment == null ? test - 1 : test - 2;

                // Captured before the wrap moves everything: the initialiser is removed by
                // reference afterwards, because its index will not be its index any more.
                var initialising = initialiser < 0 ? null : this.nodes[initialiser];

                var opening = this.WrapAsBlock(body, last, header, back, entry, increment);

                if (opening == null)
                    continue;

                if (initialising != null)
                {
                    this.MergeComment(opening, initialising);
                    this.nodes.Remove(initialising);
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// The indices of the statements that could be a loop's test, the widest span first.
        /// </summary>
        private IEnumerable<int> LoopTestsWidestFirst()
        {
            var candidates = new List<(int Index, int Span)>();

            for (var i = 0; i < this.nodes.Count; i++)
            {
                var node = this.nodes[i];

                if (node.Control != CSharpControl.ConditionalGoto || node.Target == null)
                    continue;

                var body = this.IndexOfOffset(node.Target);

                if (body >= 0 && body < i)
                    candidates.Add((i, i - body));
            }

            return candidates.OrderByDescending(candidate => candidate.Span).Select(candidate => candidate.Index);
        }

        /// <summary>
        /// Turns a forward conditional jump over a run of statements into an <c>if</c>, and the
        /// run plus the one it jumps over into an <c>if</c> with an <c>else</c>.
        /// </summary>
        private bool StructureConditionals()
        {
            for (var i = 0; i < this.nodes.Count; i++)
            {
                var test = this.nodes[i];

                if (test.IsFixed || test.Control != CSharpControl.ConditionalGoto || test.Condition == null)
                    continue;

                var join = this.IndexOfOffset(test.Target);

                if (join <= i + 1 || !this.IsSelfContained(i + 1, join - 1))
                    continue;

                if (this.JumpsTo(test.Target) != 1)
                    continue;

                // The branch is taken when the condition holds, so the statements it jumps over
                // are the ones that run when it does not.
                var header = Join(Keyword("if"), Space(), Punctuation("("), Negate(test.Condition), Punctuation(")"));

                if (this.WrapAsBlock(i + 1, join - 1, header, test, null, null) == null)
                    continue;

                return true;
            }

            return false;
        }

        // ---------- what the passes have to prove ----------

        /// <summary>
        /// Whether the statements in <paramref name="first"/>..<paramref name="last"/> are only
        /// ever entered at <paramref name="first"/> and only ever left at their end.
        /// </summary>
        /// <remarks>
        /// This is the whole safety argument for making a block out of a run of statements. It
        /// refuses on anything it cannot see the whole of: a nested block, an offset the
        /// exception table names, a jump in from outside, or a jump out to anywhere other than
        /// the run's own labels.
        /// </remarks>
        private bool IsSelfContained(int first, int last, CSharpNode[] entries = null, int?[] allowed = null)
        {
            if (first > last || first <= 0 || last >= this.nodes.Count)
                return false;

            if (this.nodes[first].Kind != CSharpNodeKind.Statement
                || this.nodes[first].Depth != this.nodes[last].Depth)
            {
                return false;
            }

            var inside = new HashSet<int>();
            var depth = this.nodes[first].Depth;

            for (var i = first; i <= last; i++)
            {
                var node = this.nodes[i];

                // Blocks of its own are fine - a loop can hold a loop, or a try - as long as
                // they are wholly inside: a run that opens a block it does not close would put
                // the new block's braces across the old one's.
                if (node.Depth < depth)
                    return false;

                if (node.Kind == CSharpNodeKind.Statement && node.Offset.HasValue)
                    inside.Add(node.Offset.Value);
            }

            if (this.nodes[last].Kind == CSharpNodeKind.Open)
                return false;

            for (var i = 0; i < this.nodes.Count; i++)
            {
                var target = this.nodes[i].Target;

                if (target == null)
                    continue;

                var from = i >= first && i <= last;
                var to = inside.Contains(target.Value);

                // The jumps that make the block work - a loop's jump to its test - are the
                // block's own entry, and are about to be replaced by it.
                if (entries != null && entries.Any(entry => ReferenceEquals(entry, this.nodes[i])))
                    continue;

                // A jump out of the block to somewhere the block itself accounts for: its test,
                // or the statement after it, which become continue and break.
                if (from && allowed != null && allowed.Any(offset => offset == target))
                    continue;

                // A jump in from outside would skip the block's header; a jump out of it would
                // need a break or a continue, which this does not model.
                if (from != to)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// How many jumps from outside <paramref name="first"/>..<paramref name="last"/> name
        /// <paramref name="offset"/>, not counting the block's own entry.
        /// </summary>
        private int JumpsFromOutside(int? offset, int first, int last, CSharpNode entry)
        {
            if (offset == null)
                return 0;

            var count = 0;

            for (var i = 0; i < this.nodes.Count; i++)
            {
                if (this.nodes[i].Target != offset || (i >= first && i <= last))
                    continue;

                if (!ReferenceEquals(this.nodes[i], entry))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Turns the jumps a loop's body makes to its own test and its own exit into
        /// <c>continue</c> and <c>break</c>.
        /// </summary>
        /// <remarks>
        /// This is what the compiler does with both keywords, and with the false branch of an
        /// <c>if</c> that is the last thing in a loop body - so a loop whose body branches back
        /// to the test is far more common than one whose body does not.
        /// </remarks>
        private void RewriteLoopJumps(int first, int last, int? test, int? exit)
        {
            for (var i = first; i <= last && i < this.nodes.Count; i++)
            {
                var node = this.nodes[i];

                if (node.IsFixed || node.Target == null)
                    continue;

                var keyword = node.Target == test ? "continue" : node.Target == exit ? "break" : null;

                if (keyword == null)
                    continue;

                node.Tokens = node.Control == CSharpControl.ConditionalGoto && node.Condition != null
                    ? Join(
                        Keyword("if"),
                        Space(),
                        Punctuation("("),
                        node.Condition,
                        Punctuation(") "),
                        Keyword(keyword),
                        Semicolon())
                    : Join(Keyword(keyword), Semicolon());

                // No target any more: the keyword says where it goes, and nothing has to keep a
                // label for it.
                node.Target = null;
                node.Control = CSharpControl.Goto;
            }
        }

        /// <summary>
        /// The statement at <paramref name="index"/> when it is the loop's increment - an
        /// assignment to a name the test reads.
        /// </summary>
        private CSharpNode IncrementOf(int index, List<ClrCSharpToken> condition, int body)
        {
            if (index <= body)
                return null;

            var node = this.nodes[index];

            if (node.Kind != CSharpNodeKind.Statement || node.IsFixed
                || node.Control != CSharpControl.None || node.Value == null
                || this.JumpsTo(node.Offset) != 0)
            {
                return null;
            }

            var name = NameStepped(node);

            return name != null
                   && condition.Any(token => token.Kind == ClrCSharpTokenKind.Identifier && token.Text == name)
                ? node
                : null;
        }

        /// <summary>
        /// The index of the loop's initialiser - an assignment to the variable the increment
        /// steps, immediately before the jump into the loop - or -1.
        /// </summary>
        private int InitialiserOf(int index, CSharpNode increment)
        {
            if (index < 0)
                return -1;

            var name = NameStepped(increment);

            if (name == null)
                return -1;

            var node = this.nodes[index];

            return this.IsPlainAssignment(node) && node.AssignedName == name && this.JumpsTo(node.Offset) == 0
                ? index
                : -1;
        }

        /// <summary>
        /// The loop's initialisation, with the variable's type when the loop is the only place
        /// it is used - which is where the source declared it.
        /// </summary>
        private List<ClrCSharpToken> Initialisation(CSharpNode initialiser, int body, int test)
        {
            var tokens = Expression(initialiser);
            var name = initialiser.AssignedName;
            var type = this.localTypeOf?.Invoke(name);

            if (type == null || !this.IsUsedOnlyWithin(name, body, test))
                return tokens;

            this.declaredInPlace.Add(name);

            return Join(new ClrCSharpToken(ClrCSharpTokenKind.Type, type), Space(), tokens);
        }

        /// <summary>
        /// Whether <paramref name="name"/> is mentioned only by the loop being built - its
        /// initialiser included, since that is about to move into the header.
        /// </summary>
        private bool IsUsedOnlyWithin(string name, int body, int test)
        {
            for (var i = 0; i < this.nodes.Count; i++)
            {
                if (i >= body - 2 && i <= test)
                    continue;

                var node = this.nodes[i];

                if (node.ReadNames.Contains(name) || node.AssignedName == name)
                    return false;
            }

            return true;
        }

        /// <summary>The name a loop's increment steps, however it is written.</summary>
        private static string NameStepped(CSharpNode increment)
        {
            if (increment.AssignedName != null)
                return increment.AssignedName;

            // A compound assignment or an increment has no assigned name any more - it reads and
            // writes in one expression - so the name is the one it starts with.
            return increment.Value != null && increment.Value.Count > 0
                   && increment.Value[0].Kind == ClrCSharpTokenKind.Identifier
                ? increment.Value[0].Text
                : null;
        }

        /// <summary>Whether the writer at <paramref name="index"/> jumps to the shared return.</summary>
        private bool JumpsToExitFrom(int index, CSharpNode exit)
        {
            if (!this.IsWriter(index) || index + 1 >= this.nodes.Count)
                return false;

            var jump = this.nodes[index + 1];

            return !jump.IsFixed && jump.Control == CSharpControl.Goto && jump.Target == exit.Offset;
        }

        /// <summary>
        /// Whether the writer at <paramref name="index"/> falls straight into the shared return,
        /// which is what a jump to the statement after a block gets dropped down to.
        /// </summary>
        private bool FallsIntoExitFrom(int index, CSharpNode exit)
        {
            if (!this.IsWriter(index))
                return false;

            var next = this.NextStatement(index, allowClose: true, allowOpen: false);

            return next >= 0 && ReferenceEquals(this.nodes[next], exit);
        }

        private bool IsWriter(int index)
        {
            var writer = this.nodes[index];

            return !writer.IsFixed && writer.Control == CSharpControl.None && writer.Value != null;
        }

        /// <summary>Whether control does not continue past <paramref name="node"/>.</summary>
        private static bool EndsControl(CSharpNode node)
        {
            return node.Control == CSharpControl.Return
                   || node.Control == CSharpControl.Throw
                   || node.Control == CSharpControl.Goto;
        }

        /// <summary>The statement before <paramref name="index"/>, or -1.</summary>
        private int PreviousStatement(int index)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                switch (this.nodes[i].Kind)
                {
                    case CSharpNodeKind.Statement:
                        return i;

                    case CSharpNodeKind.Blank:
                    case CSharpNodeKind.Close:
                        continue;

                    default:
                        return -1;
                }
            }

            return -1;
        }

        private IEnumerable<CSharpNode> Statements()
        {
            return this.nodes.Where(node => node.Kind == CSharpNodeKind.Statement);
        }

        /// <summary>
        /// Points every jump to <paramref name="from"/> at <paramref name="to"/> instead.
        /// </summary>
        /// <remarks>
        /// What makes this sound is that it is only ever used where arriving at
        /// <paramref name="from"/> leads to <paramref name="to"/> and nowhere else - a jump to
        /// the next statement, or a branch that is never taken. The label in the tokens is
        /// rewritten with the target, so the printed goto and the recorded target cannot drift
        /// apart.
        /// </remarks>
        private void RedirectJumps(int? from, int to)
        {
            if (from == null || from == to)
                return;

            foreach (var node in this.nodes)
            {
                if (node.Target != from)
                    continue;

                // A switch's cases are redirected too. Their shape is not rewritten - that is
                // what makes them fixed - but a jump is a jump, and leaving one pointing at a
                // label that has gone is worse than touching it.
                node.Target = to;

                for (var i = 0; i < node.Tokens.Count; i++)
                {
                    if (node.Tokens[i].Kind == ClrCSharpTokenKind.ControlKeyword
                        && node.Tokens[i].Text.StartsWith("IL_", StringComparison.Ordinal))
                    {
                        node.Tokens[i] = Keyword($"IL_{to:x4}");
                    }
                }
            }
        }

        /// <summary>How many jumps name <paramref name="offset"/> as their target.</summary>
        private int JumpsTo(int? offset)
        {
            return offset == null
                ? 0
                : this.nodes.Count(node => node.Target == offset);
        }

        private int IndexOfOffset(int? offset)
        {
            return offset == null
                ? -1
                : this.nodes.FindIndex(node => node.Kind == CSharpNodeKind.Statement && node.Offset == offset);
        }

        /// <summary>The next statement after <paramref name="index"/>, or -1.</summary>
        private int NextStatement(int index, bool allowClose, bool allowOpen)
        {
            for (var i = index + 1; i < this.nodes.Count; i++)
            {
                switch (this.nodes[i].Kind)
                {
                    case CSharpNodeKind.Statement:
                        return i;

                    case CSharpNodeKind.Close when allowClose:
                    case CSharpNodeKind.Open when allowOpen:
                    case CSharpNodeKind.Blank:
                        continue;

                    default:
                        return -1;
                }
            }

            return -1;
        }

        private bool SameDepth(int first, int last)
        {
            var depth = this.nodes[first].Depth;

            for (var i = first; i <= last; i++)
            {
                if (this.nodes[i].Depth != depth)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Whether any statement in the range starts at an offset the exception table names,
        /// which is an offset control reaches without any jump saying so.
        /// </summary>
        private bool AnyPinned(int first, int last)
        {
            for (var i = first; i <= last; i++)
            {
                var offset = this.nodes[i].Offset;

                if (offset.HasValue && this.pinnedOffsets.Contains(offset.Value))
                    return true;
            }

            return false;
        }

        private bool IsPlainAssignment(CSharpNode node)
        {
            return node.Kind == CSharpNodeKind.Statement
                   && !node.IsFixed
                   && node.Control == CSharpControl.None
                   && node.AssignedName != null
                   && node.Value != null
                   && node.Tokens.Count > 0
                   && node.Tokens[0].Kind == ClrCSharpTokenKind.Identifier;
        }

        /// <summary>A temporary introduced as <c>var t0 = ...</c> rather than assigned.</summary>
        private static bool IsDeclaration(CSharpNode node)
        {
            return node.Kind == CSharpNodeKind.Statement
                   && !node.IsFixed
                   && node.Control == CSharpControl.None
                   && node.AssignedName != null
                   && node.Value != null
                   && node.Tokens.Count > 0
                   && node.Tokens[0].Kind == ClrCSharpTokenKind.Type;
        }

        // ---------- rewriting ----------

        /// <summary>
        /// Wraps <paramref name="first"/>..<paramref name="last"/> in a block under
        /// <paramref name="header"/>, removing the jumps that used to do the same job.
        /// </summary>
        private CSharpNode WrapAsBlock(
            int first,
            int last,
            List<ClrCSharpToken> header,
            CSharpNode test,
            CSharpNode entry,
            CSharpNode increment)
        {
            var carried = new[] { entry, test, increment }.Where(node => node != null).ToList();

            // A label on a statement the block replaces has to end up somewhere, and the only
            // somewhere is the block's own header. One such label can move there; two cannot
            // share a line, so the rewrite is abandoned instead.
            var labels = carried
                .Where(node => node.Offset.HasValue && this.RemainingJumpsTo(node.Offset.Value, carried) > 0)
                .ToList();

            if (labels.Count > 1)
                return null;

            var depth = this.nodes[first].Depth;

            var opening = new CSharpNode
            {
                Kind = CSharpNodeKind.Open,
                Depth = depth,
                Tokens = header,
                Offset = labels.Count == 1 ? labels[0].Offset : null
            };
            var brace = new CSharpNode
            {
                Kind = CSharpNodeKind.Open, Depth = depth, Tokens = new List<ClrCSharpToken> { Punctuation("{") }
            };

            var closing = new CSharpNode
            {
                Kind = CSharpNodeKind.Close, Depth = depth, Tokens = new List<ClrCSharpToken> { Punctuation("}") }
            };

            // The jumps that made the loop or the conditional work are what the block now says,
            // so their IL moves onto the header rather than disappearing.
            opening.Comment = string.Join("; ", carried.Select(node => node.Comment).Where(text => !string.IsNullOrEmpty(text)));

            // Incremented rather than set: a block inside the range keeps its own nesting.
            for (var i = first; i <= last; i++)
                this.nodes[i].Depth++;

            this.nodes.Insert(last + 1, closing);
            this.nodes.Insert(first, brace);
            this.nodes.Insert(first, opening);

            foreach (var node in carried)
                this.nodes.Remove(node);

            return opening;
        }

        /// <summary>
        /// How many jumps would still name <paramref name="offset"/> once
        /// <paramref name="removed"/> are gone.
        /// </summary>
        private int RemainingJumpsTo(int offset, IReadOnlyCollection<CSharpNode> removed)
        {
            return this.nodes.Count(
                node => node.Target == offset && !removed.Any(gone => ReferenceEquals(gone, node)));
        }

        /// <summary>
        /// Removes the exception's name from a handler's header, once nothing reads it.
        /// </summary>
        private bool DropNameFromHeader(int index, string name)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                var node = this.nodes[i];

                if (node.Kind != CSharpNodeKind.Open)
                    continue;

                var at = node.Tokens.FindIndex(
                    token => token.Kind == ClrCSharpTokenKind.Identifier && token.Text == name);

                if (at < 0)
                    continue;

                // The space before the name goes with it, so the header reads catch (T).
                node.Tokens.RemoveAt(at);

                if (at > 0 && node.Tokens[at - 1].Text == " ")
                    node.Tokens.RemoveAt(at - 1);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Replaces the one mention of <paramref name="name"/> in <paramref name="node"/> with
        /// <paramref name="value"/>, parenthesising it where the surrounding expression needs it.
        /// </summary>
        private static bool Substitute(CSharpNode node, string name, List<ClrCSharpToken> value)
        {
            if (value == null || value.Count == 0)
                return false;

            // A value substituted into the middle of an expression needs brackets only if it
            // has an operator of its own: a member access or a call is one thing however many
            // tokens it takes, and bracketing it would only add noise.
            var whole = IsWhole(node.Value, name) || IsWhole(node.Condition, name);
            var wrapped = !whole && HasTopLevelOperator(value) ? Bracket(value) : value;

            var substituted = false;

            foreach (var list in new[] { node.Tokens, node.Value, node.Condition })
            {
                if (list == null)
                    continue;

                var at = list.FindIndex(token => token.Kind == ClrCSharpTokenKind.Identifier && token.Text == name);

                if (at < 0)
                    continue;

                list.RemoveAt(at);
                list.InsertRange(at, wrapped);
                substituted = true;
            }

            return substituted;
        }

        private static bool IsWhole(List<ClrCSharpToken> tokens, string name)
        {
            return tokens != null && tokens.Count == 1 && tokens[0].Text == name;
        }

        private void MergeComment(CSharpNode node, params CSharpNode[] folded)
        {
            var parts = new[] { node.Comment }
                .Concat(folded.Select(item => item.Comment))
                .Where(text => !string.IsNullOrEmpty(text));

            node.Comment = string.Join("; ", parts);
        }

        // ---------- tokens ----------

        /// <summary>An assignment statement's tokens.</summary>
        private static List<ClrCSharpToken> Assignment(string name, List<ClrCSharpToken> value)
        {
            return Join(Identifier(name), Punctuation(" = "), value, Semicolon());
        }

        /// <summary>A conditional expression, with the arm the branch went to first.</summary>
        private static List<ClrCSharpToken> Conditional(
            List<ClrCSharpToken> condition, List<ClrCSharpToken> whenTrue, List<ClrCSharpToken> whenFalse)
        {
            return Join(
                condition,
                Punctuation(" ? "),
                whenTrue,
                Punctuation(" : "),
                whenFalse);
        }

        /// <summary>A statement's tokens without its trailing semicolon, for a loop header.</summary>
        private static List<ClrCSharpToken> Expression(CSharpNode node)
        {
            var tokens = node.Tokens.ToList();

            if (tokens.Count > 0 && tokens[tokens.Count - 1].Text == ";")
                tokens.RemoveAt(tokens.Count - 1);

            return tokens;
        }

        /// <summary>
        /// The opposite of a condition: the mirrored operator where there is one, and a negation
        /// otherwise.
        /// </summary>
        private static List<ClrCSharpToken> Negate(List<ClrCSharpToken> condition)
        {
            // Two negations are none, and the compiler's conditions are full of the first one.
            if (TryDropNegation(condition, out var positive))
                return positive;

            var mirrored = new Dictionary<string, string>
            {
                [" == "] = " != ",
                [" != "] = " == ",
                [" < "] = " >= ",
                [" >= "] = " < ",
                [" > "] = " <= ",
                [" <= "] = " > "
            };

            var operators = condition
                .Select((token, index) => (token, index))
                .Where(item => item.token.Kind == ClrCSharpTokenKind.Punctuation
                               && mirrored.ContainsKey(item.token.Text))
                .ToList();

            // Only a single comparison can be mirrored safely; with two, which one is the
            // outermost depends on brackets this does not parse.
            if (operators.Count == 1)
            {
                var negated = condition.ToList();
                negated[operators[0].index] = Punctuation(mirrored[operators[0].token.Text]);

                return negated;
            }

            return Join(Punctuation("!"), Bracket(condition));
        }

        /// <summary>
        /// Strips a leading <c>!</c> and the brackets it applies to, when that is the whole
        /// condition.
        /// </summary>
        private static bool TryDropNegation(List<ClrCSharpToken> condition, out List<ClrCSharpToken> positive)
        {
            positive = null;

            if (condition == null || condition.Count < 2 || condition[0].Text != "!")
                return false;

            var inner = condition.Skip(1).ToList();

            if (inner.Count == 1)
            {
                positive = inner;

                return true;
            }

            if (!IsBracketed(inner))
                return false;

            positive = inner.Skip(1).Take(inner.Count - 2).ToList();

            return true;
        }

        private static List<ClrCSharpToken> Bracket(List<ClrCSharpToken> tokens)
        {
            return Join(Punctuation("("), tokens, Punctuation(")"));
        }

        private static List<ClrCSharpToken> Join(params object[] parts)
        {
            var tokens = new List<ClrCSharpToken>();

            foreach (var part in parts)
            {
                switch (part)
                {
                    case ClrCSharpToken token:
                        tokens.Add(token);
                        break;

                    case IEnumerable<ClrCSharpToken> many:
                        tokens.AddRange(many);
                        break;
                }
            }

            return tokens;
        }

        private static int Count(Dictionary<string, int> counts, string name)
        {
            return name != null && counts.TryGetValue(name, out var count) ? count : 0;
        }

        private static ClrCSharpToken Keyword(string text) =>
            new ClrCSharpToken(ClrCSharpTokenKind.ControlKeyword, text);

        private static ClrCSharpToken Identifier(string text) =>
            new ClrCSharpToken(ClrCSharpTokenKind.Identifier, text);

        private static ClrCSharpToken Punctuation(string text) =>
            new ClrCSharpToken(ClrCSharpTokenKind.Punctuation, text);

        private static ClrCSharpToken Space() => Punctuation(" ");

        private static ClrCSharpToken Semicolon() => Punctuation(";");
    }
}
