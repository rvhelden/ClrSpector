using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ClrSpector
{
    /// <summary>
    /// What a piece of projected C# is, so a dump can colour it. The kinds are about role
    /// rather than syntax - a type name and a field name are coloured alike because a reader
    /// skims for "something outside this method", not for a grammar production.
    /// </summary>
    public enum ClrCSharpTokenKind
    {
        /// <summary>An IL offset, printed as the statement's label.</summary>
        Offset,

        /// <summary>A keyword that is not control flow: <c>new</c>, <c>int</c>, <c>null</c>.</summary>
        Keyword,

        /// <summary>A keyword that moves control: <c>goto</c>, <c>if</c>, <c>return</c>, <c>throw</c>.</summary>
        ControlKeyword,

        /// <summary>A type name.</summary>
        Type,

        /// <summary>A field, property or other member name.</summary>
        Member,

        /// <summary>A method being called.</summary>
        Call,

        /// <summary>A string or character literal.</summary>
        Literal,

        /// <summary>A numeric literal.</summary>
        Number,

        /// <summary>A local, argument or synthesised temporary.</summary>
        Identifier,

        /// <summary>Operators, brackets and separators.</summary>
        Punctuation,

        /// <summary>A trailing comment - most often the IL the statement came from.</summary>
        Comment
    }

    /// <summary>How much of the compiler's work a projection tries to undo.</summary>
    public enum ClrCSharpForm
    {
        /// <summary>
        /// One statement per group of instructions, with the control flow left exactly as the
        /// IL has it: branches as <c>goto</c>, every statement labelled with its offset. Nothing
        /// is inferred, so nothing can be inferred wrongly.
        /// </summary>
        Faithful,

        /// <summary>
        /// The same projection with the compiler's scaffolding recognised and undone: its
        /// temporaries folded into the expressions they carry, its conditional jumps back into
        /// conditional expressions, its bottom-tested jumps back into loops. Every rewrite is a
        /// pattern the projection can prove; a shape it cannot prove keeps its gotos.
        /// </summary>
        Structured
    }

    /// <summary>One coloured run of text in a projected C# line.</summary>
    public sealed class ClrCSharpToken
    {
        internal ClrCSharpToken(ClrCSharpTokenKind kind, string text)
        {
            this.Kind = kind;
            this.Text = text;
        }

        public ClrCSharpTokenKind Kind { get; }

        public string Text { get; }

        public override string ToString() => this.Text;
    }

    /// <summary>
    /// One line of projected C#: the IL offset it starts at, how deeply it is nested, the code
    /// itself as coloured tokens, and the IL it was produced from.
    /// </summary>
    /// <remarks>
    /// The tokens are kept rather than a finished string because the same line has to be
    /// renderable plain or coloured, and because padding has to be measured against the
    /// uncoloured width - an ANSI escape takes space in a string but none on screen.
    /// </remarks>
    public sealed class ClrCSharpLine
    {
        /// <summary>The column the trailing IL comment starts at, when there is room for it.</summary>
        private const int CommentColumn = 52;

        internal ClrCSharpLine(int? offset, int indent, IReadOnlyList<ClrCSharpToken> tokens, string comment)
        {
            this.Offset = offset;
            this.Indent = indent;
            this.Tokens = tokens;
            this.Comment = comment;
        }

        /// <summary>
        /// The IL offset this line begins at, or null for scaffolding the IL did not produce -
        /// the signature, the braces, the local declarations.
        /// </summary>
        public int? Offset { get; }

        /// <summary>How many levels of nesting to indent by.</summary>
        public int Indent { get; }

        public IReadOnlyList<ClrCSharpToken> Tokens { get; }

        /// <summary>The IL this line came from, without the leading <c>//</c>.</summary>
        public string Comment { get; }

        /// <summary>True when the line carries a label a <c>goto</c> can name.</summary>
        public bool IsLabelled => this.Offset.HasValue;

        public override string ToString() => this.Render(false);

        /// <summary>Renders the line, optionally with ANSI colour.</summary>
        public string Render(bool colouring)
        {
            var text = new StringBuilder();

            // The offset doubles as the statement's label, which is what makes the projected
            // gotos resolve: every branch target is an offset, and every offset is printed here.
            var label = this.Offset.HasValue ? $"IL_{this.Offset.Value:x4}:" : string.Empty;

            text.Append(IlPalette.Paint(label, IlPalette.ColourFor(ClrCSharpTokenKind.Offset), colouring));
            text.Append(new string(' ', Math.Max(1, 10 - label.Length)));
            text.Append(new string(' ', this.Indent * 4));

            foreach (var token in this.Tokens)
                text.Append(IlPalette.Paint(token.Text, IlPalette.ColourFor(token.Kind), colouring));

            if (string.IsNullOrEmpty(this.Comment))
                return text.ToString().TrimEnd();

            var width = Math.Max(1, 10 - label.Length) + label.Length + this.Indent * 4
                        + this.Tokens.Sum(t => t.Text.Length);

            text.Append(new string(' ', Math.Max(1, CommentColumn - width)));
            text.Append(IlPalette.Paint(
                "// " + this.Comment, IlPalette.ColourFor(ClrCSharpTokenKind.Comment), colouring));

            return text.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// A method's IL rewritten as low-level C#: one statement per group of instructions, with
    /// the evaluation stack turned back into expressions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What it always does is undo the stack machine: <c>ldloc.0; ldc.i4.1; add; stloc.0</c>
    /// reads as <c>loc0 = loc0 + 1;</c>, which is the part of IL that is genuinely hard to read
    /// by eye. How much further it goes is <see cref="ClrCSharpForm"/>.
    /// </para>
    /// <para>
    /// <see cref="ClrCSharpForm.Faithful"/>, the default, stops there: control flow is left
    /// exactly as the IL has it, branches become <c>goto</c>, and every statement is labelled
    /// with the offset it starts at. <see cref="ClrCSharpForm.Structured"/> goes on to undo the
    /// compiler's scaffolding - its temporaries, its conditional jumps, its bottom-tested loops -
    /// but only where it can prove the shape it is rewriting, so a method it cannot prove keeps
    /// its gotos rather than being guessed at.
    /// </para>
    /// <para>
    /// Names are never invented, but they are read where they exist. A local is called what the
    /// source called it when the module's PDB can be found - see
    /// <see cref="ClrModuleSymbols"/> - and <c>loc0</c> when it cannot, which is the only thing
    /// the method's own data says about it.
    /// </para>
    /// <para>
    /// The output is a reading aid and will not compile. Both sources of IL project the same
    /// shape: the signature and locals are typed, and the try, catch, filter and finally blocks
    /// are there. What differs is where that came from - reflection's own metadata, or, for IL
    /// read from a MethodDesc, the module's tables: the local signature decoded through
    /// <see cref="ClrModuleMetadata.LocalSignature"/>, the handlers out of the body's data
    /// sections, and every operand named from the string heap. A slot whose signature will not
    /// decode is declared untyped rather than guessed at.
    /// </para>
    /// <para>
    /// Statements carry the IL they came from as a trailing comment, so nothing is hidden by the
    /// projection: the <c>.ovf</c> and <c>.un</c> forms an operator cannot express, the prefixes,
    /// and the instructions that were folded into an expression are all still named.
    /// </para>
    /// </remarks>
    public sealed class ClrMethodCSharp
    {
        internal ClrMethodCSharp(
            ClrMethodIl il, IReadOnlyList<ClrCSharpLine> lines, bool isExact, ClrCSharpForm form)
        {
            this.Il = il;
            this.Lines = lines;
            this.IsExact = isExact;
            this.Form = form;
        }

        /// <summary>The IL this was projected from.</summary>
        public ClrMethodIl Il { get; }

        /// <summary>Which form this was projected in.</summary>
        public ClrCSharpForm Form { get; }

        public IReadOnlyList<ClrCSharpLine> Lines { get; }

        /// <summary>
        /// True when every instruction was modelled and the evaluation stack stayed in step.
        /// </summary>
        /// <remarks>
        /// False means some line is a comment standing in for an instruction this does not
        /// model, or a call whose argument count could not be established - and that from there
        /// on the statements around it are not to be trusted. The IL dump is then the honest
        /// listing; see <see cref="ClrMethodIl.Dump(IlDumpStyle)"/>.
        /// </remarks>
        public bool IsExact { get; }

        /// <summary>Projects <paramref name="il"/>, or null when <paramref name="il"/> is null.</summary>
        public static ClrMethodCSharp Of(ClrMethodIl il, ClrCSharpForm form = ClrCSharpForm.Faithful)
        {
            return il == null ? null : new CSharpProjector(il, form).Project();
        }

        /// <summary>
        /// Projects <paramref name="method"/>'s IL, or null when it has no body.
        /// </summary>
        public static ClrMethodCSharp Of(MethodBase method, ClrCSharpForm form = ClrCSharpForm.Faithful)
        {
            return Of(ClrMethodIl.Of(method), form);
        }

        /// <summary>
        /// Projects the IL read from <paramref name="method"/>'s module image, or null when it
        /// has no body.
        /// </summary>
        public static ClrMethodCSharp Of(
            ClrMethodDescription method, ClrCSharpForm form = ClrCSharpForm.Faithful)
        {
            return Of(ClrMethodIl.Of(method), form);
        }

        /// <summary>The projection as plain text.</summary>
        public string Dump() => this.Dump(IlDumpStyle.Plain);

        /// <summary>
        /// The projection as text, coloured the same way and under the same rules as an IL dump.
        /// </summary>
        public string Dump(IlDumpStyle style)
        {
            var colouring = IlPalette.IsColouring(style);
            var text = new StringBuilder();

            foreach (var line in this.Lines)
                text.AppendLine(line.Render(colouring));

            return text.ToString().TrimEnd();
        }

        public override string ToString()
        {
            return $"{this.Lines.Count} lines of {this.Form.ToString().ToLowerInvariant()} C# " +
                   $"from {this.Il.Instructions.Count} instructions" +
                   (this.IsExact ? string.Empty : " (approximate)");
        }
    }
}
