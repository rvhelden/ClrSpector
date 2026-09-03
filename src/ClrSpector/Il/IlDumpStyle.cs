using System;
using System.Reflection.Emit;

namespace ClrSpector
{
    /// <summary>How an IL dump is rendered.</summary>
    public enum IlDumpStyle
    {
        /// <summary>Plain text, safe to write anywhere.</summary>
        Plain,

        /// <summary>ANSI escape sequences, for a terminal that understands them.</summary>
        Ansi,

        /// <summary>
        /// ANSI when the output looks like a terminal that wants it, plain otherwise. See
        /// <see cref="IlPalette.AnsiLooksWanted"/>.
        /// </summary>
        Auto
    }

    /// <summary>
    /// The colours an IL dump uses, and the rules for when colour is wanted at all.
    /// </summary>
    /// <remarks>
    /// Instructions are coloured by what they <i>do</i> rather than by opcode, because that is
    /// what makes a listing skimmable: control flow, calls and the loads that name something
    /// outside the method are the parts worth finding by eye.
    /// </remarks>
    public static class IlPalette
    {
        public const string Reset = "[0m";

        /// <summary>Offsets and other scaffolding - present but not competing for attention.</summary>
        public const string Offset = "[90m";

        /// <summary>Branches, switches and returns: where control goes.</summary>
        public const string ControlFlow = "[33m";

        /// <summary>Calls into other methods.</summary>
        public const string Call = "[36m";

        /// <summary>String literals.</summary>
        public const string Literal = "[32m";

        /// <summary>Types, methods and fields named in an operand.</summary>
        public const string Member = "[36m";

        /// <summary>Numeric operands and local or argument indices.</summary>
        public const string Number = "[35m";

        /// <summary>Everything else - the plain stack and arithmetic opcodes.</summary>
        public const string Opcode = "[37m";

        /// <summary>Directives and comments around the instruction listing.</summary>
        public const string Directive = "[94m";

        public const string Comment = "[90m";

        /// <summary>
        /// Whether ANSI output looks wanted here.
        /// </summary>
        /// <remarks>
        /// Honours <c>NO_COLOR</c>, which is the convention for "never colour, whatever you
        /// think", and treats redirected output as not a terminal - escape sequences in a file
        /// or a pipe are noise, not colour.
        /// </remarks>
        public static bool AnsiLooksWanted
        {
            get
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
                    return false;

                try
                {
                    return !Console.IsOutputRedirected;
                }
                catch (Exception)
                {
                    // No console at all - a test host or a service. Colour would go nowhere good.
                    return false;
                }
            }
        }

        /// <summary>The colour an opcode should be shown in.</summary>
        public static string ColourFor(OpCode opCode)
        {
            switch (opCode.FlowControl)
            {
                case FlowControl.Branch:
                case FlowControl.Cond_Branch:
                case FlowControl.Return:
                case FlowControl.Throw:
                    return ControlFlow;

                case FlowControl.Call:
                    return Call;
            }

            switch (opCode.OperandType)
            {
                case OperandType.InlineString:
                    return Literal;

                case OperandType.InlineType:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                    return Member;

                default:
                    return Opcode;
            }
        }

        /// <summary>
        /// The colour a projected C# token should be shown in.
        /// </summary>
        /// <remarks>
        /// The C# projection deliberately borrows this palette rather than defining one: the two
        /// dumps are read side by side, so an operand named in the IL listing and the same
        /// operand named in the C# has to be the same colour or the eye stops matching them up.
        /// </remarks>
        public static string ColourFor(ClrCSharpTokenKind kind)
        {
            switch (kind)
            {
                case ClrCSharpTokenKind.Offset:
                    return Offset;

                case ClrCSharpTokenKind.ControlKeyword:
                    return ControlFlow;

                case ClrCSharpTokenKind.Keyword:
                    return Directive;

                case ClrCSharpTokenKind.Type:
                case ClrCSharpTokenKind.Member:
                    return Member;

                case ClrCSharpTokenKind.Call:
                    return Call;

                case ClrCSharpTokenKind.Literal:
                    return Literal;

                case ClrCSharpTokenKind.Number:
                    return Number;

                case ClrCSharpTokenKind.Comment:
                    return Comment;

                default:
                    // Identifiers and punctuation are left in the terminal's own foreground.
                    // Painting them too would put an escape sequence around every bracket and
                    // say nothing: what colour is for here is picking out the parts that name
                    // something outside the statement.
                    return null;
            }
        }

        /// <summary>Wraps <paramref name="text"/> in <paramref name="colour"/> when colouring.</summary>
        internal static string Paint(string text, string colour, bool colouring)
        {
            return colouring && text.Length > 0 && !string.IsNullOrEmpty(colour)
                ? colour + text + Reset
                : text;
        }

        /// <summary>Resolves <see cref="IlDumpStyle.Auto"/> to a definite answer.</summary>
        internal static bool IsColouring(IlDumpStyle style)
        {
            switch (style)
            {
                case IlDumpStyle.Ansi:
                    return true;

                case IlDumpStyle.Auto:
                    return AnsiLooksWanted;

                default:
                    return false;
            }
        }
    }
}
