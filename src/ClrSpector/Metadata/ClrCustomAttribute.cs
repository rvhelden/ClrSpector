using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>What part of an attribute an argument was written as.</summary>
    public enum ClrAttributeArgumentKind
    {
        /// <summary>A positional argument, matched to a constructor parameter.</summary>
        Constructor,

        /// <summary>A named argument assigning a field.</summary>
        Field,

        /// <summary>A named argument assigning a property.</summary>
        Property
    }

    /// <summary>
    /// The type an attribute argument's value is stored as, which is not always the type the
    /// source wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An attribute blob does not repeat the types of its positional arguments - they come from
    /// the constructor's signature - but it does carry them for named ones, in a small encoding
    /// of its own (ECMA-335 II.23.3) that is not the signature encoding. This is the one shape
    /// both paths produce, so a single reader can decode either.
    /// </para>
    /// <para>
    /// The awkward case is an enum. The blob stores an enum argument as a bare number in the
    /// enum's underlying type and says nothing about what that type is - so a decoder that
    /// assumed <c>int</c> would read the right value for most enums and silently desynchronise
    /// the rest of the blob for a <c>byte</c>- or <c>long</c>-backed one. The underlying type has
    /// to be resolved from the enum's own definition, which is what
    /// <see cref="UnderlyingResolved"/> reports the success of.
    /// </para>
    /// </remarks>
    public sealed class ClrAttributeArgumentType
    {
        /// <summary>How the value is encoded in the blob.</summary>
        public CorElementType ElementType { get; internal set; }

        /// <summary>
        /// The name of the type as written, for the cases where <see cref="ElementType"/> does not
        /// say it: an enum's own name, or <c>System.Type</c>.
        /// </summary>
        public string TypeName { get; internal set; }

        /// <summary>True when the value is an enum stored as its underlying integer.</summary>
        public bool IsEnum { get; internal set; }

        /// <summary>The integer type an enum value is stored as.</summary>
        public CorElementType Underlying { get; internal set; }

        /// <summary>
        /// False when an enum's definition could not be reached, so <see cref="Underlying"/> is
        /// an assumption rather than a reading.
        /// </summary>
        /// <remarks>
        /// A wrong assumption here does not pass silently: the whole blob is required to be
        /// consumed exactly, so guessing the wrong width leaves bytes over or runs short, and
        /// <see cref="ClrCustomAttribute.DecodeError"/> says so.
        /// </remarks>
        public bool UnderlyingResolved { get; internal set; } = true;

        /// <summary>The table and row the enum or class type came from, when it came from one.</summary>
        public MetadataTable TypeTable { get; internal set; }

        /// <summary>The row id within <see cref="TypeTable"/>.</summary>
        public uint TypeRowId { get; internal set; }

        /// <summary>For a single-dimensional array, the type of its elements.</summary>
        public ClrAttributeArgumentType ArrayElement { get; internal set; }

        /// <summary>True when the value is an array of <see cref="ArrayElement"/>.</summary>
        public bool IsArray => this.ElementType == CorElementType.SZARRAY;

        /// <summary>True when the value is boxed, so the blob carries its own type inline.</summary>
        public bool IsBoxed => this.ElementType == CorElementType.OBJECT;

        public override string ToString()
        {
            if (this.IsArray)
                return $"{this.ArrayElement}[]";

            if (this.TypeName != null)
                return this.TypeName;

            switch (this.ElementType)
            {
                case CorElementType.BOOLEAN: return "bool";
                case CorElementType.CHAR: return "char";
                case CorElementType.I1: return "sbyte";
                case CorElementType.U1: return "byte";
                case CorElementType.I2: return "short";
                case CorElementType.U2: return "ushort";
                case CorElementType.I4: return "int";
                case CorElementType.U4: return "uint";
                case CorElementType.I8: return "long";
                case CorElementType.U8: return "ulong";
                case CorElementType.R4: return "float";
                case CorElementType.R8: return "double";
                case CorElementType.STRING: return "string";
                case CorElementType.OBJECT: return "object";
                default: return this.ElementType.ToString();
            }
        }
    }

    /// <summary>
    /// One argument of a custom attribute, with the value the source put there.
    /// </summary>
    public sealed class ClrAttributeArgument
    {
        /// <summary>Whether this was positional, or named a field or a property.</summary>
        public ClrAttributeArgumentKind Kind { get; internal set; }

        /// <summary>The field or property assigned, or null for a positional argument.</summary>
        public string Name { get; internal set; }

        /// <summary>The parameter name a positional argument bound to, when the signature had one.</summary>
        public string ParameterName { get; internal set; }

        /// <summary>The position of a positional argument, or -1 for a named one.</summary>
        public int Position { get; internal set; } = -1;

        /// <summary>How the value is stored.</summary>
        public ClrAttributeArgumentType Type { get; internal set; }

        /// <summary>
        /// The value: a boxed primitive, a <see cref="string"/>, a list of arguments for an
        /// array, or null.
        /// </summary>
        /// <remarks>
        /// A <c>System.Type</c> argument is a string - the type's name as the compiler spelled it
        /// - because that is literally all the blob holds. Turning it into a runtime type would
        /// mean resolving it, which is a different question from what was written.
        /// </remarks>
        public object Value { get; internal set; }

        /// <summary>True when the argument was written as null, including a null array.</summary>
        public bool IsNull => this.Value == null;

        /// <summary>The elements of an array argument, or null when this is not one.</summary>
        public IReadOnlyList<ClrAttributeArgument> Elements =>
            this.Value as IReadOnlyList<ClrAttributeArgument>;

        /// <summary>
        /// The name of the enum member this value equals, when the enum's definition was reachable.
        /// </summary>
        /// <remarks>
        /// Recovered from the enum's own literal fields, so a flags combination comes back as the
        /// members or-ed together rather than as a number.
        /// </remarks>
        public string EnumMemberName { get; internal set; }

        /// <summary>The value written the way C# source would write it.</summary>
        public override string ToString()
        {
            var value = this.Literal();

            return this.Name == null ? value : $"{this.Name} = {value}";
        }

        /// <summary>The value alone, without a named argument's <c>Name =</c> prefix.</summary>
        public string Literal()
        {
            var elements = this.Elements;

            if (elements != null)
            {
                var parts = new List<string>(elements.Count);

                foreach (var element in elements)
                    parts.Add(element.Literal());

                return $"new {this.Type?.ArrayElement}[] {{ {string.Join(", ", parts)} }}";
            }

            if (this.Value == null)
                return "null";

            if (this.EnumMemberName != null)
                return this.EnumMemberName;

            switch (this.Value)
            {
                case string text when this.Type?.ElementType == CorElementType.CLASS:
                    return $"typeof({text})";

                case string text:
                    return "\"" + Escape(text) + "\"";

                case bool flag:
                    return flag ? "true" : "false";

                case char character:
                    return "'" + Escape(character.ToString()) + "'";

                default:
                    return Convert.ToString(this.Value, CultureInfo.InvariantCulture);
            }
        }

        private static string Escape(string text)
        {
            var builder = new StringBuilder(text.Length);

            foreach (var character in text)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append(@"\\"); break;
                    case '\r': builder.Append(@"\r"); break;
                    case '\n': builder.Append(@"\n"); break;
                    case '\t': builder.Append(@"\t"); break;
                    case '\0': builder.Append(@"\0"); break;
                    default: builder.Append(character); break;
                }
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// A custom attribute as it was applied - the attribute type, the constructor chosen, and the
    /// argument values the source wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An attribute is not an object on the heap until something asks for it. What the assembly
    /// holds is a CustomAttribute row: who it was applied to, which constructor was named, and a
    /// blob of the arguments. Reflection's <c>GetCustomAttributes</c> reads that row, constructs
    /// the attribute instance and hands it back - which runs the attribute's own constructor, in
    /// this process. Reading the row instead gives the same information without instantiating
    /// anything, and works for an attribute whose type will not load.
    /// </para>
    /// <para>
    /// The applied-to side of the row is a coded index that can name 22 different tables, so one
    /// reader serves a method, a field, a type, a parameter, a generic parameter, the module and
    /// the assembly itself. Assembly-level attributes - <c>[assembly: InternalsVisibleTo]</c> and
    /// friends - are simply rows whose parent is the single Assembly row.
    /// </para>
    /// <para>
    /// What has no row at all is the pseudo-custom attributes of ECMA-335 II.21 -
    /// <c>[Serializable]</c>, <c>[StructLayout]</c>, <c>[DllImport]</c>, <c>[MethodImpl]</c> and
    /// the rest. The compiler turns those into bits in the defining table, and reflection
    /// synthesises them back on the way out. <see cref="OfImplementationFlags"/> does that for the
    /// one carried in <c>MethodDef.ImplFlags</c>; the others are simply absent, and
    /// <see cref="IsSynthesised"/> is how a caller tells a reconstruction from a row.
    /// </para>
    /// </remarks>
    public sealed class ClrCustomAttribute
    {
        private const ushort Prolog = 0x0001;
        private const byte FieldTag = 0x53;
        private const byte PropertyTag = 0x54;
        private const byte SystemTypeTag = 0x50;
        private const byte BoxedTag = 0x51;
        private const byte EnumTag = 0x55;
        private const byte NullString = 0xFF;

        private static readonly ClrAttributeArgument[] NoArguments = new ClrAttributeArgument[0];

        /// <summary>The CustomAttribute token this was read from, or zero when synthesised.</summary>
        public uint Token { get; private set; }

        /// <summary>The token of whatever the attribute was applied to.</summary>
        public int ParentToken { get; private set; }

        /// <summary>The MethodDef or MemberRef token of the constructor the source named.</summary>
        public int ConstructorToken { get; private set; }

        /// <summary>The attribute type's full metadata name.</summary>
        public string TypeName { get; private set; }

        /// <summary>
        /// The name as source writes it: without the namespace, and without the
        /// <c>Attribute</c> suffix C# lets a use site leave off.
        /// </summary>
        public string ShortName
        {
            get
            {
                var name = this.TypeName;

                if (name == null)
                    return null;

                var dot = name.LastIndexOf('.');

                if (dot >= 0)
                    name = name.Substring(dot + 1);

                return Trimmed(name);
            }
        }

        /// <summary>
        /// The constructor's signature, which is what gives the positional arguments their types.
        /// </summary>
        public ClrMethodSignature Constructor { get; private set; }

        /// <summary>The positional arguments, in order.</summary>
        public IReadOnlyList<ClrAttributeArgument> ConstructorArguments { get; private set; } = NoArguments;

        /// <summary>The named arguments assigning fields and properties.</summary>
        public IReadOnlyList<ClrAttributeArgument> NamedArguments { get; private set; } = NoArguments;

        /// <summary>
        /// Every argument rendered as source would write it, positional ones first.
        /// </summary>
        /// <remarks>
        /// For printing an attribute where the argument's type and origin do not matter. The
        /// typed view is <see cref="ConstructorArguments"/> and <see cref="NamedArguments"/>.
        /// </remarks>
        public IReadOnlyList<string> Arguments =>
            this.ConstructorArguments.Select(a => a.ToString())
                .Concat(this.NamedArguments.Select(a => a.ToString()))
                .ToList();

        /// <summary>The address of the argument blob in the mapped image.</summary>
        public IntPtr ValueAddress { get; private set; }

        /// <summary>The length of the argument blob.</summary>
        public int ValueLength { get; private set; }

        /// <summary>
        /// True when this was reconstructed from table bits rather than read from a row.
        /// </summary>
        public bool IsSynthesised { get; private set; }

        /// <summary>
        /// Why the argument blob could not be decoded, or null when it decoded whole.
        /// </summary>
        /// <remarks>
        /// A failure leaves <see cref="TypeName"/> and <see cref="ConstructorToken"/> intact: the
        /// row is always readable even when the blob is not, and knowing that an attribute of a
        /// given type is applied is worth more than nothing.
        /// </remarks>
        public string DecodeError { get; private set; }

        /// <summary>True when every argument was decoded and the blob was consumed exactly.</summary>
        public bool IsDecoded => this.DecodeError == null;

        /// <summary>True when every argument was decoded; the name <c>IsDecoded</c> also reads it.</summary>
        public bool IsComplete => this.IsDecoded;

        /// <summary>The attribute as C# source would write it.</summary>
        public override string ToString()
        {
            var name = this.TypeName == null
                ? $"0x{this.ConstructorToken:x8}"
                : Trimmed(this.TypeName);

            var parts = this.Arguments;
            var arguments = parts.Count == 0 ? string.Empty : $"({string.Join(", ", parts)})";
            var error = this.DecodeError == null ? string.Empty : $"  /* {this.DecodeError} */";

            return $"[{name}{arguments}]{error}";
        }

        /// <summary>Drops the <c>Attribute</c> suffix a use site is allowed to leave off.</summary>
        private static string Trimmed(string name)
        {
            return name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > 9
                ? name.Substring(0, name.Length - 9)
                : name;
        }

        /// <summary>
        /// The attributes a method's <c>ImplFlags</c> stand for, which have no
        /// <see cref="MetadataTable.CustomAttribute"/> row of their own.
        /// </summary>
        /// <remarks>
        /// <c>[MethodImpl]</c> is a pseudo-custom attribute: the compiler folds it into the
        /// MethodDef row's implementation flags and writes nothing to the CustomAttribute table.
        /// Reconstructing it from those flags is the only way to see it, and what comes back is
        /// marked <see cref="IsSynthesised"/> so it is never mistaken for something that was read.
        /// </remarks>
        public static IEnumerable<ClrCustomAttribute> OfImplementationFlags(ushort flags)
        {
            // The names and values of MethodImplOptions, which is what the source wrote.
            var options = new (ushort Flag, string Name)[]
            {
                (0x0004, "Unmanaged"),
                (0x0008, "NoInlining"),
                (0x0010, "ForwardRef"),
                (0x0020, "Synchronized"),
                (0x0040, "NoOptimization"),
                (0x0080, "PreserveSig"),
                (0x0100, "AggressiveInlining"),
                (0x0200, "AggressiveOptimization"),
                (0x1000, "InternalCall")
            };

            var named = options
                .Where(option => (flags & option.Flag) != 0)
                .Select(option => "MethodImplOptions." + option.Name)
                .ToList();

            if (named.Count > 0)
            {
                yield return Synthesised(
                    "System.Runtime.CompilerServices.MethodImplAttribute",
                    string.Join(" | ", named));
            }

            // CodeType 3 is "runtime", which is what an intrinsic the runtime implements itself
            // looks like; it is worth saying, and it is not a MethodImplOptions value.
            if ((flags & 0x0003) == 0x0003)
                yield return Synthesised("RuntimeImplemented", null);
        }

        /// <summary>One reconstructed attribute, with its rendered argument if it has one.</summary>
        private static ClrCustomAttribute Synthesised(string typeName, string argument)
        {
            var attribute = new ClrCustomAttribute
            {
                TypeName = typeName,
                IsSynthesised = true
            };

            if (argument != null)
            {
                attribute.ConstructorArguments = new[]
                {
                    new ClrAttributeArgument
                    {
                        Kind = ClrAttributeArgumentKind.Constructor,
                        Position = 0,
                        EnumMemberName = argument,
                        Value = argument
                    }
                };
            }

            return attribute;
        }

        /// <summary>Reads one CustomAttribute row and decodes its blob.</summary>
        internal static ClrCustomAttribute Read(ClrModuleMetadata metadata, uint rowId)
        {
            var image = metadata.Image;

            // CustomAttribute: Parent, Type, Value.
            var parent = image.DecodeCoded(
                CodedIndex.HasCustomAttribute,
                image.ReadColumn(MetadataTable.CustomAttribute, rowId, 0));

            var constructor = image.DecodeCoded(
                CodedIndex.CustomAttributeType,
                image.ReadColumn(MetadataTable.CustomAttribute, rowId, 1));

            var constructorToken = (int)(((uint)constructor.Table << 24) | constructor.RowId);

            var attribute = new ClrCustomAttribute
            {
                Token = ((uint)MetadataTable.CustomAttribute << 24) | rowId,
                ParentToken = (int)(((uint)parent.Table << 24) | parent.RowId),
                ConstructorToken = constructorToken,
                TypeName = metadata.DeclaringTypeName(constructorToken),
                Constructor = metadata.TokenSignature(constructorToken)
            };

            try
            {
                var blob = image.Blob(image.ReadColumn(MetadataTable.CustomAttribute, rowId, 2));

                attribute.ValueAddress = blob.Address;
                attribute.ValueLength = blob.Length;

                attribute.Decode(metadata, ref blob);
            }
            catch (Exception error)
            {
                attribute.DecodeError = error.Message;
            }

            return attribute;
        }

        private void Decode(ClrModuleMetadata metadata, ref SignatureBlob blob)
        {
            // A zero-length blob is legal shorthand for "the parameterless constructor, no named
            // arguments", and is how an attribute written without arguments is usually stored.
            if (blob.Length == 0)
                return;

            var prolog = (ushort)blob.ReadFixed(2);

            if (prolog != Prolog)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"A custom attribute blob starts with 0x{prolog:x4} rather than 0x0001.");

            var assumed = false;
            var parameters = this.Constructor?.Parameters;

            if (parameters != null)
            {
                var positional = new List<ClrAttributeArgument>(parameters.Count);

                for (var i = 0; i < parameters.Count; i++)
                {
                    var type = TypeOfSignature(metadata, parameters[i].Type, ref assumed);
                    var argument = ReadArgument(metadata, ref blob, type);

                    argument.Kind = ClrAttributeArgumentKind.Constructor;
                    argument.Position = i;
                    argument.ParameterName = parameters[i].Name;

                    positional.Add(argument);
                }

                this.ConstructorArguments = positional;
            }

            var namedCount = (ushort)blob.ReadFixed(2);

            if (namedCount > 0)
            {
                var named = new List<ClrAttributeArgument>(namedCount);

                for (var i = 0; i < namedCount; i++)
                {
                    var tag = blob.ReadByte();

                    if (tag != FieldTag && tag != PropertyTag)
                        throw new ClrSpectorUnsupportedRuntimeException(
                            $"A named argument is tagged 0x{tag:x2}, which is neither a field " +
                            $"(0x53) nor a property (0x54).");

                    var type = ReadArgumentType(metadata, ref blob, ref assumed);
                    var name = ReadSerString(ref blob);
                    var argument = ReadArgument(metadata, ref blob, type);

                    argument.Kind = tag == FieldTag
                        ? ClrAttributeArgumentKind.Field
                        : ClrAttributeArgumentKind.Property;

                    argument.Name = name;

                    named.Add(argument);
                }

                this.NamedArguments = named;
            }

            // The blob has no terminator, so full consumption is the only check that the widths
            // used were the right ones - which is what catches an enum whose underlying type had
            // to be assumed, and was assumed wrongly.
            if (!blob.IsAtEnd)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"{blob.Remaining} of {blob.Length} blob bytes were left over" +
                    (assumed ? ", after assuming an unreachable enum was int-backed." : "."));
        }

        /// <summary>
        /// The stored shape of an argument whose type came from the constructor's signature.
        /// </summary>
        private static ClrAttributeArgumentType TypeOfSignature(
            ClrModuleMetadata metadata, ClrSignatureType signature, ref bool assumed)
        {
            if (signature == null)
                throw new ClrSpectorUnsupportedRuntimeException(
                    "A custom attribute constructor parameter has no decoded type.");

            var type = new ClrAttributeArgumentType
            {
                ElementType = signature.ElementType,
                TypeTable = signature.TypeTable,
                TypeRowId = signature.TypeRowId
            };

            switch (signature.ElementType)
            {
                case CorElementType.SZARRAY:
                    type.ArrayElement = TypeOfSignature(metadata, signature.Element, ref assumed);
                    break;

                case CorElementType.VALUETYPE:
                    // Only an enum is a legal value-type argument, so the signature naming one is
                    // what says "read the underlying integer here".
                    type.IsEnum = true;
                    type.TypeName = signature.TypeName;

                    ResolveUnderlying(metadata, type, ref assumed);
                    break;

                case CorElementType.CLASS:
                    // ECMA-335 allows only System.Type here, stored as its name.
                    type.TypeName = signature.TypeName;
                    break;
            }

            return type;
        }

        /// <summary>
        /// The stored shape of a named argument, whose type the blob spells out itself.
        /// </summary>
        private static ClrAttributeArgumentType ReadArgumentType(
            ClrModuleMetadata metadata, ref SignatureBlob blob, ref bool assumed)
        {
            var tag = blob.ReadByte();

            switch (tag)
            {
                case (byte)CorElementType.SZARRAY:
                    return new ClrAttributeArgumentType
                    {
                        ElementType = CorElementType.SZARRAY,
                        ArrayElement = ReadArgumentType(metadata, ref blob, ref assumed)
                    };

                case SystemTypeTag:
                    return new ClrAttributeArgumentType
                    {
                        ElementType = CorElementType.CLASS,
                        TypeName = "System.Type"
                    };

                case BoxedTag:
                    return new ClrAttributeArgumentType { ElementType = CorElementType.OBJECT };

                case EnumTag:
                {
                    // The blob names the enum, but by name only - the underlying type still has
                    // to come from the enum's own definition.
                    var type = new ClrAttributeArgumentType
                    {
                        ElementType = CorElementType.VALUETYPE,
                        IsEnum = true,
                        TypeName = ReadSerString(ref blob)
                    };

                    ResolveUnderlyingByName(metadata, type, ref assumed);

                    return type;
                }

                default:
                    return new ClrAttributeArgumentType { ElementType = (CorElementType)tag };
            }
        }

        private static void ResolveUnderlying(
            ClrModuleMetadata metadata, ClrAttributeArgumentType type, ref bool assumed)
        {
            var underlying = metadata.EnumUnderlyingType(type.TypeTable, type.TypeRowId);

            if (underlying == CorElementType.END)
            {
                type.Underlying = CorElementType.I4;
                type.UnderlyingResolved = false;

                assumed = true;
            }
            else
            {
                type.Underlying = underlying;
            }
        }

        private static void ResolveUnderlyingByName(
            ClrModuleMetadata metadata, ClrAttributeArgumentType type, ref bool assumed)
        {
            // A named argument's enum is spelled out reflection-style, so the assembly qualifier
            // has to come off before the name can be matched against a TypeDef.
            var name = type.TypeName;
            var comma = name == null ? -1 : name.IndexOf(',');

            if (comma > 0)
                name = name.Substring(0, comma).Trim();

            if (name != null && metadata.TryFindType(name, out var found))
            {
                type.TypeTable = found.Table;
                type.TypeRowId = found.RowId;

                ResolveUnderlying(metadata, type, ref assumed);

                return;
            }

            // Nothing in this module mentions the enum: setting a property does not emit a
            // reference to the property's type, so a named argument can be the only thing in the
            // assembly that names it. CoreLib is then the place to look, by name.
            var coreLib = ClrModuleMetadata.CoreLib;
            var token = name == null || coreLib == null ? 0 : coreLib.FindTypeDef(name);

            if (token != 0)
            {
                type.TypeTable = MetadataTable.TypeDef;
                type.TypeRowId = token & 0x00FFFFFF;

                ResolveUnderlying(coreLib, type, ref assumed);

                return;
            }

            ResolveUnderlying(metadata, type, ref assumed);
        }

        private static ClrAttributeArgument ReadArgument(
            ClrModuleMetadata metadata, ref SignatureBlob blob, ClrAttributeArgumentType type)
        {
            if (type.IsArray)
            {
                var count = (int)(uint)blob.ReadFixed(4);

                // 0xFFFFFFFF is a null array, which is not the same as an empty one.
                if (count == -1)
                    return new ClrAttributeArgument { Type = type, Value = null };

                if (count < 0 || count > blob.Remaining + 1)
                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"An attribute array claims {(uint)count} elements, which will not fit " +
                        $"in the {blob.Remaining} bytes left.");

                var elements = new List<ClrAttributeArgument>(count);

                for (var i = 0; i < count; i++)
                    elements.Add(ReadArgument(metadata, ref blob, type.ArrayElement));

                return new ClrAttributeArgument { Type = type, Value = elements };
            }

            if (type.IsBoxed)
            {
                // A boxed argument carries its own type ahead of its value.
                var assumed = false;
                var boxed = ReadArgumentType(metadata, ref blob, ref assumed);
                var inner = ReadArgument(metadata, ref blob, boxed);

                inner.Type = boxed;

                return inner;
            }

            var argument = new ClrAttributeArgument { Type = type };
            var element = type.IsEnum ? type.Underlying : type.ElementType;

            switch (element)
            {
                case CorElementType.BOOLEAN: argument.Value = blob.ReadFixed(1) != 0; break;
                case CorElementType.CHAR: argument.Value = (char)blob.ReadFixed(2); break;
                case CorElementType.I1: argument.Value = (sbyte)blob.ReadFixed(1); break;
                case CorElementType.U1: argument.Value = (byte)blob.ReadFixed(1); break;
                case CorElementType.I2: argument.Value = (short)blob.ReadFixed(2); break;
                case CorElementType.U2: argument.Value = (ushort)blob.ReadFixed(2); break;
                case CorElementType.I4: argument.Value = (int)blob.ReadFixed(4); break;
                case CorElementType.U4: argument.Value = (uint)blob.ReadFixed(4); break;
                case CorElementType.I8: argument.Value = (long)blob.ReadFixed(8); break;
                case CorElementType.U8: argument.Value = blob.ReadFixed(8); break;

                case CorElementType.R4:
                    argument.Value = BitConverter.Int32BitsToSingle((int)blob.ReadFixed(4));
                    break;

                case CorElementType.R8:
                    argument.Value = BitConverter.Int64BitsToDouble((long)blob.ReadFixed(8));
                    break;

                case CorElementType.STRING:
                case CorElementType.CLASS:
                    argument.Value = ReadSerString(ref blob);
                    break;

                default:
                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"A custom attribute argument of element type {element} is not a value " +
                        $"ECMA-335 II.23.3 can encode.");
            }

            if (type.IsEnum && argument.Value != null)
            {
                argument.EnumMemberName = metadata.EnumMemberName(
                    type.TypeTable, type.TypeRowId, argument.Value);
            }

            return argument;
        }

        /// <summary>
        /// A SerString: a compressed byte length then UTF-8, with 0xFF meaning null.
        /// </summary>
        /// <remarks>
        /// The length is a byte count, not a character count, and 0xFF is a null string rather
        /// than a 255-byte one - a single-byte compressed integer can only reach 0x7F, so 0xFF is
        /// free to mean something else.
        /// </remarks>
        private static string ReadSerString(ref SignatureBlob blob)
        {
            if (blob.PeekByte() == NullString)
            {
                blob.ReadByte();

                return null;
            }

            var length = (int)blob.ReadCompressedUInt();

            return length == 0 ? string.Empty : blob.ReadUtf8(length);
        }
    }
}