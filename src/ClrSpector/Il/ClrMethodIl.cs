using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace ClrSpector
{
    /// <summary>One decoded IL instruction.</summary>
    public sealed class ClrIlInstruction
    {
        /// <summary>The instruction's byte offset from the start of the method body.</summary>
        public int Offset { get; internal set; }

        public OpCode OpCode { get; internal set; }

        /// <summary>
        /// The decoded operand: a number, a resolved <see cref="MemberInfo"/>, a string literal,
        /// a branch target offset, an <c>int[]</c> of switch targets, or null for no operand.
        /// </summary>
        public object Operand { get; internal set; }

        /// <summary>How many bytes this instruction occupies, opcode and operand together.</summary>
        public int Length { get; internal set; }

        /// <summary>The offset of the instruction after this one.</summary>
        public int NextOffset => this.Offset + this.Length;

        /// <summary>
        /// An instruction built by hand rather than decoded, for editing a body before emitting
        /// it back.
        /// </summary>
        /// <remarks>
        /// <paramref name="offset"/> only matters as a branch destination: re-emission repositions
        /// everything, so an instruction nothing jumps to can leave it at zero. Give a distinct
        /// offset to an instruction a <see cref="ClrIlBranchTarget"/> names.
        /// </remarks>
        public static ClrIlInstruction Create(OpCode opCode, object operand = null, int offset = 0)
        {
            return new ClrIlInstruction
            {
                OpCode = opCode,
                Operand = operand,
                Offset = offset,
                Length = opCode.Size
            };
        }

        /// <summary>The operand rendered the way ILDASM would write it.</summary>
        public string OperandText()
        {
            switch (this.Operand)
            {
                case null:
                    return string.Empty;

                case string text:
                    return $"\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

                case int[] targets:
                    return "(" + string.Join(", ", targets.Select(t => $"IL_{t:x4}")) + ")";

                case ClrIlBranchTarget branch:
                    return $"IL_{branch.Target:x4}";

                case MethodBase method:
                    return $"{Describe(method.DeclaringType)}::{method.Name}";

                case FieldInfo field:
                    return $"{Describe(field.DeclaringType)}::{field.Name}";

                case Type type:
                    return Describe(type);

                case MemberInfo member:
                    return $"{Describe(member.DeclaringType)}::{member.Name}";

                case ClrIlToken token:
                    return token.ToString();

                case float single:
                    return single.ToString("R", CultureInfo.InvariantCulture);

                case double value:
                    return value.ToString("R", CultureInfo.InvariantCulture);

                default:
                    return Convert.ToString(this.Operand, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// A type's name for display. An open generic type has no <see cref="Type.FullName"/>,
        /// so it falls back to the short name rather than printing nothing.
        /// </summary>
        private static string Describe(Type type)
        {
            return type == null ? "?" : type.FullName ?? type.Name;
        }

        public override string ToString() => this.Render(false);

        /// <summary>Renders the instruction, optionally with ANSI colour.</summary>
        public string Render(bool colouring)
        {
            var label = IlPalette.Paint($"IL_{this.Offset:x4}:", IlPalette.Offset, colouring);
            var name = IlPalette.Paint(this.OpCode.Name, IlPalette.ColourFor(this.OpCode), colouring);

            var operand = this.OperandText();
            if (operand.Length == 0)
                return $"{label}  {name}";

            // Padding is applied to the uncoloured width, so escape sequences do not throw the
            // column out - they take space in the string but none on screen.
            var padding = new string(' ', Math.Max(1, 13 - this.OpCode.Name.Length));

            return $"{label}  {name}{padding}{IlPalette.Paint(operand, this.OperandColour(), colouring)}";
        }

        /// <summary>The colour the operand itself should take, which is not always the opcode's.</summary>
        private string OperandColour()
        {
            switch (this.Operand)
            {
                case string _:
                    return IlPalette.Literal;

                case ClrIlBranchTarget _:
                case int[] _:
                    return IlPalette.ControlFlow;

                case MemberInfo _:
                case ClrIlToken _:
                    return IlPalette.Member;

                default:
                    return IlPalette.Number;
            }
        }
    }

    /// <summary>
    /// A metadata token named from the module's own metadata rather than resolved to a
    /// reflection object.
    /// </summary>
    /// <remarks>
    /// This is what an operand becomes when the IL was read from memory through a MethodDesc: the
    /// name comes out of the string heap, and no <see cref="Type"/> or <see cref="MethodBase"/>
    /// is created. Enough to read a listing, and deliberately not enough to re-emit - the emitter
    /// refuses one, because a name is not a member.
    /// </remarks>
    public sealed class ClrIlToken
    {
        internal ClrIlToken(int token, string name)
        {
            this.Token = token;
            this.Name = name;
        }

        public int Token { get; }

        /// <summary>What the token names, or its hexadecimal value when it does not resolve.</summary>
        public string Name { get; }

        public override string ToString() => this.Name ?? $"0x{this.Token:x8}";
    }

    /// <summary>A resolved branch destination, kept distinct from a plain integer operand.</summary>
    public sealed class ClrIlBranchTarget
    {
        /// <summary>A branch to <paramref name="target"/>, an absolute IL offset.</summary>
        public ClrIlBranchTarget(int target)
        {
            this.Target = target;
        }

        /// <summary>The absolute IL offset the branch goes to.</summary>
        public int Target { get; }

        public override string ToString() => $"IL_{this.Target:x4}";
    }

    /// <summary>
    /// A method's IL, decoded into instructions and printable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bytes come from <see cref="MethodBody.GetILAsByteArray"/>, which is the method's
    /// current IL as the runtime would hand it to the jit. Tokens in the operands are resolved
    /// through the declaring module, with the declaring type's and method's generic arguments
    /// supplied as context - without those a token inside a generic method fails to resolve.
    /// </para>
    /// <para>
    /// A method a profiler has rejitted may be running IL that differs from this; the runtime
    /// keeps that separately, on the IL code version (see <see cref="ClrCodeVersionState"/>).
    /// </para>
    /// </remarks>
    public sealed class ClrMethodIl
    {
        private static readonly OpCode[] SingleByteOpCodes = new OpCode[0x100];
        private static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

        static ClrMethodIl()
        {
            // The opcode tables are built from the framework's own list rather than written out,
            // so they cannot drift from it.
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType != typeof(OpCode))
                    continue;

                var opCode = (OpCode)field.GetValue(null);

                if (opCode.Size == 1)
                    SingleByteOpCodes[opCode.Value & 0xFF] = opCode;
                else
                    TwoByteOpCodes[opCode.Value & 0xFF] = opCode;
            }
        }

        private ClrMethodIl()
        {
        }

        /// <summary>The method this IL belongs to, when it came from reflection.</summary>
        public MethodBase Method { get; private set; }

        /// <summary>The MethodDesc this was decoded from, when it came from one.</summary>
        public ClrMethodDescription Description { get; private set; }

        /// <summary>The raw IL bytes.</summary>
        public byte[] Bytes { get; private set; }

        public IReadOnlyList<ClrIlInstruction> Instructions { get; private set; }

        /// <summary>The method's local variables, in slot order.</summary>
        public IReadOnlyList<LocalVariableInfo> Locals { get; private set; }

        /// <summary>The try/catch/finally regions the method declares.</summary>
        public IReadOnlyList<ExceptionHandlingClause> ExceptionHandlers { get; private set; }

        public int MaxStackSize { get; private set; }

        public bool InitLocals { get; private set; }

        /// <summary>
        /// Decodes <paramref name="method"/>'s IL, or returns null when it has no body - an
        /// abstract method, a P/Invoke, or one the runtime implements itself.
        /// </summary>
        public static ClrMethodIl Of(MethodBase method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));

            MethodBody body;
            try
            {
                body = method.GetMethodBody();
            }
            catch (Exception)
            {
                return null;
            }

            var bytes = body?.GetILAsByteArray();
            if (bytes == null)
                return null;

            var il = new ClrMethodIl
            {
                Method = method,
                Bytes = bytes,
                Locals = body.LocalVariables.ToArray(),
                ExceptionHandlers = SafeClauses(body),
                MaxStackSize = body.MaxStackSize,
                InitLocals = body.InitLocals
            };

            il.Instructions = Decode(method, bytes);

            return il;
        }

        /// <summary>
        /// Decodes a method's IL read out of the module image, reached through its MethodDesc
        /// rather than through reflection.
        /// </summary>
        /// <remarks>
        /// Operands come back as <see cref="ClrIlToken"/> - named from the module's metadata -
        /// instead of resolved members, so nothing here creates a <see cref="Type"/> or a
        /// <see cref="MethodBase"/>. Null when the method has no body, or its module no image.
        /// </remarks>
        public static ClrMethodIl Of(ClrMethodDescription method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));

            var body = method.ReadIl();
            if (body == null)
                return null;

            var metadata = method.Metadata;

            var il = new ClrMethodIl
            {
                Bytes = body.Il,
                Locals = Array.Empty<LocalVariableInfo>(),
                ExceptionHandlers = Array.Empty<ExceptionHandlingClause>(),
                MaxStackSize = body.MaxStack,
                InitLocals = body.InitLocals,
                Description = method
            };

            il.Instructions = Decode(body.Il, (operandType, token) => NameToken(metadata, operandType, token));

            return il;
        }

        private static object NameToken(ClrModuleMetadata metadata, OperandType operandType, int token)
        {
            if (metadata == null)
                return token;

            if (operandType == OperandType.InlineString)
                return metadata.UserString(token) ?? (object)token;

            return new ClrIlToken(token, metadata.TokenName(token));
        }

        private static IReadOnlyList<ExceptionHandlingClause> SafeClauses(MethodBody body)
        {
            try
            {
                return body.ExceptionHandlingClauses.ToArray();
            }
            catch (Exception)
            {
                // A body whose clauses reference types that will not load still has readable IL.
                return Array.Empty<ExceptionHandlingClause>();
            }
        }

        private static List<ClrIlInstruction> Decode(MethodBase method, byte[] il)
        {
            // A token inside a generic method only resolves when the instantiation is supplied.
            var typeArguments = SafeGenericArguments(method.DeclaringType);
            var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
            var module = method.Module;

            return Decode(il, (operandType, token) => operandType == OperandType.InlineString
                ? ResolveString(module, token)
                : ResolveToken(operandType, token, module, typeArguments, methodArguments));
        }

        private static object ResolveString(Module module, int token)
        {
            try
            {
                return module.ResolveString(token);
            }
            catch (Exception)
            {
                return token;
            }
        }

        /// <summary>
        /// Walks the IL, turning each token into an operand with <paramref name="resolve"/>.
        /// </summary>
        /// <remarks>
        /// The two sources of IL differ only in what a token becomes: a reflection member when
        /// the method came from reflection, a name from metadata when it came from a MethodDesc.
        /// Everything else about the decode is identical, so only that step is a parameter.
        /// </remarks>
        private static List<ClrIlInstruction> Decode(byte[] il, Func<OperandType, int, object> resolve)
        {
            var instructions = new List<ClrIlInstruction>();

            var position = 0;

            while (position < il.Length)
            {
                var start = position;

                var code = il[position++];
                var opCode = code == 0xFE && position < il.Length
                    ? TwoByteOpCodes[il[position++]]
                    : SingleByteOpCodes[code];

                var operand = ReadOperand(il, ref position, opCode, resolve);

                instructions.Add(new ClrIlInstruction
                {
                    Offset = start,
                    OpCode = opCode,
                    Operand = operand,
                    Length = position - start
                });
            }

            return instructions;
        }

        private static Type[] SafeGenericArguments(Type type)
        {
            try
            {
                return type != null && type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
            }
            catch (Exception)
            {
                return Type.EmptyTypes;
            }
        }

        private static object ReadOperand(
            byte[] il, ref int position, OpCode opCode, Func<OperandType, int, object> resolve)
        {
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    return null;

                case OperandType.ShortInlineI:
                    return (sbyte)il[position++];

                case OperandType.ShortInlineVar:
                    return il[position++];

                case OperandType.InlineVar:
                    return ReadUInt16(il, ref position);

                case OperandType.InlineI:
                    return ReadInt32(il, ref position);

                case OperandType.InlineI8:
                    return ReadInt64(il, ref position);

                case OperandType.ShortInlineR:
                    return BitConverter.ToSingle(il, Advance(ref position, 4));

                case OperandType.InlineR:
                    return BitConverter.ToDouble(il, Advance(ref position, 8));

                case OperandType.ShortInlineBrTarget:
                {
                    var delta = (sbyte)il[position++];

                    return new ClrIlBranchTarget(position + delta);
                }

                case OperandType.InlineBrTarget:
                {
                    var delta = ReadInt32(il, ref position);

                    return new ClrIlBranchTarget(position + delta);
                }

                case OperandType.InlineSwitch:
                {
                    var count = ReadInt32(il, ref position);
                    var deltas = new int[count];

                    for (var i = 0; i < count; i++)
                        deltas[i] = ReadInt32(il, ref position);

                    // Switch deltas are relative to the end of the whole instruction.
                    var targets = new int[count];
                    for (var i = 0; i < count; i++)
                        targets[i] = position + deltas[i];

                    return targets;
                }

                default:
                {
                    var token = ReadInt32(il, ref position);

                    return resolve(opCode.OperandType, token);
                }
            }
        }

        /// <summary>
        /// Resolves a metadata token to the member it names, falling back to the raw token when
        /// it will not resolve - which happens for tokens that need a context this method does
        /// not have.
        /// </summary>
        private static object ResolveToken(
            OperandType operandType, int token, Module module, Type[] typeArguments, Type[] methodArguments)
        {
            try
            {
                switch (operandType)
                {
                    case OperandType.InlineType:
                        return module.ResolveType(token, typeArguments, methodArguments);

                    case OperandType.InlineMethod:
                        return module.ResolveMethod(token, typeArguments, methodArguments);

                    case OperandType.InlineField:
                        return module.ResolveField(token, typeArguments, methodArguments);

                    case OperandType.InlineTok:
                        return module.ResolveMember(token, typeArguments, methodArguments);

                    case OperandType.InlineSig:
                        return module.ResolveSignature(token);

                    default:
                        return token;
                }
            }
            catch (Exception)
            {
                return token;
            }
        }

        private static int Advance(ref int position, int size)
        {
            var start = position;
            position += size;

            return start;
        }

        private static ushort ReadUInt16(byte[] il, ref int position)
        {
            return BitConverter.ToUInt16(il, Advance(ref position, 2));
        }

        private static int ReadInt32(byte[] il, ref int position)
        {
            return BitConverter.ToInt32(il, Advance(ref position, 4));
        }

        private static long ReadInt64(byte[] il, ref int position)
        {
            return BitConverter.ToInt64(il, Advance(ref position, 8));
        }

        /// <summary>
        /// The method rendered as IL text: signature, locals, then one instruction per line.
        /// </summary>
        public string Dump() => this.Dump(IlDumpStyle.Plain);

        /// <summary>
        /// The method rendered as IL text, optionally coloured with ANSI escape sequences.
        /// </summary>
        /// <remarks>
        /// <see cref="IlDumpStyle.Auto"/> colours only when the output looks like a terminal that
        /// wants it, so the same call is right for a console and for a log file.
        /// </remarks>
        public string Dump(IlDumpStyle style)
        {
            var colouring = IlPalette.IsColouring(style);
            var text = new StringBuilder();

            text.AppendLine(IlPalette.Paint($"// {this.Describe()}", IlPalette.Comment, colouring));

            text.AppendLine(IlPalette.Paint($".maxstack {this.MaxStackSize}", IlPalette.Directive, colouring));

            if (this.Locals.Count > 0)
            {
                text.AppendLine(IlPalette.Paint(
                    $".locals {(this.InitLocals ? "init " : string.Empty)}(", IlPalette.Directive, colouring));

                for (var i = 0; i < this.Locals.Count; i++)
                {
                    var local = this.Locals[i];
                    var pinned = local.IsPinned ? " pinned" : string.Empty;
                    var comma = i == this.Locals.Count - 1 ? string.Empty : ",";

                    text.AppendLine($"    {IlPalette.Paint($"[{local.LocalIndex}]", IlPalette.Number, colouring)} " +
                                    $"{IlPalette.Paint(local.LocalType?.FullName ?? "?", IlPalette.Member, colouring)}{pinned}{comma}");
                }

                text.AppendLine(IlPalette.Paint(")", IlPalette.Directive, colouring));
            }

            foreach (var instruction in this.Instructions)
                text.AppendLine(instruction.Render(colouring));

            foreach (var handler in this.ExceptionHandlers)
            {
                var line = $"// {handler.Flags} try IL_{handler.TryOffset:x4}..IL_{handler.TryOffset + handler.TryLength:x4} " +
                           $"handler IL_{handler.HandlerOffset:x4}..IL_{handler.HandlerOffset + handler.HandlerLength:x4}" +
                           (handler.Flags == ExceptionHandlingClauseOptions.Clause
                               ? $" catch {handler.CatchType?.FullName}"
                               : string.Empty);

                text.AppendLine(IlPalette.Paint(line, IlPalette.Comment, colouring));
            }

            return text.ToString().TrimEnd();
        }

        public override string ToString()
        {
            return $"{this.Describe()} {this.Bytes.Length} bytes, {this.Instructions.Count} instructions";
        }

        /// <summary>
        /// Names the method from whichever source produced the IL - reflection, or the MethodDesc
        /// and its module's metadata.
        /// </summary>
        private string Describe()
        {
            if (this.Method != null)
                return $"{this.Method.DeclaringType?.FullName}::{this.Method.Name}";

            if (this.Description != null)
                return $"{this.Description.DeclaringTypeName}::{this.Description.Name}";

            return "<unnamed>";
        }
    }
}
