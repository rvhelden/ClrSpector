using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ClrSpector
{
    /// <summary>
    /// Turns one method's IL into the lines of a <see cref="ClrMethodCSharp"/> by running the
    /// evaluation stack symbolically: each instruction pops expressions and pushes the
    /// expression it makes of them, and a statement is emitted whenever a value is consumed by
    /// something that is not an expression - a store, a branch, a call that returns nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Folding expressions is only sound while nothing changes underneath them. An expression
    /// sitting unevaluated on the stack is therefore spilled into a temporary before any
    /// statement that could change what it would read: a store to a local it reads, a write to
    /// memory when it reads memory, or a call, which could change anything. That is what keeps
    /// <c>ldloc.0; ldc.i4.5; stloc.0; ldloc.0; add</c> from folding into <c>loc0 + loc0</c>
    /// after <c>loc0</c> has already been overwritten.
    /// </para>
    /// <para>
    /// Anything still on the stack at a branch or a branch target is spilled too, into slots
    /// named <c>st0</c> upwards, because the projection has no dataflow across blocks - it walks
    /// the instructions once, in order. Real compiler output almost never carries a stack across
    /// a block boundary, so those slots rarely appear.
    /// </para>
    /// </remarks>
    internal sealed class CSharpProjector
    {
        // C#'s binding strength, as far as this needs it: an operand is parenthesised when its
        // own expression binds more loosely than the position it is being put into.
        private const int Primary = 14;
        private const int Unary = 13;
        private const int Multiplicative = 12;
        private const int Additive = 11;
        private const int Shift = 10;
        private const int Relational = 9;
        private const int Equality = 8;
        private const int BitAnd = 7;
        private const int BitXor = 6;
        private const int BitOr = 5;

        // MethodAttributes, which is what source's keywords compile into.
        private const ushort MemberAccessMask = 0x0007;
        private const ushort StaticFlag = 0x0010;
        private const ushort FinalFlag = 0x0020;
        private const ushort VirtualFlag = 0x0040;
        private const ushort NewSlotFlag = 0x0100;
        private const ushort AbstractFlag = 0x0400;
        private const ushort PInvokeFlag = 0x2000;

        /// <summary>A signature's calling convention bit for a method that has a <c>this</c>.</summary>
        private const byte HasThisConvention = 0x20;

        /// <summary>The calling convention bit that says a generic parameter count follows.</summary>
        private const byte GenericConvention = 0x10;

        private readonly ClrMethodIl il;

        private readonly ClrCSharpForm form;

        /// <summary>
        /// The offsets the exception table names, which control reaches without any jump saying
        /// so - so a structuring pass may not fold them away.
        /// </summary>
        private readonly HashSet<int> handlerOffsets = new HashSet<int>();

        /// <summary>
        /// The names still mentioned once the passes have run, or null when everything declared
        /// is to be declared - which is the faithful form, where nothing was folded away.
        /// </summary>
        private HashSet<string> used;

        private readonly List<CSharpNode> body = new List<CSharpNode>();

        private readonly List<Slot> stack = new List<Slot>();

        /// <summary>Offsets something branches to, or an exception region starts or ends at.</summary>
        private readonly HashSet<int> boundaries = new HashSet<int>();

        /// <summary>How deep the stack was when control last left for a given offset.</summary>
        private readonly Dictionary<int, int> incomingDepth = new Dictionary<int, int>();

        /// <summary>Exception regions currently open, innermost last.</summary>
        private readonly Stack<int> openRegions = new Stack<int>();

        /// <summary>Prefix instructions waiting to be attributed to the statement they modify.</summary>
        private readonly List<ClrIlInstruction> prefixes = new List<ClrIlInstruction>();

        private string[] argumentNames;

        /// <summary>The names of the method's own generic parameters, by position.</summary>
        private IReadOnlyList<string> methodTypeParameters = Array.Empty<string>();

        /// <summary>The names of the declaring type's generic parameters, by position.</summary>
        private IReadOnlyList<string> typeTypeParameters = Array.Empty<string>();

        /// <summary>The name of the exception the handler being entered was given, if any.</summary>
        private string pendingException;

        private int handlers;

        /// <summary>The exception name given to each handler, so a filter and its catch share one.</summary>
        private readonly Dictionary<ClrIlExceptionRegion, string> exceptionNames =
            new Dictionary<ClrIlExceptionRegion, string>();

        private ClrMethodSignature signature;

        private int indent = 1;

        private int temporaries;

        private int spillSlots;

        private int highestLocal = -1;

        /// <summary>The offset the last emitted label named, so labels only ever move forwards.</summary>
        private int lastLabel;

        private bool exact = true;

        internal CSharpProjector(ClrMethodIl il, ClrCSharpForm form = ClrCSharpForm.Faithful)
        {
            this.il = il;
            this.form = form;
        }

        /// <summary>What a projected expression's type is known to be, if anything.</summary>
        private enum Flavour
        {
            Unknown,

            /// <summary>A bool, or a comparison that produced one.</summary>
            Boolean,

            /// <summary>Something that can be compared to null.</summary>
            Reference
        }

        /// <summary>One expression on the symbolic evaluation stack.</summary>
        private sealed class Slot
        {
            public List<ClrCSharpToken> Tokens { get; } = new List<ClrCSharpToken>();

            /// <summary>How tightly the expression binds, for parenthesising.</summary>
            public int Precedence { get; set; } = Primary;

            /// <summary>True when evaluating it does something - a call, or a stack allocation.</summary>
            public bool HasSideEffects { get; set; }

            /// <summary>True when it reads memory a later write could change.</summary>
            public bool ReadsMemory { get; set; }

            /// <summary>
            /// What kind of value it is, as far as anything knows. This is only ever used to
            /// write a branch on it the way source would: <c>brtrue</c> is <c>if (x)</c> on a
            /// bool, <c>if (x != null)</c> on a reference and <c>if (x != 0)</c> on a number,
            /// and guessing wrong there is the difference between readable and misleading.
            /// </summary>
            public Flavour Flavour { get; set; }

            /// <summary>True when the expression is already a comparison.</summary>
            public bool IsCondition => this.Flavour == Flavour.Boolean;

            /// <summary>The local slots it reads, which a store to any of them invalidates.</summary>
            public HashSet<int> LocalsRead { get; } = new HashSet<int>();

            /// <summary>The instructions folded into it, in order, for the trailing comment.</summary>
            public List<ClrIlInstruction> Source { get; } = new List<ClrIlInstruction>();

            /// <summary>
            /// What this is the address of, when it is an address: the tokens for the place
            /// itself, without the <c>&amp;</c>.
            /// </summary>
            /// <remarks>
            /// Calling a value type's method needs its address, so the IL takes one where the
            /// source called a method on a field or a local. Keeping the place lets the call be
            /// written the way it was written, rather than as a call on an address.
            /// </remarks>
            public List<ClrCSharpToken> AddressedPlace { get; set; }

            /// <summary>True for a bare name or literal, which never needs spilling.</summary>
            public bool IsAtom => this.Tokens.Count == 1 && !this.HasSideEffects;
        }

        internal ClrMethodCSharp Project()
        {
            this.ReadSignature();
            this.FindBoundaries();

            foreach (var instruction in this.il.Instructions)
            {
                this.EnterOffset(instruction.Offset);
                this.Translate(instruction);
            }

            this.CloseRegionsUpTo(int.MaxValue);

            if (this.form == ClrCSharpForm.Structured)
            {
                var declared = CSharpStructurer.Apply(
                    this.body, this.handlerOffsets, this.LocalTypeOf, this.IsSourceNamed);

                // Declarations come after the passes, because a slot the passes folded into the
                // statement that read it is no longer a variable the method has.
                this.used = new HashSet<string>(this.body.SelectMany(Names));
                this.used.ExceptWith(declared);
            }

            var nodes = new List<CSharpNode>();
            nodes.AddRange(this.Header());
            nodes.AddRange(this.Declarations());
            nodes.AddRange(this.body);
            nodes.Add(this.Scaffold(0, Punctuation("}")));

            return new ClrMethodCSharp(
                this.il, nodes.Select(node => node.ToLine()).ToArray(), this.exact, this.form);
        }

        // ---------- the method's own shape ----------

        /// <summary>
        /// Works out what the arguments are called, from whichever source the IL came from.
        /// </summary>
        /// <remarks>
        /// Reflection has the real parameter names; a MethodDesc has them too, through the Param
        /// table its signature reads. With neither, arguments keep their IL numbering, which is
        /// what the instructions name them by anyway.
        /// </remarks>
        private void ReadSignature()
        {
            this.ReadTypeParameters();

            var method = this.il.Method;

            if (method != null)
            {
                var parameters = method.GetParameters();
                var names = new List<string>();

                if (!method.IsStatic)
                    names.Add("this");

                for (var i = 0; i < parameters.Length; i++)
                    names.Add(string.IsNullOrEmpty(parameters[i].Name) ? $"arg{i}" : parameters[i].Name);

                this.argumentNames = names.ToArray();

                return;
            }

            this.signature = this.il.Description?.Signature;

            if (this.signature != null)
            {
                var names = new List<string>();

                if (this.signature.HasThis)
                    names.Add("this");

                for (var i = 0; i < this.signature.Parameters.Count; i++)
                {
                    var parameter = this.signature.Parameters[i];

                    names.Add(string.IsNullOrEmpty(parameter.Name) ? $"arg{i}" : parameter.Name);
                }

                this.argumentNames = names.ToArray();

                return;
            }

            this.argumentNames = Array.Empty<string>();
        }

        /// <summary>
        /// Finds what the generic parameters are called, so a signature that refers to them by
        /// position can be written the way source wrote it.
        /// </summary>
        /// <remarks>
        /// A signature holds no names for them: a method's parameter is <c>!!0</c> and a type's
        /// is <c>!0</c>, by position. Reflection has the names on the type arguments; a
        /// MethodDesc has them in the module's GenericParam table.
        /// </remarks>
        private void ReadTypeParameters()
        {
            var method = this.il.Method;

            if (method != null)
            {
                try
                {
                    this.methodTypeParameters = method.IsGenericMethodDefinition || method.IsGenericMethod
                        ? method.GetGenericArguments().Select(argument => argument.Name).ToList()
                        : (IReadOnlyList<string>)Array.Empty<string>();

                    var declaring = method.DeclaringType;

                    this.typeTypeParameters = declaring != null && declaring.IsGenericType
                        ? declaring.GetGenericArguments().Select(argument => argument.Name).ToList()
                        : Array.Empty<string>();
                }
                catch (Exception)
                {
                    // A signature by position still reads; it just reads as a position.
                }

                return;
            }

            var metadata = this.il.Description?.Metadata;

            if (metadata == null)
                return;

            this.methodTypeParameters = metadata.GenericParameterNames((int)this.il.Description.MetadataToken);

            var table = this.il.Description.DeclaringMethodTable;

            if (table != null)
                this.typeTypeParameters = metadata.GenericParameterNames((int)table.TypeDefToken);
        }

        /// <summary>
        /// Replaces the positions a signature refers to its generic parameters by with their
        /// names: <c>!!0</c> with the method's first, <c>!0</c> with the type's.
        /// </summary>
        private string Named(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('!') < 0)
                return text;

            var named = new StringBuilder(text.Length);

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '!')
                {
                    named.Append(text[i]);

                    continue;
                }

                var method = i + 1 < text.Length && text[i + 1] == '!';
                var digits = i + (method ? 2 : 1);
                var end = digits;

                while (end < text.Length && char.IsDigit(text[end]))
                    end++;

                if (end == digits || !int.TryParse(text.Substring(digits, end - digits), out var position))
                {
                    named.Append(text[i]);

                    continue;
                }

                var names = method ? this.methodTypeParameters : this.typeTypeParameters;

                named.Append(position < names.Count && !string.IsNullOrEmpty(names[position])
                    ? names[position]
                    : text.Substring(i, end - i));

                i = end - 1;
            }

            return named.ToString();
        }

        private IEnumerable<CSharpNode> Header()
        {
            // The first line is the IL dump's own header, unchanged, so the two listings can be
            // lined up next to each other.
            yield return this.Scaffold(0, Comment(this.DescribeMethod()));

            // The attributes the method was declared with, one per line as source writes them.
            foreach (var attribute in this.il.Attributes)
                yield return this.Scaffold(0, Attribute(attribute));

            var tokens = new List<ClrCSharpToken>();

            foreach (var modifier in this.Modifiers())
            {
                tokens.Add(Keyword(modifier));
                tokens.Add(Punctuation(" "));
            }

            tokens.Add(Type(this.ReturnTypeText()));
            tokens.Add(Punctuation(" "));
            tokens.Add(CallName(this.MethodName()));

            // The method's own type parameters, which its signature refers to by position.
            if (this.methodTypeParameters.Count > 0)
            {
                tokens.Add(Punctuation("<"));
                tokens.Add(Type(string.Join(", ", this.methodTypeParameters)));
                tokens.Add(Punctuation(">"));
            }

            tokens.Add(Punctuation("("));

            var first = true;

            foreach (var parameter in this.ParameterText())
            {
                if (!first)
                    tokens.Add(Punctuation(", "));

                first = false;
                tokens.Add(Type(parameter.Type));
                tokens.Add(Punctuation(" "));
                tokens.Add(Identifier(parameter.Name));
            }

            tokens.Add(Punctuation(")"));

            yield return this.Scaffold(0, tokens.ToArray());

            // The constraints, which are the reason some of the calls above are calls at all.
            foreach (var constraint in this.Constraints())
                yield return this.Scaffold(1, constraint);

            yield return this.Scaffold(0, Punctuation("{"));
        }

        /// <summary>
        /// One attribute, tokenised so a dump can colour it.
        /// </summary>
        /// <remarks>
        /// The arguments arrive already written the way source writes them, so they are coloured
        /// by what they look like rather than by a type the projection would have to re-derive:
        /// a quote makes a literal, a digit or a sign makes a number, and everything else is a
        /// name of something.
        /// </remarks>
        private static IEnumerable<ClrCSharpToken> Attribute(ClrCustomAttribute attribute)
        {
            var tokens = new List<ClrCSharpToken> { Punctuation("["), Type(attribute.ShortName) };
            var arguments = attribute.Arguments;

            if (arguments.Count > 0)
            {
                tokens.Add(Punctuation("("));

                for (var i = 0; i < arguments.Count; i++)
                {
                    if (i > 0)
                        tokens.Add(Punctuation(", "));

                    tokens.AddRange(ArgumentTokens(arguments[i]));
                }

                tokens.Add(Punctuation(")"));
            }

            tokens.Add(Punctuation("]"));

            // An attribute that could not be decoded to the end says so where it is written,
            // rather than looking like one that had nothing more to say.
            if (!attribute.IsComplete)
                tokens.Add(Comment($"  /* {attribute.DecodeError} */"));

            return tokens;
        }

        private static IEnumerable<ClrCSharpToken> ArgumentTokens(string argument)
        {
            if (string.IsNullOrEmpty(argument))
                return new[] { Punctuation(string.Empty) };

            // A named argument is a name, an equals and a value; the value is the part worth
            // colouring and shortening, since the name is already only a name.
            var equals = argument.IndexOf(" = ", StringComparison.Ordinal);

            if (equals > 0)
            {
                return new[] { MemberToken(argument.Substring(0, equals)), Punctuation(" = ") }
                    .Concat(ArgumentTokens(argument.Substring(equals + 3)));
            }

            if (argument[0] == '"' || argument[0] == '\'')
                return new[] { Literal(argument) };

            return new[]
            {
                char.IsDigit(argument[0]) || argument[0] == '-'
                    ? Number(argument)
                    : MemberToken(ShortenQualified(argument))
            };
        }

        /// <summary>
        /// Drops the namespace from a qualified member reference, keeping the type that names it.
        /// </summary>
        /// <remarks>
        /// An enum argument is decoded fully qualified, and source wrote <c>AuditLevel.Full</c>
        /// rather than <c>Some.Namespace.AuditLevel.Full</c> - so the last two segments are what
        /// is kept, being the type and the member. Shortening to one would drop the type name
        /// the source did write.
        /// </remarks>
        private static string ShortenQualified(string argument)
        {
            var segments = argument.Split('.');

            return segments.Length <= 2
                ? argument
                : string.Join(".", segments.Skip(segments.Length - 2));
        }

        /// <summary>
        /// The keywords the method was declared with, in the order C# writes them.
        /// </summary>
        /// <remarks>
        /// All of it is in the MethodDef row's flags. Whether a virtual method is
        /// <c>virtual</c> or <c>override</c> is the new-slot bit: a method that takes a slot of
        /// its own introduces the virtual, and one that reuses a slot is overriding whatever
        /// declared it.
        /// </remarks>
        private IEnumerable<string> Modifiers()
        {
            var flags = this.il.DeclarationFlags;

            if (flags == 0)
            {
                // No row to read - a dynamic method, or IL from somewhere with no metadata.
                if (this.IsStatic())
                    yield return "static";

                yield break;
            }

            switch (flags & MemberAccessMask)
            {
                case 1: yield return "private"; break;
                case 2: yield return "private protected"; break;
                case 3: yield return "internal"; break;
                case 4: yield return "protected"; break;
                case 5: yield return "protected internal"; break;
                case 6: yield return "public"; break;
            }

            if ((flags & StaticFlag) != 0)
                yield return "static";

            var reusesSlot = (flags & NewSlotFlag) == 0;

            if ((flags & AbstractFlag) != 0)
            {
                yield return "abstract";

                if (reusesSlot)
                    yield return "override";
            }
            else if ((flags & VirtualFlag) != 0)
            {
                if (!reusesSlot)
                {
                    yield return "virtual";
                }
                else
                {
                    if ((flags & FinalFlag) != 0)
                        yield return "sealed";

                    yield return "override";
                }
            }

            if ((flags & PInvokeFlag) != 0)
                yield return "extern";
        }

        /// <summary>
        /// Rewrites a zero or a one being stored somewhere that holds a bool as
        /// <c>false</c> or <c>true</c>.
        /// </summary>
        /// <remarks>
        /// A bool is an integer in IL: <c>ldc.i4.1; stloc.0</c> is what <c>flag = true</c>
        /// compiles to, and the only thing that knows the difference is the type of the slot
        /// being written. Doing it here rather than at the return means the value is already
        /// spelled right wherever it ends up - including in a return the structuring passes
        /// build later out of the assignment.
        /// </remarks>
        private void SpellBooleanLiteral(Slot value, Flavour destination)
        {
            if (destination != Flavour.Boolean || value.Tokens.Count != 1)
                return;

            var literal = value.Tokens[0];

            if (literal.Kind != ClrCSharpTokenKind.Number || literal.Text != "0" && literal.Text != "1")
                return;

            value.Tokens[0] = Keyword(literal.Text == "0" ? "false" : "true");
            value.Flavour = Flavour.Boolean;
        }

        /// <summary>
        /// The tokens for a returned value, with a bool spelled the way source spells one.
        /// </summary>
        /// <remarks>
        /// A bool is an integer in IL, so returning true is <c>ldc.i4.1</c> and comes back as
        /// <c>return 1;</c> from a method whose signature says bool. The signature is the thing
        /// that knows better, so it decides.
        /// </remarks>
        private List<ClrCSharpToken> Returned(Slot value)
        {
            if (this.ReturnTypeText() != "bool" || value.Tokens.Count != 1)
                return value.Tokens;

            var literal = value.Tokens[0];

            if (literal.Kind != ClrCSharpTokenKind.Number || literal.Text != "0" && literal.Text != "1")
                return value.Tokens;

            return new List<ClrCSharpToken> { Keyword(literal.Text == "0" ? "false" : "true") };
        }

        /// <summary>
        /// A <c>where</c> clause per constrained type parameter, as source wrote them.
        /// </summary>
        /// <remarks>
        /// Only the method's own parameters: a constraint on the declaring type belongs to the
        /// type, and a listing of one method should not claim it.
        /// </remarks>
        private IEnumerable<ClrCSharpToken[]> Constraints()
        {
            if (this.il.Metadata == null || this.il.MetadataToken == 0)
                yield break;

            foreach (var parameter in this.il.Metadata.GenericParameters((int)this.il.MetadataToken))
            {
                var described = parameter.ToString();

                if (described == parameter.Name)
                    continue;

                yield return new[]
                {
                    Keyword("where"),
                    Punctuation(" "),
                    Type(this.Named(CSharpNames.ShortenAll(described)))
                };
            }
        }

        private bool IsStatic()
        {
            if (this.il.Method != null)
                return this.il.Method.IsStatic;

            return this.signature != null && !this.signature.HasThis;
        }

        private string MethodName()
        {
            return this.il.Method?.Name ?? this.il.Description?.Name ?? "<method>";
        }

        private string DescribeMethod()
        {
            if (this.il.Method != null)
                return $"{this.il.Method.DeclaringType?.FullName}::{this.il.Method.Name}";

            if (this.il.Description != null)
                return $"{this.il.Description.DeclaringTypeName}::{this.il.Description.Name}";

            return "<unnamed>";
        }

        private string ReturnTypeText()
        {
            if (this.il.Method is MethodInfo info)
                return this.Named(CSharpNames.Of(info.ReturnType));

            if (this.il.Method is ConstructorInfo)
                return "void";

            return this.signature == null ? "var" : this.Named(CSharpNames.Of(this.signature.ReturnType));
        }

        private IEnumerable<(string Type, string Name)> ParameterText()
        {
            var offset = this.IsStatic() ? 0 : 1;

            if (this.il.Method != null)
            {
                foreach (var parameter in this.il.Method.GetParameters())
                {
                    yield return (this.Named(CSharpNames.Of(parameter.ParameterType)),
                        this.ArgumentName(parameter.Position + offset));
                }

                yield break;
            }

            if (this.signature == null)
                yield break;

            foreach (var parameter in this.signature.Parameters)
            {
                yield return (this.Named(CSharpNames.Of(parameter.Type)),
                    this.ArgumentName(parameter.Index + offset));
            }
        }

        /// <summary>
        /// The locals, the spill slots and the temporaries, declared where C# wants them - which
        /// is why this runs after the body rather than before it.
        /// </summary>
        private IEnumerable<CSharpNode> Declarations()
        {
            var any = false;

            if (this.il.LocalVariables.Count > 0)
            {
                foreach (var local in this.il.LocalVariables)
                {
                    if (!this.IsUsed(local.DisplayName))
                        continue;

                    any = true;

                    // A pinned slot is spelled as a fixed one: it is a pointer the GC has been
                    // told to leave alone, which is what fixed means in source.
                    yield return this.Scaffold(
                        1,
                        Join(
                            local.IsPinned
                                ? new[] { Keyword("fixed"), Punctuation(" ") }
                                : Array.Empty<ClrCSharpToken>(),
                            Type(this.Named(LocalTypeText(local))),
                            Punctuation(" "),
                            Identifier(local.DisplayName),
                            Punctuation(";")));
                }
            }
            else if (this.highestLocal >= 0)
            {
                // No signature could be read - a body with no local signature token, or a module
                // with no readable metadata - so the count is what the instructions used and the
                // types are not known at all.
                yield return this.Scaffold(1, Comment("locals: no signature read, types unknown"));

                for (var i = 0; i <= this.highestLocal; i++)
                {
                    if (!this.IsUsed($"loc{i}"))
                        continue;

                    any = true;

                    yield return this.Scaffold(
                        1, Type("var"), Punctuation(" "), Identifier($"loc{i}"), Punctuation(";"));
                }
            }

            for (var i = 0; i < this.spillSlots; i++)
            {
                if (!this.IsUsed($"st{i}"))
                    continue;

                any = true;

                yield return this.Scaffold(
                    1,
                    Type("var"),
                    Punctuation(" "),
                    Identifier($"st{i}"),
                    Punctuation(";"),
                    Comment("  // evaluation stack carried across a branch"));
            }

            // The blank line separates the declarations from the body, so it only belongs
            // where there were any.
            if (any)
                yield return this.Scaffold(1);
        }

        /// <summary>
        /// The type to declare <paramref name="name"/> as, when it is one of this method's
        /// locals and its type is known; null otherwise.
        /// </summary>
        private string LocalTypeOf(string name)
        {
            for (var i = 0; i < this.il.LocalVariables.Count; i++)
            {
                if (name != this.il.LocalVariables[i].DisplayName)
                    continue;

                var local = this.il.LocalVariables[i];

                return local.IsPinned || local.Type == null && local.SignatureType == null
                    ? null
                    : this.Named(LocalTypeText(local));
            }

            return null;
        }

        /// <summary>
        /// Whether <paramref name="name"/> is a variable the source declared, as opposed to a
        /// slot the compiler needed or one this projection invented.
        /// </summary>
        /// <remarks>
        /// Only a PDB says so, and only for what the source named: a compiler temporary either
        /// has no name at all or one that could not be written in source, which is what the
        /// angle brackets and dollars in it are for.
        /// </remarks>
        private bool IsSourceNamed(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            foreach (var local in this.il.LocalVariables)
            {
                if (local.Name == name)
                    return name.IndexOf('<') < 0 && name.IndexOf('$') < 0;
            }

            return false;
        }

        /// <summary>
        /// Whether <paramref name="name"/> is still mentioned. Everything is, until the passes
        /// have run and said otherwise.
        /// </summary>
        private bool IsUsed(string name) => this.used == null || this.used.Contains(name);

        /// <summary>Every name a line mentions, however it mentions it.</summary>
        private static IEnumerable<string> Names(CSharpNode node)
        {
            return node.Tokens
                .Concat(node.Value ?? Enumerable.Empty<ClrCSharpToken>())
                .Concat(node.Condition ?? Enumerable.Empty<ClrCSharpToken>())
                .Where(token => token.Kind == ClrCSharpTokenKind.Identifier)
                .Select(token => token.Text);
        }

        // ---------- block structure ----------

        /// <summary>
        /// Collects every offset a statement has to start at: branch targets, and the edges of
        /// the exception regions. Folding an expression across one of these would move code from
        /// one block into another.
        /// </summary>
        private void FindBoundaries()
        {
            foreach (var instruction in this.il.Instructions)
            {
                switch (instruction.Operand)
                {
                    case ClrIlBranchTarget branch:
                        this.boundaries.Add(branch.Target);
                        break;

                    case int[] targets:
                        foreach (var target in targets)
                            this.boundaries.Add(target);
                        break;
                }
            }

            foreach (var region in this.il.ExceptionRegions)
            {
                this.boundaries.Add(region.TryOffset);
                this.boundaries.Add(region.TryEnd);
                this.boundaries.Add(region.HandlerOffset);
                this.boundaries.Add(region.HandlerEnd);

                if (region.Kind == ClrIlExceptionRegionKind.Filter)
                    this.boundaries.Add(region.FilterOffset);

                this.handlerOffsets.Add(region.TryOffset);
                this.handlerOffsets.Add(region.TryEnd);
                this.handlerOffsets.Add(region.HandlerOffset);
                this.handlerOffsets.Add(region.HandlerEnd);
                this.handlerOffsets.Add(region.FilterOffset);
            }
        }

        /// <summary>
        /// Handles everything that has to happen before the instruction at
        /// <paramref name="offset"/>: closing and opening exception blocks, and getting the
        /// symbolic stack into the state the block is entered with.
        /// </summary>
        private void EnterOffset(int offset)
        {
            if (!this.boundaries.Contains(offset))
                return;

            // Whatever is still stacked belongs to the block being left, so it is written out
            // before the boundary rather than folded across it.
            this.SpillStackToSlots(offset);

            this.CloseRegionsUpTo(offset);
            this.OpenRegionsAt(offset);

            var depth = this.incomingDepth.TryGetValue(offset, out var known) ? known : 0;

            this.stack.Clear();

            // A catch or filter block is entered with the exception already on the stack - the
            // one thing about the stack at a boundary that no branch carried there.
            if (this.pendingException != null)
            {
                var exception = new Slot();
                exception.Tokens.Add(Identifier(this.pendingException));

                this.stack.Add(exception);
                this.pendingException = null;

                return;
            }

            for (var i = 0; i < depth; i++)
                this.stack.Add(this.SpillSlotValue(i));
        }

        private void CloseRegionsUpTo(int offset)
        {
            while (this.openRegions.Count > 0 && this.openRegions.Peek() <= offset)
            {
                this.openRegions.Pop();
                this.indent--;

                var close = this.Scaffold(this.indent, Punctuation("}"));
                close.Kind = CSharpNodeKind.Close;
                close.IsFixed = true;

                this.body.Add(close);
            }
        }

        private void OpenRegionsAt(int offset)
        {
            // A try region can be named by several clauses - one per catch - so it is opened
            // once, and the widest region first, which is the one that encloses the others.
            var tries = this.il.ExceptionRegions
                .Where(region => region.TryOffset == offset)
                .Select(region => region.TryLength)
                .Distinct()
                .OrderByDescending(length => length);

            foreach (var length in tries)
                this.OpenRegion(offset + length, ControlKeyword("try"));

            foreach (var region in this.il.ExceptionRegions)
            {
                if (region.Kind == ClrIlExceptionRegionKind.Filter && region.FilterOffset == offset)
                {
                    this.pendingException = this.ExceptionName(region);
                    this.OpenRegion(region.HandlerOffset, ControlKeyword("filter"));
                }

                if (region.HandlerOffset != offset)
                    continue;

                var caught = region.Kind == ClrIlExceptionRegionKind.Finally
                             || region.Kind == ClrIlExceptionRegionKind.Fault
                    ? null
                    : this.ExceptionName(region);

                this.pendingException = caught;
                this.OpenRegion(region.HandlerEnd, this.HandlerHeader(region, caught));
            }
        }

        private ClrCSharpToken[] HandlerHeader(ClrIlExceptionRegion region, string exception)
        {
            switch (region.Kind)
            {
                case ClrIlExceptionRegionKind.Finally:
                    return new[] { ControlKeyword("finally") };

                case ClrIlExceptionRegionKind.Fault:
                    return new[] { ControlKeyword("fault") };

                case ClrIlExceptionRegionKind.Filter:
                    return new[]
                    {
                        ControlKeyword("catch"),
                        Punctuation(" ("),
                        Type("object"),
                        Punctuation(" "),
                        Identifier(exception),
                        Punctuation(") "),
                        Comment("/* when the filter above says so */")
                    };

                default:
                    return new[]
                    {
                        ControlKeyword("catch"),
                        Punctuation(" ("),
                        Type(CSharpNames.ShortenAll(region.CatchTypeName) ?? "object"),
                        Punctuation(" "),
                        Identifier(exception),
                        Punctuation(")")
                    };
            }
        }

        /// <summary>
        /// The name of the exception <paramref name="region"/> is entered with, unique within
        /// the method.
        /// </summary>
        /// <remarks>
        /// A filter and the handler it guards are two blocks that see the same exception, so
        /// they are named per region rather than per block - otherwise the filter would test one
        /// name and the catch would bind another.
        /// </remarks>
        private string ExceptionName(ClrIlExceptionRegion region)
        {
            if (this.exceptionNames.TryGetValue(region, out var existing))
                return existing;

            var index = this.handlers++;
            var name = index == 0 ? "ex" : $"ex{index}";

            this.exceptionNames.Add(region, name);

            return name;
        }

        private void OpenRegion(int end, params ClrCSharpToken[] header)
        {
            var opening = this.Scaffold(this.indent, header);
            opening.Kind = CSharpNodeKind.Open;
            opening.IsFixed = true;

            var brace = this.Scaffold(this.indent, Punctuation("{"));
            brace.Kind = CSharpNodeKind.Open;
            brace.IsFixed = true;

            this.body.Add(opening);
            this.body.Add(brace);

            this.openRegions.Push(end);
            this.indent++;
        }

        // ---------- the instruction walk ----------

        private void Translate(ClrIlInstruction instruction)
        {
            var name = instruction.OpCode.Name ?? string.Empty;

            switch (name)
            {
                case "nop":
                case "break":
                case "volatile.":
                case "readonly.":
                case "tail.":
                case "constrained.":
                case "unaligned.":
                case "no.":
                    // Nothing to say on its own - it is folded into the statement it modifies.
                    this.prefixes.Add(instruction);

                    return;

                case "dup":
                    this.Duplicate(instruction);

                    return;

                case "pop":
                {
                    var value = this.Pop();

                    var discard = this.Statement(value.Source.Append(instruction), Discard(value));

                    discard.AssignedName = "_";
                    discard.Value = value.Tokens;

                    return;
                }

                case "ldnull":
                {
                    var value = this.Leaf(instruction, Keyword("null"));
                    value.Flavour = Flavour.Reference;

                    this.Push(value);

                    return;
                }

                case "ldstr":
                {
                    var value = this.Leaf(instruction, Literal(instruction.OperandText()));
                    value.Flavour = Flavour.Reference;

                    this.Push(value);

                    return;
                }

                case "ret":
                    this.Return(instruction);

                    return;

                case "throw":
                {
                    var value = this.Pop();

                    var thrown = this.Statement(
                        value.Source.Append(instruction),
                        Join(new[] { ControlKeyword("throw"), Punctuation(" ") }, value.Tokens, Punctuation(";")));

                    thrown.Control = CSharpControl.Throw;

                    this.stack.Clear();

                    return;
                }

                case "rethrow":
                    this.Statement(new[] { instruction }, ControlKeyword("throw"), Punctuation(";")).Control =
                        CSharpControl.Throw;

                    return;

                case "endfinally":
                case "endfilter":
                    this.Statement(new[] { instruction }, Comment($"/* {name} */")).IsFixed = true;
                    this.stack.Clear();

                    return;

                case "switch":
                    this.Switch(instruction);

                    return;

                case "neg":
                    this.PushUnary(instruction, "-");

                    return;

                case "not":
                    this.PushUnary(instruction, "~");

                    return;

                case "ldlen":
                {
                    var array = this.Pop();

                    this.Push(this.MemberAccess(instruction, array, "Length"));

                    return;
                }

                case "localloc":
                {
                    var size = this.Pop();
                    var value = this.Build(instruction, Unary, size);

                    value.Tokens.AddRange(Join(
                        new[] { Keyword("stackalloc"), Punctuation(" "), Type("byte"), Punctuation("[") },
                        size.Tokens,
                        Punctuation("]")));

                    value.HasSideEffects = true;

                    this.Push(value);

                    return;
                }

                case "arglist":
                    this.Push(this.Leaf(instruction, Keyword("__arglist")));

                    return;

                case "ckfinite":
                {
                    var value = this.Pop();
                    var result = this.Build(instruction, Primary, value);

                    result.Tokens.AddRange(Join(
                        new[] { CallName("ckfinite"), Punctuation("(") }, value.Tokens, Punctuation(")")));

                    this.Push(result);

                    return;
                }
            }

            if (this.TranslateLoadStore(instruction, name)
                || this.TranslateArithmetic(instruction, name)
                || this.TranslateBranch(instruction, name)
                || this.TranslateCall(instruction, name)
                || this.TranslateMemory(instruction, name)
                || this.TranslateTypes(instruction, name))
            {
                return;
            }

            this.NotModelled(instruction);
        }

        private bool TranslateLoadStore(ClrIlInstruction instruction, string name)
        {
            if (name.StartsWith("ldarga", StringComparison.Ordinal))
            {
                this.Push(this.AddressOf(instruction, Identifier(this.ArgumentName(IndexOf(instruction)))));

                return true;
            }

            if (name.StartsWith("ldarg", StringComparison.Ordinal))
            {
                var index = IndexOf(instruction);
                var argument = this.Leaf(instruction, Identifier(this.ArgumentName(index)));

                argument.Flavour = this.ArgumentFlavour(index);

                this.Push(argument);

                return true;
            }

            if (name.StartsWith("ldloca", StringComparison.Ordinal))
            {
                this.Push(this.AddressOf(instruction, Identifier(this.LocalName(IndexOf(instruction)))));

                return true;
            }

            if (name.StartsWith("ldloc", StringComparison.Ordinal))
            {
                var slot = IndexOf(instruction);
                var value = this.Leaf(instruction, Identifier(this.LocalName(slot)));

                value.Flavour = this.LocalFlavour(slot);
                value.LocalsRead.Add(slot);

                this.Push(value);

                return true;
            }

            if (name.StartsWith("stloc", StringComparison.Ordinal))
            {
                var slot = IndexOf(instruction);
                var value = this.Pop();

                this.SpellBooleanLiteral(value, this.LocalFlavour(slot));
                this.SpillDependents(slot, false, false);
                this.Assign(instruction, value, Identifier(this.LocalName(slot)));

                return true;
            }

            if (name.StartsWith("starg", StringComparison.Ordinal))
            {
                var value = this.Pop();

                this.SpillDependents(null, false, false);
                this.Assign(instruction, value, Identifier(this.ArgumentName(IndexOf(instruction))));

                return true;
            }

            if (name.StartsWith("ldc.", StringComparison.Ordinal))
            {
                this.Push(this.Leaf(instruction, Number(ConstantText(instruction, name))));

                return true;
            }

            return false;
        }

        private bool TranslateArithmetic(ClrIlInstruction instruction, string name)
        {
            // The .ovf and .un forms have no operator of their own; the statement's trailing
            // comment names the real opcode, so nothing is lost by folding them together.
            var core = name.Replace(".ovf", string.Empty).Replace(".un", string.Empty);

            var (op, precedence) = core switch
            {
                "add" => ("+", Additive),
                "sub" => ("-", Additive),
                "mul" => ("*", Multiplicative),
                "div" => ("/", Multiplicative),
                "rem" => ("%", Multiplicative),
                "and" => ("&", BitAnd),
                "or" => ("|", BitOr),
                "xor" => ("^", BitXor),
                "shl" => ("<<", Shift),
                "shr" => (">>", Shift),
                "ceq" => ("==", Equality),
                "cgt" => (">", Relational),
                "clt" => ("<", Relational),
                _ => (null, 0)
            };

            if (op == null)
                return name.StartsWith("conv.", StringComparison.Ordinal) && this.Convert(instruction, name);

            var right = this.Pop();
            var left = this.Pop();

            if (this.TryNormalisedCondition(instruction, left, op, right))
                return true;

            this.Push(this.BinaryOp(instruction, left, op, right, precedence));

            return true;
        }

        /// <summary>
        /// Folds away the comparison against zero the compiler uses to normalise a bool.
        /// </summary>
        /// <remarks>
        /// A condition that has already been computed into a bool is turned back into a bool
        /// with <c>cgt.un</c> against zero, so <c>e.Message.Length &gt; 1</c> arrives as
        /// <c>(e.Message.Length &gt; 1) &gt; 0</c>. Comparing a bool to zero that way is the
        /// identity, and comparing it with <c>ceq</c> is its negation - both of which say less
        /// than the expression already did.
        /// </remarks>
        private bool TryNormalisedCondition(ClrIlInstruction instruction, Slot left, string op, Slot right)
        {
            if (this.form != ClrCSharpForm.Structured || !left.IsCondition)
                return false;

            if (right.Tokens.Count != 1 || right.Tokens[0].Text != "0")
                return false;

            if (op != ">" && op != "!=" && op != "==")
                return false;

            var folded = this.Build(instruction, left.Precedence, left, right);

            if (op == "==")
            {
                folded.Tokens.Add(Punctuation("!"));
                folded.Tokens.AddRange(Wrap(left, Unary));
                folded.Precedence = Unary;
            }
            else
            {
                folded.Tokens.AddRange(left.Tokens);
            }

            folded.Flavour = Flavour.Boolean;

            this.Push(folded);

            return true;
        }

        private bool Convert(ClrIlInstruction instruction, string name)
        {
            var target = ConversionTarget(name);
            if (target == null)
                return false;

            var value = this.Pop();

            this.Push(this.CastTo(instruction, target, value));

            return true;
        }

        private bool TranslateBranch(ClrIlInstruction instruction, string name)
        {
            if (!(instruction.Operand is ClrIlBranchTarget branch))
                return false;

            var core = name.Replace(".s", string.Empty).Replace(".un", string.Empty);

            var (op, precedence) = core switch
            {
                "beq" => ("==", Equality),
                "bne" => ("!=", Equality),
                "bgt" => (">", Relational),
                "blt" => ("<", Relational),
                "bge" => (">=", Relational),
                "ble" => ("<=", Relational),
                _ => (null, 0)
            };

            List<ClrCSharpToken> condition = null;
            var source = new List<ClrIlInstruction>();

            if (op != null)
            {
                var right = this.Pop();
                var left = this.Pop();
                var comparison = this.BinaryOp(instruction, left, op, right, precedence);

                condition = comparison.Tokens;
                source.AddRange(comparison.Source);
            }
            else if (core == "brtrue" || core == "brfalse")
            {
                var value = this.Pop();

                source.AddRange(value.Source);
                source.Add(instruction);

                // These branch on a value being zero, which only reads as a negation when the
                // value is already a comparison. Anything else - an int, a reference - has to be
                // compared explicitly, or the projection would claim a bool that is not there.
                if (value.IsCondition)
                {
                    condition = core == "brtrue"
                        ? value.Tokens
                        : Join(new[] { Punctuation("!") }, Wrap(value, Unary));
                }
                else if (value.Flavour == Flavour.Reference)
                {
                    condition = Join(
                        Wrap(value, Equality),
                        Punctuation(core == "brtrue" ? " != " : " == "),
                        Keyword("null"));
                }
                else
                {
                    condition = Join(
                        Wrap(value, Equality),
                        Punctuation(core == "brtrue" ? " != " : " == "),
                        Number("0"));
                }
            }
            else if (core != "br" && core != "leave")
            {
                return false;
            }
            else
            {
                source.Add(instruction);
            }

            // Anything left on the stack has to be somewhere the target can read it from.
            this.SpillStackToSlots(branch.Target);

            if (condition == null)
            {
                var jump = this.Statement(source, GotoTokens(branch.Target));

                jump.Control = CSharpControl.Goto;
                jump.Target = branch.Target;

                if (core == "br")
                    this.stack.Clear();

                return true;
            }

            var conditional = this.Statement(
                source,
                Join(
                        new[] { ControlKeyword("if"), Punctuation(" (") },
                        condition,
                        Punctuation(") "))
                    .Concat(GotoTokens(branch.Target))
                    .ToArray());

            conditional.Control = CSharpControl.ConditionalGoto;
            conditional.Target = branch.Target;
            conditional.Condition = condition;

            return true;
        }

        private void Switch(ClrIlInstruction instruction)
        {
            var targets = (int[])instruction.Operand;
            var value = this.Pop();

            foreach (var target in targets)
                this.SpillStackToSlots(target);

            var head = this.Statement(
                value.Source.Append(instruction),
                Join(new[] { ControlKeyword("switch"), Punctuation(" (") }, value.Tokens, Punctuation(")")));

            head.IsFixed = true;

            this.body.Add(this.Fixed(this.Scaffold(this.indent, Punctuation("{"))));

            for (var i = 0; i < targets.Length; i++)
            {
                var line = this.Fixed(this.Scaffold(
                    this.indent + 1,
                    Join(
                        new[]
                        {
                            ControlKeyword("case"), Punctuation(" "), Number(i.ToString(CultureInfo.InvariantCulture)),
                            Punctuation(": ")
                        },
                        GotoTokens(targets[i]))));

                // The case's jump is not rewritten, but it is still a jump: a pass that did not
                // count it could delete the only label a case lands on.
                line.Control = CSharpControl.Goto;
                line.Target = targets[i];

                this.body.Add(line);
            }

            this.body.Add(this.Fixed(this.Scaffold(this.indent, Punctuation("}"))));
        }

        private bool TranslateCall(ClrIlInstruction instruction, string name)
        {
            switch (name)
            {
                case "call":
                case "callvirt":
                case "calli":
                case "newobj":
                case "ldftn":
                case "ldvirtftn":
                case "jmp":
                    break;

                default:
                    return false;
            }

            if (name == "calli")
                return this.Indirect(instruction);

            var target = CallTarget.Of(instruction.Operand);

            if (name == "ldftn" || name == "ldvirtftn")
            {
                var receiver = name == "ldvirtftn" ? this.Pop() : null;
                var value = receiver == null ? this.New(instruction) : this.Build(instruction, Unary, receiver);

                value.Tokens.AddRange(Join(
                    Punctuation("&"), this.Qualifier(target), CallName(target.Name)));

                this.Push(value);

                return true;
            }

            if (!target.IsKnown)
            {
                this.NotModelled(instruction);

                return true;
            }

            // A call could change anything, so nothing unevaluated may be left sitting behind it -
            // apart from the operands the call is itself about to take.
            this.SpillDependents(
                null, true, true, target.ArgumentCount + (target.HasThis && name != "newobj" ? 1 : 0));

            var arguments = new List<Slot>();

            for (var i = 0; i < target.ArgumentCount; i++)
                arguments.Insert(0, this.Pop());

            var instance = target.HasThis && name != "newobj" ? this.Pop() : null;
            var operands = instance == null ? arguments : new[] { instance }.Concat(arguments).ToList();

            if (name == "call" && this.TryConcatenation(instruction, target, arguments, instance))
                return true;

            if (this.TryProperty(instruction, target, arguments, instance))
                return true;

            if (this.TryOperator(instruction, target, arguments, instance))
                return true;

            if (this.TryValueConstruction(instruction, target, arguments, instance))
                return true;

            var call = this.Build(instruction, Primary, operands.ToArray());
            call.HasSideEffects = true;
            call.Flavour = name == "newobj" ? Flavour.Reference : target.Returns;

            if (name == "newobj")
            {
                call.Tokens.AddRange(new[] { Keyword("new"), Punctuation(" "), Type(target.Owner) });
            }
            else if (instance != null)
            {
                call.Tokens.AddRange(Receiver(instance));
                call.Tokens.Add(Punctuation("."));
                call.Tokens.Add(CallName(target.Name));
            }
            else
            {
                call.Tokens.AddRange(this.Qualifier(target));
                call.Tokens.Add(CallName(target.Name));
            }

            call.Tokens.Add(Punctuation("("));

            for (var i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                    call.Tokens.Add(Punctuation(", "));

                call.Tokens.AddRange(arguments[i].Tokens);
            }

            call.Tokens.Add(Punctuation(")"));

            if (name == "jmp")
            {
                this.Statement(call.Source,
                    Join(new[] { ControlKeyword("return"), Punctuation(" ") }, call.Tokens, Punctuation(";")));

                return true;
            }

            if (target.ReturnsVoid && name != "newobj")
                this.Statement(call.Source, Join(call.Tokens, Punctuation(";")));
            else
                this.Push(call);

            return true;
        }

        /// <summary>
        /// Projects a <c>calli</c>, which calls a function pointer rather than a named method.
        /// </summary>
        /// <remarks>
        /// There is no method to ask how many arguments it takes - only a standalone signature,
        /// whose calling convention and parameter count are the first two numbers in the blob.
        /// That is all the stack model needs, and reading just those two is why a <c>calli</c>
        /// does not have to be given up on: it is what the runtime's own P/Invoke stubs are made
        /// of, so it turns up in ordinary framework code.
        /// </remarks>
        private bool Indirect(ClrIlInstruction instruction)
        {
            var shape = IndirectShape(instruction.Operand);

            if (shape == null)
            {
                this.NotModelled(instruction);

                return true;
            }

            var (hasThis, count, returnsVoid) = shape.Value;

            this.SpillDependents(null, true, true, count + (hasThis ? 1 : 0) + 1);

            var arguments = new List<Slot>();

            for (var i = 0; i < count + (hasThis ? 1 : 0); i++)
                arguments.Insert(0, this.Pop());

            // The function pointer is pushed last and so is popped first, before the arguments.
            var pointer = this.Pop();
            var call = this.Build(instruction, Primary, new[] { pointer }.Concat(arguments).ToArray());

            call.HasSideEffects = true;
            call.Tokens.Add(CallName("calli"));
            call.Tokens.Add(Punctuation("("));
            call.Tokens.AddRange(pointer.Tokens);

            foreach (var argument in arguments)
            {
                call.Tokens.Add(Punctuation(", "));
                call.Tokens.AddRange(argument.Tokens);
            }

            call.Tokens.Add(Punctuation(")"));

            if (returnsVoid)
                this.Statement(call.Source, Join(call.Tokens, Punctuation(";")));
            else
                this.Push(call);

            return true;
        }

        /// <summary>
        /// The calling convention, parameter count and void-ness of an indirect call site, or
        /// null when neither source could say.
        /// </summary>
        private static (bool HasThis, int Count, bool ReturnsVoid)? IndirectShape(object operand)
        {
            switch (operand)
            {
                case byte[] blob when blob.Length > 1:
                {
                    // A MethodDefSig: the calling convention, then a generic count when the
                    // convention says so, then the parameter count, then the return type.
                    var position = 0;
                    var convention = blob[position++];

                    if ((convention & GenericConvention) != 0)
                        ReadCompressed(blob, ref position);

                    var count = ReadCompressed(blob, ref position);

                    return ((convention & HasThisConvention) != 0,
                        count,
                        position < blob.Length && blob[position] == (byte)CorElementType.VOID);
                }

                case ClrIlToken token when token.Signature != null:
                    return (token.Signature.HasThis,
                        token.Signature.Parameters.Count,
                        token.Signature.ReturnType?.ElementType == CorElementType.VOID);

                default:
                    return null;
            }
        }

        /// <summary>Reads one ECMA-335 compressed unsigned integer.</summary>
        private static int ReadCompressed(byte[] blob, ref int position)
        {
            var first = blob[position++];

            if ((first & 0x80) == 0)
                return first;

            if ((first & 0x40) == 0)
                return ((first & 0x3F) << 8) | blob[position++];

            var third = blob[position++];
            var fourth = blob[position++];

            return ((first & 0x1F) << 24) | (third << 16) | (fourth << 8) | blob[position++];
        }

        /// <summary>
        /// The type a static call is qualified by, and the dot after it - or nothing, when the
        /// operand did not say which type it belongs to.
        /// </summary>
        /// <remarks>
        /// A token that names no owner is not a reason to write one: <c>?.Method()</c> claims
        /// there is a type called <c>?</c>, where <c>Method()</c> says only what is known.
        /// </remarks>
        private List<ClrCSharpToken> Qualifier(CallTarget target)
        {
            return string.IsNullOrEmpty(target.Owner) || target.Owner == "?"
                ? new List<ClrCSharpToken>()
                : new List<ClrCSharpToken> { Type(this.Named(target.Owner)), Punctuation(".") };
        }

        /// <summary>
        /// A call's receiver. The address of a local is spelled as the local: calling a value
        /// type's method needs its address, and <c>(&amp;loc0).ToString()</c> is that ceremony
        /// rather than anything the source said.
        /// </summary>
        private static List<ClrCSharpToken> Receiver(Slot instance)
        {
            return instance.AddressedPlace ?? Wrap(instance, Primary);
        }

        /// <summary>
        /// A property's accessor written back as the property.
        /// </summary>
        /// <remarks>
        /// A property is a pair of methods with a naming convention, and the convention is all
        /// there is to go on here: a MethodDesc-sourced call has no metadata flag to check
        /// without walking the Property table. The convention is the compiler's own, so
        /// <c>get_Current()</c> with no arguments is a property read and nothing else.
        /// </remarks>
        /// <summary>
        /// An operator method written as the operator it is.
        /// </summary>
        /// <remarks>
        /// C# compiles an operator on anything that is not a primitive into a call to a method
        /// with a reserved name, and generic maths turns every operator on a type parameter into
        /// one - so a constrained generic method reads as a list of calls until they are written
        /// back. The names are the ones ECMA-335 reserves, so nothing is called this by accident.
        /// </remarks>
        private bool TryOperator(
            ClrIlInstruction instruction, CallTarget target, List<Slot> arguments, Slot instance)
        {
            if (this.form != ClrCSharpForm.Structured || instance != null)
                return false;

            var (symbol, precedence, arity) = OperatorOf(target.Name);

            if (symbol == null || arguments.Count != arity)
                return false;

            var result = this.Build(instruction, precedence, arguments.ToArray());
            result.HasSideEffects = true;

            if (arity == 1)
            {
                // A step reads as one: an increment is not a prefix operator on its operand.
                if (symbol == "++" || symbol == "--")
                {
                    result.Tokens.AddRange(Wrap(arguments[0], Additive));
                    result.Tokens.Add(Punctuation(symbol == "++" ? " + " : " - "));
                    result.Tokens.Add(Number("1"));
                }
                else
                {
                    result.Tokens.Add(Punctuation(symbol));
                    result.Tokens.AddRange(Wrap(arguments[0], Unary));
                }
            }
            else
            {
                result.Tokens.AddRange(Wrap(arguments[0], precedence));
                result.Tokens.Add(Punctuation($" {symbol} "));
                result.Tokens.AddRange(Wrap(arguments[1], precedence + 1));
            }

            if (precedence == Equality || precedence == Relational)
                result.Flavour = Flavour.Boolean;

            this.Push(result);

            return true;
        }

        /// <summary>
        /// The operator a method's reserved name stands for, how tightly it binds, and how many
        /// operands it takes.
        /// </summary>
        private static (string Symbol, int Precedence, int Arity) OperatorOf(string name)
        {
            switch (name)
            {
                case "op_Addition": return ("+", Additive, 2);
                case "op_Subtraction": return ("-", Additive, 2);
                case "op_Multiply": return ("*", Multiplicative, 2);
                case "op_Division": return ("/", Multiplicative, 2);
                case "op_Modulus": return ("%", Multiplicative, 2);
                case "op_BitwiseAnd": return ("&", BitAnd, 2);
                case "op_BitwiseOr": return ("|", BitOr, 2);
                case "op_ExclusiveOr": return ("^", BitXor, 2);
                case "op_LeftShift": return ("<<", Shift, 2);
                case "op_RightShift": return (">>", Shift, 2);
                case "op_Equality": return ("==", Equality, 2);
                case "op_Inequality": return ("!=", Equality, 2);
                case "op_LessThan": return ("<", Relational, 2);
                case "op_GreaterThan": return (">", Relational, 2);
                case "op_LessThanOrEqual": return ("<=", Relational, 2);
                case "op_GreaterThanOrEqual": return (">=", Relational, 2);
                case "op_UnaryNegation": return ("-", Unary, 1);
                case "op_UnaryPlus": return ("+", Unary, 1);
                case "op_LogicalNot": return ("!", Unary, 1);
                case "op_OnesComplement": return ("~", Unary, 1);
                case "op_Increment": return ("++", Additive, 1);
                case "op_Decrement": return ("--", Additive, 1);
                default: return (null, 0, 0);
            }
        }

        /// <summary>
        /// A value type's constructor, written as the assignment it amounts to.
        /// </summary>
        /// <remarks>
        /// A struct is constructed in place: the IL takes the address of the local and calls the
        /// constructor on it, where the source wrote an assignment of a new value. Left alone it
        /// reads as <c>span..ctor(values)</c>, which is not something anyone wrote.
        /// </remarks>
        private bool TryValueConstruction(
            ClrIlInstruction instruction, CallTarget target, List<Slot> arguments, Slot instance)
        {
            if (this.form != ClrCSharpForm.Structured || target.Name != ".ctor")
                return false;

            if (instance?.AddressedPlace == null)
                return false;

            var construction = this.Build(instruction, Primary, arguments.ToArray());

            construction.Tokens.Add(Keyword("new"));
            construction.Tokens.Add(Punctuation(" "));
            construction.Tokens.Add(Type(this.Named(target.Owner)));
            construction.Tokens.Add(Punctuation("("));

            for (var i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                    construction.Tokens.Add(Punctuation(", "));

                construction.Tokens.AddRange(arguments[i].Tokens);
            }

            construction.Tokens.Add(Punctuation(")"));

            var node = this.Statement(
                construction.Source.Concat(instance.Source).OrderBy(item => item.Offset),
                Join(instance.AddressedPlace, Punctuation(" = "), construction.Tokens, Punctuation(";")));

            node.Value = construction.Tokens;

            if (instance.AddressedPlace.Count == 1
                && instance.AddressedPlace[0].Kind == ClrCSharpTokenKind.Identifier)
            {
                node.AssignedName = instance.AddressedPlace[0].Text;
            }

            return true;
        }

        private bool TryProperty(
            ClrIlInstruction instruction, CallTarget target, List<Slot> arguments, Slot instance)
        {
            if (this.form != ClrCSharpForm.Structured)
                return false;

            var getter = target.Name.StartsWith("get_", StringComparison.Ordinal);
            var setter = target.Name.StartsWith("set_", StringComparison.Ordinal);

            if (!getter && !setter)
                return false;

            var property = target.Name.Substring(4);

            if (property.Length == 0)
                return false;

            // An indexer is a property called Item whose arguments are the index. Anything else
            // taking arguments is a method whose name happens to start the same way.
            var indexer = property == "Item" && (getter ? arguments.Count > 0 : arguments.Count > 1);

            if (!indexer && (getter ? arguments.Count != 0 : arguments.Count != 1))
                return false;

            var indices = indexer
                ? arguments.Take(getter ? arguments.Count : arguments.Count - 1).ToList()
                : new List<Slot>();

            var operands = instance == null ? arguments : new[] { instance }.Concat(arguments).ToList();
            var access = this.Build(instruction, Primary, operands.ToArray());

            if (instance == null)
                access.Tokens.AddRange(this.Qualifier(target));
            else
                access.Tokens.AddRange(Receiver(instance));

            if (indexer)
            {
                access.Tokens.Add(Punctuation("["));

                for (var i = 0; i < indices.Count; i++)
                {
                    if (i > 0)
                        access.Tokens.Add(Punctuation(", "));

                    access.Tokens.AddRange(indices[i].Tokens);
                }

                access.Tokens.Add(Punctuation("]"));
            }
            else
            {
                if (instance != null)
                    access.Tokens.Add(Punctuation("."));

                access.Tokens.Add(MemberToken(property));
            }

            access.HasSideEffects = true;
            access.Flavour = target.Returns;

            if (getter)
            {
                this.Push(access);

                return true;
            }

            // The value assigned is the setter's last argument, which for an indexer comes after
            // the indices.
            var assigned = arguments[arguments.Count - 1];

            var node = this.Statement(
                access.Source, Join(access.Tokens, Punctuation(" = "), assigned.Tokens, Punctuation(";")));

            node.Value = assigned.Tokens;

            return true;
        }

        /// <summary>
        /// The concatenation a <c>+</c> on strings compiles to, written back as a <c>+</c>.
        /// </summary>
        /// <remarks>
        /// Only in the structured form, and only for the overloads that take their arguments
        /// directly: <c>Concat</c> of an array or a span is a call the source made, not an
        /// operator the compiler turned into one.
        /// </remarks>
        private bool TryConcatenation(
            ClrIlInstruction instruction, CallTarget target, List<Slot> arguments, Slot instance)
        {
            if (this.form != ClrCSharpForm.Structured || instance != null)
                return false;

            if (target.Name != "Concat" || target.Owner != "string" && target.Owner != "String")
                return false;

            if (arguments.Count < 2 || arguments.Count > 4)
                return false;

            var sum = this.Build(instruction, Additive, arguments.ToArray());

            for (var i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                    sum.Tokens.Add(Punctuation(" + "));

                sum.Tokens.AddRange(Wrap(arguments[i], i == 0 ? Additive : Additive + 1));
            }

            sum.Flavour = Flavour.Reference;

            this.Push(sum);

            return true;
        }

        private bool TranslateMemory(ClrIlInstruction instruction, string name)
        {
            if (name == "ldsfld" || name == "ldsflda")
            {
                var member = MemberName(instruction.Operand);
                var value = this.New(instruction);

                if (name == "ldsflda")
                    value.Tokens.Add(Punctuation("&"));

                value.Tokens.AddRange(new[] { Type(member.Owner), Punctuation("."), MemberToken(member.Name) });
                value.ReadsMemory = true;
                value.Flavour = name == "ldsflda" ? Flavour.Unknown : FieldFlavour(instruction.Operand);

                this.Push(value);

                return true;
            }

            if (name == "stsfld")
            {
                var member = MemberName(instruction.Operand);
                var value = this.Pop();

                this.SpillDependents(null, true, false);
                this.Assign(instruction, value, Type(member.Owner), Punctuation("."), MemberToken(member.Name));

                return true;
            }

            if (name == "ldfld" || name == "ldflda")
            {
                var member = MemberName(instruction.Operand);
                var receiver = this.Pop();
                var value = name == "ldflda"
                    ? this.AddressOfExpression(instruction, receiver, member.Name)
                    : this.MemberAccess(instruction, receiver, member.Name);

                value.ReadsMemory = true;
                value.Flavour = name == "ldflda" ? Flavour.Unknown : FieldFlavour(instruction.Operand);

                this.Push(value);

                return true;
            }

            if (name == "stfld")
            {
                var member = MemberName(instruction.Operand);
                var value = this.Pop();
                var receiver = this.Pop();

                this.SpillDependents(null, true, false);

                var target = Wrap(receiver, Primary)
                    .Concat(new[] { Punctuation("."), MemberToken(member.Name) })
                    .ToArray();

                this.Assign(instruction, value, receiver, target);

                return true;
            }

            if (name == "newarr")
            {
                var count = this.Pop();
                var value = this.Build(instruction, Unary, count);
                value.Flavour = Flavour.Reference;

                value.Tokens.AddRange(Join(
                    new[] { Keyword("new"), Punctuation(" "), Type(TypeName(instruction.Operand)), Punctuation("[") },
                    count.Tokens,
                    Punctuation("]")));

                this.Push(value);

                return true;
            }

            if (name.StartsWith("ldelem", StringComparison.Ordinal))
            {
                var index = this.Pop();
                var array = this.Pop();
                var value = this.Indexer(instruction, array, index);

                if (name == "ldelema")
                {
                    value.Tokens.Insert(0, Punctuation("&"));
                    value.Precedence = Unary;
                }

                value.ReadsMemory = true;

                this.Push(value);

                return true;
            }

            if (name.StartsWith("stelem", StringComparison.Ordinal))
            {
                var value = this.Pop();
                var index = this.Pop();
                var array = this.Pop();

                this.SpillDependents(null, true, false);

                var target = Wrap(array, Primary)
                    .Concat(new[] { Punctuation("[") })
                    .Concat(index.Tokens)
                    .Concat(new[] { Punctuation("]") })
                    .ToArray();

                this.Assign(instruction, value, new[] { array, index }, target);

                return true;
            }

            if (name.StartsWith("ldind.", StringComparison.Ordinal) || name == "ldobj")
            {
                var address = this.Pop();
                var type = name == "ldobj" ? TypeName(instruction.Operand) : IndirectType(name);
                var value = this.Build(instruction, Unary, address);

                value.Tokens.AddRange(Join(
                    new[] { Punctuation("*("), Type(type), Punctuation("*)") }, Wrap(address, Unary)));

                value.ReadsMemory = true;

                this.Push(value);

                return true;
            }

            if (name.StartsWith("stind.", StringComparison.Ordinal) || name == "stobj")
            {
                var value = this.Pop();
                var address = this.Pop();
                var type = name == "stobj" ? TypeName(instruction.Operand) : IndirectType(name);

                this.SpillDependents(null, true, false);

                var target = new[] { Punctuation("*("), Type(type), Punctuation("*)") }
                    .Concat(Wrap(address, Unary))
                    .ToArray();

                this.Assign(instruction, value, address, target);

                return true;
            }

            if (name == "initobj")
            {
                var address = this.Pop();

                this.Statement(
                    address.Source.Append(instruction),
                    Join(
                        new[] { Punctuation("*") },
                        Wrap(address, Unary),
                        Punctuation(" = "),
                        Keyword("default"),
                        Punctuation(";")));

                return true;
            }

            return false;
        }

        private bool TranslateTypes(ClrIlInstruction instruction, string name)
        {
            switch (name)
            {
                case "box":
                {
                    var value = this.Pop();
                    var boxed = this.CastTo(instruction, "object", value);

                    boxed.Flavour = Flavour.Reference;

                    this.Push(boxed);

                    return true;
                }

                case "unbox":
                {
                    var value = this.Pop();
                    var result = this.Build(instruction, Unary, value);

                    result.Tokens.AddRange(Join(
                        new[] { Punctuation("&("), Type(TypeName(instruction.Operand)), Punctuation(")") },
                        Wrap(value, Unary)));

                    this.Push(result);

                    return true;
                }

                case "unbox.any":
                case "castclass":
                {
                    var value = this.Pop();

                    this.Push(this.CastTo(instruction, TypeName(instruction.Operand), value));

                    return true;
                }

                case "isinst":
                {
                    var value = this.Pop();
                    var result = this.Build(instruction, Relational, value);
                    result.Flavour = Flavour.Reference;

                    result.Tokens.AddRange(Wrap(value, Relational));
                    result.Tokens.AddRange(new[]
                    {
                        Punctuation(" "), Keyword("as"), Punctuation(" "), Type(TypeName(instruction.Operand))
                    });

                    this.Push(result);

                    return true;
                }

                case "sizeof":
                    this.Push(this.Leaf(
                        instruction,
                        Keyword("sizeof"),
                        Punctuation("("),
                        Type(TypeName(instruction.Operand)),
                        Punctuation(")")));

                    return true;

                case "ldtoken":
                    this.Push(this.Leaf(
                        instruction,
                        Keyword("typeof"),
                        Punctuation("("),
                        Type(TypeName(instruction.Operand)),
                        Punctuation(")")));

                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Records that an instruction was not modelled, and gives up on the stack rather than
        /// carrying on with expressions that would no longer line up with it.
        /// </summary>
        private void NotModelled(ClrIlInstruction instruction)
        {
            this.exact = false;

            var text = instruction.OperandText();

            this.Statement(
                new[] { instruction },
                Comment($"/* {instruction.OpCode.Name}{(text.Length == 0 ? string.Empty : " " + text)} " +
                        "- not modelled, stack dropped */"));

            this.stack.Clear();
        }

        private void Return(ClrIlInstruction instruction)
        {
            if (this.stack.Count == 0)
            {
                var bare = this.Statement(new[] { instruction }, ControlKeyword("return"), Punctuation(";"));
                bare.Control = CSharpControl.Return;

                return;
            }

            var value = this.Pop();

            var returned = this.Statement(
                value.Source.Append(instruction),
                Join(new[] { ControlKeyword("return"), Punctuation(" ") }, this.Returned(value), Punctuation(";")));

            returned.Control = CSharpControl.Return;
            returned.Value = value.Tokens;

            this.stack.Clear();
        }

        private void Duplicate(ClrIlInstruction instruction)
        {
            var value = this.Pop();

            // Duplicating an expression would evaluate it twice, so anything that is not a bare
            // name goes through a temporary - which is what the jit does with it as well.
            if (!value.IsAtom)
            {
                this.SpillUpTo(this.stack.Count - 1);
                value = this.SpillToTemporary(value, instruction);
            }

            this.Push(value);
            this.Push(this.Copy(value, instruction));
        }

        // ---------- statements, spilling and the stack ----------

        private void Push(Slot value) => this.stack.Add(value);

        private Slot Pop()
        {
            if (this.stack.Count == 0)
            {
                // Either an instruction this does not model dropped the stack, or the IL is not
                // what it seemed. Either way, say so rather than inventing an operand.
                this.exact = false;

                var missing = new Slot();
                missing.Tokens.Add(Comment("/* stack empty */"));

                return missing;
            }

            var top = this.stack[this.stack.Count - 1];
            this.stack.RemoveAt(this.stack.Count - 1);

            return top;
        }

        /// <summary>
        /// Writes out any stacked expression that the statement about to be emitted could change
        /// underneath.
        /// </summary>
        /// <summary>
        /// Writes out stacked expressions a statement about to be emitted could change under.
        /// </summary>
        /// <param name="protect">
        /// How many slots at the top of the stack the statement is about to consume itself.
        /// Those need no protecting: a call cannot change its own arguments before it receives
        /// them, and spilling them would put a temporary in front of every call operand.
        /// </param>
        private void SpillDependents(int? localWritten, bool writesMemory, bool isCall, int protect = 0)
        {
            var highest = -1;

            for (var i = 0; i < this.stack.Count - protect; i++)
            {
                if (IsThreatened(this.stack[i], localWritten, writesMemory, isCall))
                    highest = i;
            }

            if (highest >= 0)
                this.SpillUpTo(highest);
        }

        private static bool IsThreatened(Slot slot, int? localWritten, bool writesMemory, bool isCall)
        {
            if (slot.IsAtom && !slot.ReadsMemory && slot.LocalsRead.Count == 0)
                return false;

            return isCall
                ? slot.HasSideEffects || slot.ReadsMemory || slot.LocalsRead.Count > 0
                : (localWritten.HasValue && slot.LocalsRead.Contains(localWritten.Value))
                  || (writesMemory && (slot.ReadsMemory || slot.HasSideEffects));
        }

        /// <summary>
        /// Writes out every stacked expression up to and including <paramref name="highest"/>.
        /// </summary>
        /// <remarks>
        /// The ones below the threatened slot are written out as well, even though nothing
        /// threatens them, because they were pushed earlier: leaving them folded would put a
        /// statement carrying their offsets after the temporary's, and those offsets are the
        /// labels the projected gotos jump to - they have to run forwards.
        /// </remarks>
        private void SpillUpTo(int highest)
        {
            for (var i = 0; i <= highest && i < this.stack.Count; i++)
            {
                if (this.stack[i].Source.Count > 0)
                    this.stack[i] = this.SpillToTemporary(this.stack[i], null);
            }
        }

        /// <summary>
        /// Evaluates <paramref name="value"/> into a temporary now, and returns a slot that
        /// simply names it.
        /// </summary>
        private Slot SpillToTemporary(Slot value, ClrIlInstruction cause)
        {
            var name = $"t{this.temporaries++}";

            var node = this.Statement(
                cause == null ? value.Source : value.Source.Append(cause),
                Join(
                    new[] { Type("var"), Punctuation(" "), Identifier(name), Punctuation(" = ") },
                    value.Tokens,
                    Punctuation(";")));

            node.AssignedName = name;
            node.Value = value.Tokens;

            var spilled = new Slot { Flavour = value.Flavour };
            spilled.Tokens.Add(Identifier(name));

            return spilled;
        }

        /// <summary>
        /// Moves the whole remaining stack into the numbered spill slots, so control can leave
        /// this block for <paramref name="target"/> without leaving expressions behind.
        /// </summary>
        private void SpillStackToSlots(int target)
        {
            if (this.stack.Count == 0)
                return;

            for (var i = 0; i < this.stack.Count; i++)
            {
                var slot = this.stack[i];

                if (slot.Tokens.Count == 1 && slot.Tokens[0].Text == $"st{i}")
                    continue;

                var spill = this.Statement(
                    slot.Source,
                    Join(
                        new[] { Identifier($"st{i}"), Punctuation(" = ") },
                        slot.Tokens,
                        Punctuation(";")));

                spill.AssignedName = $"st{i}";
                spill.Value = slot.Tokens;

                this.stack[i] = this.SpillSlotValue(i);
            }

            this.spillSlots = Math.Max(this.spillSlots, this.stack.Count);

            this.incomingDepth[target] = Math.Max(
                this.incomingDepth.TryGetValue(target, out var known) ? known : 0, this.stack.Count);
        }

        private Slot SpillSlotValue(int index)
        {
            var slot = new Slot();
            slot.Tokens.Add(Identifier($"st{index}"));

            this.spillSlots = Math.Max(this.spillSlots, index + 1);

            return slot;
        }

        private void Assign(ClrIlInstruction instruction, Slot value, params ClrCSharpToken[] target)
        {
            this.Assign(instruction, value, Array.Empty<Slot>(), target);
        }

        private void Assign(
            ClrIlInstruction instruction, Slot value, Slot extra, ClrCSharpToken[] target)
        {
            this.Assign(instruction, value, new[] { extra }, target);
        }

        /// <summary>
        /// Emits <c>target = value;</c>, attributing to it the IL of everything it consumed.
        /// </summary>
        private void Assign(
            ClrIlInstruction instruction, Slot value, IReadOnlyList<Slot> extra, ClrCSharpToken[] target)
        {
            var source = extra.SelectMany(slot => slot.Source)
                .Concat(value.Source)
                .Append(instruction)
                .OrderBy(item => item.Offset)
                .ToList();

            var node = this.Statement(source, Join(target, Punctuation(" = "), value.Tokens, Punctuation(";")));

            node.Value = value.Tokens;

            // Only an assignment to a plain name can be moved or folded; a field or an element
            // depends on whatever its receiver was at the time.
            if (target.Length == 1 && target[0].Kind == ClrCSharpTokenKind.Identifier)
                node.AssignedName = target[0].Text;
        }

        private CSharpNode Statement(IEnumerable<ClrIlInstruction> source, params ClrCSharpToken[] tokens)
        {
            // The prefixes are part of the statement's IL, so they count towards its label as
            // well: a nop at a branch target is where the branch actually goes.
            var instructions = this.prefixes.Concat(source).OrderBy(item => item.Offset).ToList();

            this.prefixes.Clear();

            var node = new CSharpNode
            {
                Offset = this.LabelFor(instructions),
                Depth = this.indent,
                Tokens = tokens.ToList(),
                Comment = string.Join("; ", instructions.Select(IlTextOf))
            };

            this.body.Add(node);

            return node;
        }

        private CSharpNode Statement(IEnumerable<ClrIlInstruction> source, IEnumerable<ClrCSharpToken> tokens)
        {
            return this.Statement(source, tokens.ToArray());
        }

        /// <summary>
        /// Which of a statement's instructions its label names.
        /// </summary>
        /// <remarks>
        /// The first one, normally. But an expression can be folded from an instruction long
        /// before the statement that consumes it - an argument loaded at the top of a method and
        /// passed at the bottom of it - and labelling that statement with the load's offset would
        /// print a label the listing has already gone past. So the label is the statement's first
        /// instruction that has not been passed yet, which for a statement starting a block is
        /// still its first: a block boundary spills the stack, so nothing is folded across one.
        /// </remarks>
        private int? LabelFor(IReadOnlyList<ClrIlInstruction> instructions)
        {
            if (instructions.Count == 0)
                return null;

            // An offset something jumps to has to keep its own label, even when an instruction
            // before it - a nop the compiler left as a sequence point - was folded into the same
            // statement and would otherwise name it.
            var target = instructions.FirstOrDefault(item => this.boundaries.Contains(item.Offset));

            if (target != null)
            {
                this.lastLabel = Math.Max(this.lastLabel, target.Offset);

                return target.Offset;
            }

            var first = instructions.Min(item => item.Offset);

            // A branch target keeps its own label whatever else has been printed - a goto with
            // no label to land on is worse than a label out of order.
            var offset = first >= this.lastLabel || this.boundaries.Contains(first)
                ? first
                : instructions.FirstOrDefault(item => item.Offset >= this.lastLabel)?.Offset ?? first;

            this.lastLabel = Math.Max(this.lastLabel, offset);

            return offset;
        }

        private static string IlTextOf(ClrIlInstruction instruction)
        {
            var operand = instruction.OperandText();

            return operand.Length == 0
                ? instruction.OpCode.Name
                : $"{instruction.OpCode.Name} {Shorten(operand)}";
        }

        /// <summary>Keeps a comment readable when an operand is a fully qualified name.</summary>
        private static string Shorten(string operand)
        {
            return operand.Length <= 60 ? operand : operand.Substring(0, 57) + "...";
        }

        /// <summary>Marks a node no pass may touch, and hands it back.</summary>
        private CSharpNode Fixed(CSharpNode node)
        {
            node.IsFixed = true;

            return node;
        }

        private CSharpNode Scaffold(int indent, params ClrCSharpToken[] tokens)
        {
            return this.Scaffold(indent, (IEnumerable<ClrCSharpToken>)tokens);
        }

        private CSharpNode Scaffold(int indent, IEnumerable<ClrCSharpToken> tokens)
        {
            return new CSharpNode
            {
                Kind = CSharpNodeKind.Blank,
                Depth = indent,
                Tokens = tokens.ToList()
            };
        }

        // ---------- expression building ----------

        private Slot New(ClrIlInstruction from)
        {
            var slot = new Slot();
            slot.Source.Add(from);

            return slot;
        }

        /// <summary>A slot whose operands are <paramref name="operands"/>, with their flags.</summary>
        private Slot Build(ClrIlInstruction from, int precedence, params Slot[] operands)
        {
            var slot = new Slot { Precedence = precedence };

            foreach (var operand in operands)
            {
                slot.Source.AddRange(operand.Source);
                slot.HasSideEffects |= operand.HasSideEffects;
                slot.ReadsMemory |= operand.ReadsMemory;

                foreach (var local in operand.LocalsRead)
                    slot.LocalsRead.Add(local);
            }

            slot.Source.Add(from);
            slot.Source.Sort((left, right) => left.Offset.CompareTo(right.Offset));

            return slot;
        }

        private Slot Leaf(ClrIlInstruction from, params ClrCSharpToken[] tokens)
        {
            var slot = this.New(from);
            slot.Tokens.AddRange(tokens);

            return slot;
        }

        private Slot Copy(Slot value, ClrIlInstruction from)
        {
            var slot = this.New(from);
            slot.Tokens.AddRange(value.Tokens);
            slot.Precedence = value.Precedence;

            foreach (var local in value.LocalsRead)
                slot.LocalsRead.Add(local);

            slot.ReadsMemory = value.ReadsMemory;
            slot.Flavour = value.Flavour;

            return slot;
        }

        private Slot AddressOf(ClrIlInstruction from, ClrCSharpToken name)
        {
            var slot = this.Leaf(from, Punctuation("&"), name);
            slot.AddressedPlace = new List<ClrCSharpToken> { name };

            // Taking an address binds more loosely than a member access does, so &n.ToString()
            // would read as the address of the result rather than a call on the address.
            slot.Precedence = Unary;

            return slot;
        }

        private Slot AddressOfExpression(ClrIlInstruction from, Slot receiver, string member)
        {
            var slot = this.MemberAccess(from, receiver, member);

            slot.AddressedPlace = slot.Tokens.ToList();
            slot.Tokens.Insert(0, Punctuation("&"));
            slot.Precedence = Unary;

            return slot;
        }

        private Slot PushUnary(ClrIlInstruction from, string op)
        {
            var value = this.Pop();
            var slot = this.Build(from, Unary, value);

            slot.Tokens.Add(Punctuation(op));
            slot.Tokens.AddRange(Wrap(value, Unary));

            this.Push(slot);

            return slot;
        }

        private Slot BinaryOp(ClrIlInstruction from, Slot left, string op, Slot right, int precedence)
        {
            var slot = this.Build(from, precedence, left, right);

            if (precedence == Equality || precedence == Relational)
                slot.Flavour = Flavour.Boolean;

            slot.Tokens.AddRange(Wrap(left, precedence));
            slot.Tokens.Add(Punctuation($" {op} "));

            // The right operand of a left-associative operator needs the tighter bound, or
            // a - (b - c) would print as a - b - c.
            slot.Tokens.AddRange(Wrap(right, precedence + 1));

            return slot;
        }

        private Slot CastTo(ClrIlInstruction from, string type, Slot value)
        {
            var slot = this.Build(from, Unary, value);

            slot.Tokens.Add(Punctuation("("));
            slot.Tokens.Add(Type(type));
            slot.Tokens.Add(Punctuation(")"));
            slot.Tokens.AddRange(Wrap(value, Unary));

            return slot;
        }

        private Slot MemberAccess(ClrIlInstruction from, Slot receiver, string member)
        {
            var slot = this.Build(from, Primary, receiver);

            slot.Tokens.AddRange(Wrap(receiver, Primary));
            slot.Tokens.Add(Punctuation("."));
            slot.Tokens.Add(MemberToken(member));

            return slot;
        }

        private Slot Indexer(ClrIlInstruction from, Slot array, Slot index)
        {
            var slot = this.Build(from, Primary, array, index);

            slot.Tokens.AddRange(Wrap(array, Primary));
            slot.Tokens.Add(Punctuation("["));
            slot.Tokens.AddRange(index.Tokens);
            slot.Tokens.Add(Punctuation("]"));

            return slot;
        }

        private static ClrCSharpToken[] Discard(Slot value)
        {
            // A value that was computed and thrown away still has to be shown being computed,
            // because computing it may have been the point.
            return Join(new[] { Identifier("_"), Punctuation(" = ") }, value.Tokens, Punctuation(";")).ToArray();
        }

        private static ClrCSharpToken[] GotoTokens(int target)
        {
            return new[]
            {
                ControlKeyword("goto"), Punctuation(" "), ControlKeyword($"IL_{target:x4}"), Punctuation(";")
            };
        }

        /// <summary>
        /// An operand's tokens, parenthesised when it binds more loosely than where it is going.
        /// </summary>
        private static List<ClrCSharpToken> Wrap(Slot value, int required)
        {
            if (value.Precedence >= required)
                return value.Tokens;

            var wrapped = new List<ClrCSharpToken> { Punctuation("(") };
            wrapped.AddRange(value.Tokens);
            wrapped.Add(Punctuation(")"));

            return wrapped;
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

        // ---------- names ----------

        private string ArgumentName(int index)
        {
            return index >= 0 && index < this.argumentNames.Length ? this.argumentNames[index] : $"arg{index}";
        }

        /// <summary>
        /// What to call the local in slot <paramref name="index"/>: the name the source gave it
        /// when the module's PDB could be read, and the slot number otherwise.
        /// </summary>
        private string LocalName(int index)
        {
            this.highestLocal = Math.Max(this.highestLocal, index);

            return index >= 0 && index < this.il.LocalVariables.Count
                ? this.il.LocalVariables[index].DisplayName
                : $"loc{index}";
        }

        /// <summary>What the local in <paramref name="index"/> holds, when that is known.</summary>
        private Flavour LocalFlavour(int index)
        {
            if (index < 0 || index >= this.il.LocalVariables.Count)
                return Flavour.Unknown;

            var local = this.il.LocalVariables[index];

            if (local.IsByRef)
                return Flavour.Unknown;

            return local.Type != null ? FlavourOf(local.Type) : FlavourOf(local.SignatureType);
        }

        /// <summary>
        /// A local's type, from whichever source described it - a reflection type, or a decoded
        /// signature.
        /// </summary>
        private static string LocalTypeText(ClrIlLocal local)
        {
            var name = local.Type != null ? CSharpNames.Of(local.Type) : CSharpNames.Of(local.SignatureType);

            return local.IsByRef && !name.StartsWith("ref ", StringComparison.Ordinal) ? "ref " + name : name;
        }

        private Flavour ArgumentFlavour(int index)
        {
            var offset = this.IsStatic() ? 0 : 1;

            if (index < offset)
                return Flavour.Reference;

            if (this.il.Method != null)
            {
                var parameters = this.il.Method.GetParameters();

                return index - offset < parameters.Length
                    ? FlavourOf(parameters[index - offset].ParameterType)
                    : Flavour.Unknown;
            }

            if (this.signature == null || index - offset >= this.signature.Parameters.Count)
                return Flavour.Unknown;

            return FlavourOf(this.signature.Parameters[index - offset].Type);
        }

        private static Flavour FieldFlavour(object operand)
        {
            return operand is FieldInfo field ? FlavourOf(field.FieldType) : Flavour.Unknown;
        }

        private static Flavour FlavourOf(Type type)
        {
            if (type == null)
                return Flavour.Unknown;

            if (type == typeof(bool))
                return Flavour.Boolean;

            return !type.IsValueType && !type.IsPointer && !type.IsByRef
                ? Flavour.Reference
                : Flavour.Unknown;
        }

        private static Flavour FlavourOf(ClrSignatureType type)
        {
            switch (type?.ElementType)
            {
                case CorElementType.BOOLEAN:
                    return Flavour.Boolean;

                case CorElementType.STRING:
                case CorElementType.OBJECT:
                case CorElementType.CLASS:
                case CorElementType.SZARRAY:
                case CorElementType.ARRAY:
                    return Flavour.Reference;

                default:
                    return Flavour.Unknown;
            }
        }

        /// <summary>The index a load or store names, whether in its opcode or in its operand.</summary>
        private static int IndexOf(ClrIlInstruction instruction)
        {
            var name = instruction.OpCode.Name ?? string.Empty;
            var dot = name.LastIndexOf('.');

            if (dot >= 0 && dot + 1 < name.Length && char.IsDigit(name[dot + 1]))
                return int.Parse(name.Substring(dot + 1), CultureInfo.InvariantCulture);

            try
            {
                return System.Convert.ToInt32(instruction.Operand ?? 0, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string ConstantText(ClrIlInstruction instruction, string name)
        {
            if (name == "ldc.i4.m1")
                return "-1";

            var dot = name.LastIndexOf('.');

            if (dot >= 0 && dot + 1 < name.Length && char.IsDigit(name[dot + 1]) &&
                name.StartsWith("ldc.i4.", StringComparison.Ordinal))
                return name.Substring(dot + 1);

            var text = instruction.OperandText();

            return text.Length == 0 ? "0" : text;
        }

        /// <summary>The type a <c>conv</c> opcode converts to, or null when it is not one.</summary>
        private static string ConversionTarget(string name)
        {
            switch (name.Replace("conv.", string.Empty).Replace("ovf.", string.Empty))
            {
                case "i1":
                case "i1.un": return "sbyte";
                case "u1":
                case "u1.un": return "byte";
                case "i2":
                case "i2.un": return "short";
                case "u2":
                case "u2.un": return "ushort";
                case "i4":
                case "i4.un": return "int";
                case "u4":
                case "u4.un": return "uint";
                case "i8":
                case "i8.un": return "long";
                case "u8":
                case "u8.un": return "ulong";
                case "r4": return "float";
                case "r8":
                case "r.un": return "double";
                case "i":
                case "i.un": return "nint";
                case "u":
                case "u.un": return "nuint";
                default: return null;
            }
        }

        /// <summary>The type an indirect load or store moves.</summary>
        private static string IndirectType(string name)
        {
            switch (name.Substring(name.IndexOf('.') + 1))
            {
                case "i1": return "sbyte";
                case "u1": return "byte";
                case "i2": return "short";
                case "u2": return "ushort";
                case "i4": return "int";
                case "u4": return "uint";
                case "i8": return "long";
                case "r4": return "float";
                case "r8": return "double";
                case "i": return "nint";
                case "ref": return "object";
                default: return "void";
            }
        }

        private string TypeName(object operand)
        {
            switch (operand)
            {
                case Type type:
                    return CSharpNames.Of(type);

                case null:
                    return "?";

                default:
                    return this.Named(CSharpNames.ShortenAll(operand.ToString()));
            }
        }

        /// <summary>Splits a field or method operand into the type that declares it and its name.</summary>
        private static (string Owner, string Name) MemberName(object operand)
        {
            switch (operand)
            {
                case FieldInfo field:
                    return (CSharpNames.Of(field.DeclaringType), field.Name);

                case MemberInfo member:
                    return (CSharpNames.Of(member.DeclaringType), member.Name);

                default:
                    return CSharpNames.Split(operand?.ToString());
            }
        }

        private static ClrCSharpToken Keyword(string text) => new ClrCSharpToken(ClrCSharpTokenKind.Keyword, text);

        private static ClrCSharpToken ControlKeyword(string text) =>
            new ClrCSharpToken(ClrCSharpTokenKind.ControlKeyword, text);

        private static ClrCSharpToken Type(string text) => new ClrCSharpToken(ClrCSharpTokenKind.Type, text);

        private static ClrCSharpToken MemberToken(string text) => new ClrCSharpToken(ClrCSharpTokenKind.Member, text);

        private static ClrCSharpToken CallName(string text) => new ClrCSharpToken(ClrCSharpTokenKind.Call, text);

        private static ClrCSharpToken Literal(string text) => new ClrCSharpToken(ClrCSharpTokenKind.Literal, text);

        private static ClrCSharpToken Number(string text) => new ClrCSharpToken(ClrCSharpTokenKind.Number, text);

        private static ClrCSharpToken Identifier(string text) =>
            new ClrCSharpToken(ClrCSharpTokenKind.Identifier, text);

        private static ClrCSharpToken Punctuation(string text) =>
            new ClrCSharpToken(ClrCSharpTokenKind.Punctuation, text);

        private static ClrCSharpToken Comment(string text) => new ClrCSharpToken(ClrCSharpTokenKind.Comment, text);

        /// <summary>
        /// What a call site consumes and produces, from whichever kind of operand named it.
        /// </summary>
        /// <remarks>
        /// The parameter count and the this-ness are what the stack model needs; without them a
        /// call cannot be projected at all, which is what <see cref="IsKnown"/> reports. A
        /// reflection operand has both directly; an operand read from metadata has them in its
        /// signature blob, decoded through <see cref="ClrIlToken.Signature"/>.
        /// </remarks>
        private sealed class CallTarget
        {
            public string Owner { get; private set; } = "?";

            public string Name { get; private set; } = "?";

            public int ArgumentCount { get; private set; }

            public bool HasThis { get; private set; }

            public bool ReturnsVoid { get; private set; }

            /// <summary>What the return value is, as far as the signature says.</summary>
            public Flavour Returns { get; private set; }

            public bool IsKnown { get; private set; }

            public static CallTarget Of(object operand)
            {
                switch (operand)
                {
                    case ConstructorInfo constructor:
                        return new CallTarget
                        {
                            Owner = CSharpNames.Of(constructor.DeclaringType),
                            Name = ".ctor",
                            ArgumentCount = constructor.GetParameters().Length,
                            HasThis = !constructor.IsStatic,
                            ReturnsVoid = true,
                            IsKnown = true
                        };

                    case MethodInfo method:
                        return new CallTarget
                        {
                            Owner = CSharpNames.Of(method.DeclaringType),
                            Name = CSharpNames.Of(method),
                            ArgumentCount = method.GetParameters().Length,
                            HasThis = !method.IsStatic,
                            ReturnsVoid = method.ReturnType == typeof(void),
                            Returns = FlavourOf(method.ReturnType),
                            IsKnown = true
                        };

                    case ClrIlToken token:
                    {
                        var signature = token.Signature;
                        var split = CSharpNames.Split(token.Name);

                        if (signature == null)
                            return new CallTarget { Owner = split.Owner, Name = split.Name };

                        return new CallTarget
                        {
                            Owner = split.Owner,
                            Name = split.Name,
                            ArgumentCount = signature.Parameters.Count,
                            HasThis = signature.HasThis,
                            ReturnsVoid = signature.ReturnType?.ElementType == CorElementType.VOID,
                            Returns = FlavourOf(signature.ReturnType),
                            IsKnown = true
                        };
                    }

                    default:
                        return new CallTarget();
                }
            }
        }
    }

    /// <summary>
    /// Type and member names spelled the way C# spells them, for the projection.
    /// </summary>
    /// <remarks>
    /// Names are shortened to the declaring type rather than fully qualified. An IL listing uses
    /// full names because ILDASM does and because a token is unambiguous only that way, but a
    /// line of C# reads as code, and <c>Console.WriteLine(s)</c> is what the source said where
    /// <c>System.Console::WriteLine</c> is what the metadata says.
    /// </remarks>
    internal static class CSharpNames
    {
        private static readonly Dictionary<Type, string> Keywords = new Dictionary<Type, string>
        {
            [typeof(void)] = "void",
            [typeof(bool)] = "bool",
            [typeof(byte)] = "byte",
            [typeof(sbyte)] = "sbyte",
            [typeof(char)] = "char",
            [typeof(short)] = "short",
            [typeof(ushort)] = "ushort",
            [typeof(int)] = "int",
            [typeof(uint)] = "uint",
            [typeof(long)] = "long",
            [typeof(ulong)] = "ulong",
            [typeof(float)] = "float",
            [typeof(double)] = "double",
            [typeof(decimal)] = "decimal",
            [typeof(string)] = "string",
            [typeof(object)] = "object",
            [typeof(IntPtr)] = "nint",
            [typeof(UIntPtr)] = "nuint"
        };

        public static string Of(Type type)
        {
            if (type == null)
                return "var";

            if (Keywords.TryGetValue(type, out var keyword))
                return keyword;

            if (type.IsByRef)
                return "ref " + Of(type.GetElementType());

            if (type.IsPointer)
                return Of(type.GetElementType()) + "*";

            if (type.IsArray)
                return Of(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";

            if (type.IsGenericType)
            {
                var name = Shorten(type.Name);
                var tick = name.IndexOf('`');

                if (tick >= 0)
                    name = name.Substring(0, tick);

                return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Of))}>";
            }

            return Shorten(type.FullName ?? type.Name);
        }

        /// <summary>A signature type, which is already spelled the way source would.</summary>
        public static string Of(ClrSignatureType type)
        {
            if (type == null)
                return "var";

            // The signature reader qualifies every name it renders, including the ones inside a
            // generic argument list, so the whole rendering is shortened rather than its head.
            return type.KeywordName ?? ShortenAll(type.ToString());
        }

        /// <summary>A method's name, with its generic arguments when it has any.</summary>
        public static string Of(MethodInfo method)
        {
            if (!method.IsGenericMethod)
                return method.Name;

            return $"{method.Name}<{string.Join(", ", method.GetGenericArguments().Select(Of))}>";
        }

        /// <summary>The last segment of a qualified name - the part source would have written.</summary>
        public static string Shorten(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            // A generic argument list can carry dots of its own, which are not part of the name
            // being shortened.
            var end = name.IndexOf('<');
            var head = end < 0 ? name : name.Substring(0, end);
            var tail = end < 0 ? string.Empty : name.Substring(end);

            var cut = head.LastIndexOfAny(new[] { '.', '+' });

            return (cut < 0 ? head : head.Substring(cut + 1)) + tail;
        }

        /// <summary>
        /// Shortens every qualified name in <paramref name="text"/>, not just the first, so
        /// <c>Dictionary&lt;string, System.Collections.Generic.List&lt;int&gt;&gt;</c> comes out
        /// the way source would have written it.
        /// </summary>
        /// <remarks>
        /// Only runs that begin with a letter are treated as names: a version number is dots and
        /// digits too, and shortening <c>11.0.0.0</c> to <c>0</c> would be worse than leaving a
        /// namespace in. A generic type's arity suffix goes with the namespace - what source
        /// wrote was <c>List</c>, never <c>List`1</c>.
        /// </remarks>
        public static string ShortenAll(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var result = new StringBuilder(text.Length);
            var run = new StringBuilder();

            void Flush()
            {
                if (run.Length == 0)
                    return;

                var name = run.ToString();
                run.Clear();

                if (!char.IsLetter(name[0]) && name[0] != '_')
                {
                    result.Append(name);

                    return;
                }

                var cut = name.LastIndexOfAny(new[] { '.', '+' });
                var shortened = cut < 0 ? name : name.Substring(cut + 1);
                var arity = shortened.IndexOf('`');

                result.Append(arity < 0 ? shortened : shortened.Substring(0, arity));
            }

            foreach (var character in text)
            {
                if (char.IsLetterOrDigit(character) || character == '_'
                                                    || character == '.' || character == '+' || character == '`')
                {
                    run.Append(character);

                    continue;
                }

                Flush();
                result.Append(character);
            }

            Flush();

            return result.ToString();
        }

        /// <summary>Splits a metadata name of the form <c>Type::Member</c>.</summary>
        public static (string Owner, string Name) Split(string name)
        {
            if (string.IsNullOrEmpty(name))
                return ("?", "?");

            var separator = name.IndexOf("::", StringComparison.Ordinal);

            return separator < 0
                ? ("?", Shorten(name))
                : (ShortenAll(name.Substring(0, separator)), name.Substring(separator + 2));
        }
    }
}