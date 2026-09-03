using System.Collections.Generic;
using System.Linq;

namespace ClrSpector
{
    /// <summary>What a projected node is structurally.</summary>
    internal enum CSharpNodeKind
    {
        /// <summary>A statement, which may carry a label.</summary>
        Statement,

        /// <summary>A block header and its opening brace - <c>try</c>, <c>while (x)</c>.</summary>
        Open,

        /// <summary>A closing brace.</summary>
        Close,

        /// <summary>Scaffolding with no structural meaning: a blank line, a comment, a signature.</summary>
        Blank
    }

    /// <summary>What a statement does to control flow, as far as a rewriting pass needs to know.</summary>
    internal enum CSharpControl
    {
        None,

        /// <summary>An unconditional jump to <see cref="CSharpNode.Target"/>.</summary>
        Goto,

        /// <summary>A jump to <see cref="CSharpNode.Target"/> when its condition holds.</summary>
        ConditionalGoto,

        /// <summary>A return, whose value is in <see cref="CSharpNode.Value"/> when it has one.</summary>
        Return,

        /// <summary>A throw, or anything else control does not come back from.</summary>
        Throw
    }

    /// <summary>
    /// One line of a projection before it is rendered: what it says, and enough about what it
    /// does for a pass to rewrite it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The projector could render straight to <see cref="ClrCSharpLine"/>, and did. Structuring
    /// needs more than text: to turn a bottom-tested jump into a <c>while</c>, or a diamond of
    /// assignments into a conditional expression, a pass has to know which statement assigns
    /// what, which one branches where, and how many jumps land on each label. That is what this
    /// carries alongside the tokens.
    /// </para>
    /// <para>
    /// The list of these stays flat rather than becoming a tree; nesting is
    /// <see cref="Depth"/>, with <see cref="CSharpNodeKind.Open"/> and
    /// <see cref="CSharpNodeKind.Close"/> marking where it changes. A flat list is what makes
    /// the passes readable: every one of them is a search for a run of adjacent nodes.
    /// </para>
    /// </remarks>
    internal sealed class CSharpNode
    {
        public CSharpNodeKind Kind { get; set; } = CSharpNodeKind.Statement;

        /// <summary>The IL offset this line begins at, which is also its label.</summary>
        public int? Offset { get; set; }

        /// <summary>How deeply nested the line is.</summary>
        public int Depth { get; set; }

        public List<ClrCSharpToken> Tokens { get; set; } = new List<ClrCSharpToken>();

        /// <summary>The IL the line came from, as text, for the trailing comment.</summary>
        public string Comment { get; set; }

        public CSharpControl Control { get; set; }

        /// <summary>Where this branches to, for a goto or a conditional goto.</summary>
        public int? Target { get; set; }

        /// <summary>A conditional goto's condition.</summary>
        public List<ClrCSharpToken> Condition { get; set; }

        /// <summary>An assignment's right-hand side, or a return's value.</summary>
        public List<ClrCSharpToken> Value { get; set; }

        /// <summary>The name an assignment assigns to, when it assigns to a plain name.</summary>
        public string AssignedName { get; set; }

        /// <summary>
        /// True when no pass may rewrite or remove this line - a switch's cases, whose shape the
        /// passes do not model, and anything an exception region depends on being where it is.
        /// </summary>
        public bool IsFixed { get; set; }

        /// <summary>
        /// The names this line reads, once each per mention.
        /// </summary>
        /// <remarks>
        /// An assignment reads its value and not the name it assigns to. Everything else reads
        /// whatever it says, and <see cref="Tokens"/> is what it says - the condition and value
        /// lists hold the same token instances, so counting those as well would count twice.
        /// </remarks>
        public IEnumerable<string> ReadNames
        {
            get
            {
                var read = this.AssignedName != null
                    ? this.Value ?? Enumerable.Empty<ClrCSharpToken>()
                    : this.Tokens;

                return read
                    .Where(token => token.Kind == ClrCSharpTokenKind.Identifier)
                    .Select(token => token.Text);
            }
        }

        public ClrCSharpLine ToLine()
        {
            return new ClrCSharpLine(this.Offset, this.Depth, this.Tokens, this.Comment);
        }

        public override string ToString() => this.ToLine().ToString();
    }
}
