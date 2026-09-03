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
        private readonly ClrModuleMetadata metadata;

        private ClrMethodSignature signature;

        private bool signatureRead;

        internal ClrIlToken(int token, string name, ClrModuleMetadata metadata = null)
        {
            this.Token = token;
            this.Name = name;
            this.metadata = metadata;
        }

        public int Token { get; }

        /// <summary>What the token names, or its hexadecimal value when it does not resolve.</summary>
        public string Name { get; }

        /// <summary>
        /// The signature of the callable this token names, or null when it names something else
        /// or the module's metadata is not readable.
        /// </summary>
        /// <remarks>
        /// Decoded on first use rather than during the IL walk: most tokens in a body are types
        /// and fields, and only a call site is ever asked what it consumes.
        /// </remarks>
        public ClrMethodSignature Signature
        {
            get
            {
                if (!this.signatureRead)
                {
                    this.signature = this.metadata?.TokenSignature(this.Token);
                    this.signatureRead = true;
                }

                return this.signature;
            }
        }

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

        /// <summary>
        /// The module metadata this method was read through, for either source of IL - which is
        /// where everything about the method that is not in its body lives.
        /// </summary>
        public ClrModuleMetadata Metadata { get; private set; }

        /// <summary>The MethodDef token of the method, or zero when it has no row.</summary>
        public uint MetadataToken { get; private set; }

        /// <summary>The raw IL bytes.</summary>
        public byte[] Bytes { get; private set; }

        public IReadOnlyList<ClrIlInstruction> Instructions { get; private set; }

        /// <summary>
        /// The method's local variables as reflection reports them, in slot order. Always empty
        /// for IL read from a MethodDesc; use <see cref="LocalVariables"/> for the slots of
        /// either source.
        /// </summary>
        public IReadOnlyList<LocalVariableInfo> Locals { get; private set; }

        /// <summary>
        /// The method's local variable slots, from whichever source the IL came from -
        /// reflection's locals, or the body's own local signature decoded from metadata.
        /// </summary>
        public IReadOnlyList<ClrIlLocal> LocalVariables { get; private set; }

        /// <summary>
        /// The attributes the method was declared with, rows and pseudo-custom attributes alike.
        /// </summary>
        /// <remarks>
        /// Read from the module's metadata for either source of IL, because that is where they
        /// are: <c>[MethodImpl]</c> is folded into the MethodDef row's own flags rather than
        /// written to the CustomAttribute table, so both places have to be read to see what
        /// source wrote. See <see cref="ClrCustomAttribute"/>.
        /// </remarks>
        public IReadOnlyList<ClrCustomAttribute> Attributes { get; private set; }
            = Array.Empty<ClrCustomAttribute>();

        /// <summary>
        /// The MethodDef row's own flags - the accessibility and the virtualness that source
        /// spells as keywords. Zero when there is no row to read them from.
        /// </summary>
        /// <remarks>
        /// These are MethodAttributes: the low three bits are the accessibility, and the rest
        /// are the modifiers. They are not attributes in the CustomAttribute sense and are not
        /// reported as such; they are what <c>public</c> and <c>virtual</c> compile into.
        /// </remarks>
        public ushort DeclarationFlags { get; private set; }

        /// <summary>
        /// The try/catch/finally regions the method declares, as reflection reports them. Always
        /// empty for IL read from a MethodDesc, which never goes through reflection; use
        /// <see cref="ExceptionRegions"/> for the regions of either source.
        /// </summary>
        public IReadOnlyList<ExceptionHandlingClause> ExceptionHandlers { get; private set; }

        /// <summary>
        /// The try/catch/finally regions the method declares, from whichever source the IL came
        /// from - reflection's clauses, or the exception table read out of the body in memory.
        /// </summary>
        public IReadOnlyList<ClrIlExceptionRegion> ExceptionRegions { get; private set; }

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
                ExceptionRegions = Array.Empty<ClrIlExceptionRegion>(),
                MaxStackSize = body.MaxStackSize,
                InitLocals = body.InitLocals
            };

            il.Instructions = Decode(method, bytes);
            il.ExceptionRegions = il.ExceptionHandlers.Select(RegionOf).ToArray();
            il.LocalVariables = il.Locals.Select(ClrIlLocal.Of).ToArray();

            NameLocals(il.LocalVariables, ImageBaseOf(method), (uint)method.MetadataToken);

            var image = ClrModuleMetadata.AtImageBase(ImageBaseOf(method));

            il.Metadata = image;
            il.MetadataToken = (uint)method.MetadataToken;
            il.Attributes = AttributesOf(image, (uint)method.MetadataToken);
            il.DeclarationFlags = DeclarationFlagsOf(image, (uint)method.MetadataToken);

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
                ExceptionRegions = NameCaughtTypes(body.ExceptionRegions, metadata),
                LocalVariables = metadata?.LocalSignature((int)body.LocalSignatureToken)
                                 ?? (IReadOnlyList<ClrIlLocal>)Array.Empty<ClrIlLocal>(),
                MaxStackSize = body.MaxStack,
                InitLocals = body.InitLocals,
                Description = method
            };

            il.Instructions = Decode(body.Il, (operandType, token) => NameToken(metadata, operandType, token));

            NameLocals(
                il.LocalVariables, metadata?.ImageBase ?? IntPtr.Zero, method.MetadataToken);

            il.Metadata = metadata;
            il.MetadataToken = method.MetadataToken;
            il.Attributes = AttributesOf(metadata, method.MetadataToken);
            il.DeclarationFlags = DeclarationFlagsOf(metadata, method.MetadataToken);

            return il;
        }

        /// <summary>
        /// Reflection's clause in the form both sources share.
        /// </summary>
        /// <remarks>
        /// <see cref="ExceptionHandlingClause.CatchType"/> and
        /// <see cref="ExceptionHandlingClause.FilterOffset"/> each throw unless the clause is of
        /// the matching kind, so neither is read without checking which kind it is first.
        /// </remarks>
        private static ClrIlExceptionRegion RegionOf(ExceptionHandlingClause clause)
        {
            var kind = (ClrIlExceptionRegionKind)clause.Flags;

            var region = new ClrIlExceptionRegion(
                kind,
                clause.TryOffset,
                clause.TryLength,
                clause.HandlerOffset,
                clause.HandlerLength,
                kind == ClrIlExceptionRegionKind.Filter ? clause.FilterOffset : 0,
                kind == ClrIlExceptionRegionKind.Catch ? (uint)(clause.CatchType?.MetadataToken ?? 0) : 0);

            if (kind == ClrIlExceptionRegionKind.Catch)
                region.CatchTypeName = clause.CatchType?.FullName;

            return region;
        }

        /// <summary>
        /// Names the types the typed catches in <paramref name="regions"/> catch, from the
        /// module's own metadata - the body carries only their tokens.
        /// </summary>
        private static IReadOnlyList<ClrIlExceptionRegion> NameCaughtTypes(
            IReadOnlyList<ClrIlExceptionRegion> regions, ClrModuleMetadata metadata)
        {
            if (metadata == null)
                return regions;

            foreach (var region in regions)
            {
                if (region.Kind == ClrIlExceptionRegionKind.Catch && region.CatchTypeToken != 0)
                    region.CatchTypeName = metadata.TokenName((int)region.CatchTypeToken);
            }

            return regions;
        }

        /// <summary>The MethodDef row's Flags column, or zero when there is no row.</summary>
        private static ushort DeclarationFlagsOf(ClrModuleMetadata metadata, uint methodDefToken)
        {
            var rowId = methodDefToken & 0x00FFFFFF;

            if (metadata == null || rowId == 0
                || rowId > (uint)metadata.Image.RowCount(MetadataTable.MethodDef))
            {
                return 0;
            }

            // MethodDef: RVA, ImplFlags, Flags, Name, Signature, ParamList.
            return (ushort)metadata.Image.ReadColumn(MetadataTable.MethodDef, rowId, 2);
        }

        /// <summary>
        /// The attributes on a MethodDef token: the CustomAttribute rows, then the ones the
        /// row's own implementation flags stand for.
        /// </summary>
        private static IReadOnlyList<ClrCustomAttribute> AttributesOf(
            ClrModuleMetadata metadata, uint methodDefToken)
        {
            if (metadata == null)
                return Array.Empty<ClrCustomAttribute>();

            try
            {
                var attributes = metadata.CustomAttributes((int)methodDefToken).ToList();
                var rowId = methodDefToken & 0x00FFFFFF;

                if (rowId == 0 || rowId > (uint)metadata.Image.RowCount(MetadataTable.MethodDef))
                    return attributes;

                // MethodDef: RVA, ImplFlags, Flags, Name, Signature, ParamList.
                var flags = (ushort)metadata.Image.ReadColumn(MetadataTable.MethodDef, rowId, 1);

                if (flags != 0)
                    attributes.AddRange(ClrCustomAttribute.OfImplementationFlags(flags));

                return attributes;
            }
            catch (Exception)
            {
                // A listing without its attributes is still a listing.
                return Array.Empty<ClrCustomAttribute>();
            }
        }

        /// <summary>
        /// Gives the local slots the names the module's PDB has for them, when there is one.
        /// </summary>
        /// <remarks>
        /// Nothing else in a method's own data has them, so this is the only step in reading IL
        /// that looks outside the mapped image - and the only one that may find nothing, which
        /// is why every local keeps a slot-numbered name to fall back on.
        /// </remarks>
        private static void NameLocals(
            IReadOnlyList<ClrIlLocal> locals, IntPtr imageBase, uint methodDefToken)
        {
            if (locals.Count == 0)
                return;

            var names = ClrModuleSymbols.AtImageBase(imageBase)?.LocalNames(methodDefToken);

            if (names == null || names.Count == 0)
                return;

            foreach (var local in locals)
            {
                if (names.TryGetValue(local.Index, out var name))
                    local.Name = name;
            }

            Disambiguate(locals);
        }

        /// <summary>
        /// Numbers apart the slots that share a name, so a copy between two of them does not
        /// read as a copy to itself.
        /// </summary>
        /// <remarks>
        /// A PDB names a slot per lexical scope, and two scopes that never overlap can each
        /// declare an <c>i</c> - as the arms of a switch over patterns do, one variable per arm.
        /// The compiler gave them different slots, and a listing that calls both <c>i</c> would
        /// print <c>i = i</c> for the copy between them.
        /// </remarks>
        private static void Disambiguate(IReadOnlyList<ClrIlLocal> locals)
        {
            var seen = new Dictionary<string, int>();

            foreach (var local in locals)
            {
                if (local.Name == null)
                    continue;

                if (!seen.TryGetValue(local.Name, out var count))
                {
                    seen[local.Name] = 1;

                    continue;
                }

                seen[local.Name] = count + 1;
                local.Name = $"{local.Name}_{count}";
            }
        }

        /// <summary>
        /// Where <paramref name="method"/>'s module is mapped, or zero for a module with no
        /// image - one built at runtime.
        /// </summary>
        private static IntPtr ImageBaseOf(MethodBase method)
        {
            try
            {
                var module = method.Module;

                return module == null || module.Assembly.IsDynamic
                    ? IntPtr.Zero
                    : System.Runtime.InteropServices.Marshal.GetHINSTANCE(module);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        private static object NameToken(ClrModuleMetadata metadata, OperandType operandType, int token)
        {
            if (metadata == null)
                return token;

            if (operandType == OperandType.InlineString)
                return metadata.UserString(token) ?? (object)token;

            return new ClrIlToken(token, metadata.TokenName(token), metadata);
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
        /// The same method as low-level C#: the stack machine undone into expressions, with the
        /// control flow left as it is. See <see cref="ClrMethodCSharp"/> for what the projection
        /// does and does not claim.
        /// </summary>
        public ClrMethodCSharp ToCSharp(ClrCSharpForm form = ClrCSharpForm.Faithful)
        {
            return ClrMethodCSharp.Of(this, form);
        }

        /// <summary>The C# projection rendered as text, coloured like an IL dump.</summary>
        public string DumpCSharp(
            IlDumpStyle style = IlDumpStyle.Plain, ClrCSharpForm form = ClrCSharpForm.Faithful)
        {
            return this.ToCSharp(form).Dump(style);
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

            if (this.LocalVariables.Count > 0)
            {
                text.AppendLine(IlPalette.Paint(
                    $".locals {(this.InitLocals ? "init " : string.Empty)}(", IlPalette.Directive, colouring));

                for (var i = 0; i < this.LocalVariables.Count; i++)
                {
                    var local = this.LocalVariables[i];
                    var pinned = local.IsPinned ? " pinned" : string.Empty;
                    var comma = i == this.LocalVariables.Count - 1 ? string.Empty : ",";

                    var named = local.Name == null
                        ? string.Empty
                        : " " + IlPalette.Paint(local.Name, IlPalette.Opcode, colouring);

                    text.AppendLine($"    {IlPalette.Paint($"[{local.Index}]", IlPalette.Number, colouring)} " +
                                    $"{IlPalette.Paint(local.TypeName ?? "?", IlPalette.Member, colouring)}" +
                                    $"{named}{pinned}{comma}");
                }

                text.AppendLine(IlPalette.Paint(")", IlPalette.Directive, colouring));
            }

            foreach (var instruction in this.Instructions)
                text.AppendLine(instruction.Render(colouring));

            foreach (var region in this.ExceptionRegions)
                text.AppendLine(IlPalette.Paint("// " + region, IlPalette.Comment, colouring));

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
