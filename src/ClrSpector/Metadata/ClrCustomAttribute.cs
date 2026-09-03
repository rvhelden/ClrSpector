using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClrSpector
{
    /// <summary>
    /// One attribute on a metadata row, decoded far enough to write back out as source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attributes reach metadata two different ways, and reading only one of them misses half of
    /// them. Most are a <see cref="MetadataTable.CustomAttribute"/> row: a parent, a constructor,
    /// and a blob holding the arguments. A handful the compiler translates into bits instead -
    /// <c>[MethodImpl(MethodImplOptions.NoInlining)]</c> becomes <c>MethodDef.ImplFlags</c>,
    /// <c>[Serializable]</c> becomes a TypeDef flag - and those have no row at all. Both kinds
    /// come back from here, and nothing says which was which, because the source did not.
    /// </para>
    /// <para>
    /// The blob is only decodable with the constructor's signature to hand, because the blob
    /// itself records no types: an argument is however many bytes its parameter's type says.
    /// Where a value cannot be decoded - an enum whose underlying type is not in the signature,
    /// an array of them - the argument is rendered as an ellipsis rather than guessed at, and
    /// <see cref="IsComplete"/> says so.
    /// </para>
    /// </remarks>
    public sealed class ClrCustomAttribute
    {
        /// <summary>An argument list that could not be decoded to the end.</summary>
        private const string Truncated = "...";

        internal ClrCustomAttribute(string typeName, IReadOnlyList<string> arguments, bool complete)
        {
            this.TypeName = typeName;
            this.Arguments = arguments ?? Array.Empty<string>();
            this.IsComplete = complete;
        }

        /// <summary>The attribute type's name, as the metadata spells it.</summary>
        public string TypeName { get; }

        /// <summary>
        /// The arguments as source would write them, positional ones first and then the named
        /// ones as <c>Name = value</c>.
        /// </summary>
        public IReadOnlyList<string> Arguments { get; }

        /// <summary>False when an argument could not be decoded and stands as an ellipsis.</summary>
        public bool IsComplete { get; }

        /// <summary>
        /// The name as source writes it: without its namespace, and without the
        /// <c>Attribute</c> suffix C# lets you leave off.
        /// </summary>
        public string ShortName
        {
            get
            {
                var name = this.TypeName ?? "?";
                var cut = name.LastIndexOfAny(new[] { '.', '+' });

                if (cut >= 0)
                    name = name.Substring(cut + 1);

                return name.Length > 9 && name.EndsWith("Attribute", StringComparison.Ordinal)
                    ? name.Substring(0, name.Length - 9)
                    : name;
            }
        }

        /// <summary>The attribute as it would be written in source, brackets included.</summary>
        public override string ToString()
        {
            return this.Arguments.Count == 0
                ? $"[{this.ShortName}]"
                : $"[{this.ShortName}({string.Join(", ", this.Arguments)})]";
        }

        /// <summary>
        /// The attributes a method's <c>ImplFlags</c> stand for, which have no
        /// <see cref="MetadataTable.CustomAttribute"/> row of their own.
        /// </summary>
        internal static IEnumerable<ClrCustomAttribute> OfImplementationFlags(ushort flags)
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

            var named = options.Where(option => (flags & option.Flag) != 0).Select(option => option.Name).ToList();

            if (named.Count > 0)
            {
                yield return new ClrCustomAttribute(
                    "System.Runtime.CompilerServices.MethodImplAttribute",
                    new[] { "MethodImplOptions." + string.Join(" | MethodImplOptions.", named) },
                    true);
            }

            // CodeType 3 is "runtime", which is what an intrinsic the runtime implements itself
            // looks like; it is worth saying, and it is not a MethodImplOptions value.
            if ((flags & 0x0003) == 0x0003)
                yield return new ClrCustomAttribute("RuntimeImplemented", Array.Empty<string>(), true);
        }

        /// <summary>
        /// Decodes one attribute's blob against its constructor's signature.
        /// </summary>
        /// <remarks>
        /// The blob is a two-byte prolog, then one value per constructor parameter with no types
        /// of its own, then the named arguments - which do carry their types, because a property
        /// name alone would not say how many bytes follow.
        /// </remarks>
        internal static ClrCustomAttribute Decode(
            string typeName, SignatureBlob blob, ClrMethodSignature constructor, MetadataImage image)
        {
            var arguments = new List<string>();

            try
            {
                if (blob.Remaining < 2 || blob.ReadByte() != 0x01 || blob.ReadByte() != 0x00)
                    return new ClrCustomAttribute(typeName, Array.Empty<string>(), constructor == null);

                var parameters = constructor?.Parameters ??
                                 (IReadOnlyList<ClrMethodParameter>)Array.Empty<ClrMethodParameter>();

                foreach (var parameter in parameters)
                {
                    var value = ReadValue(ref blob, parameter.Type, image);

                    if (value == null)
                        return new ClrCustomAttribute(typeName, Append(arguments, Truncated), false);

                    arguments.Add(value);
                }

                if (blob.Remaining < 2)
                    return new ClrCustomAttribute(typeName, arguments, true);

                var named = blob.ReadByte() | (blob.ReadByte() << 8);

                for (var i = 0; i < named; i++)
                {
                    var argument = ReadNamed(ref blob, image);

                    if (argument == null)
                        return new ClrCustomAttribute(typeName, Append(arguments, Truncated), false);

                    arguments.Add(argument);
                }

                return new ClrCustomAttribute(typeName, arguments, true);
            }
            catch (Exception)
            {
                // A blob that stops making sense has still told us the attribute is there.
                return new ClrCustomAttribute(typeName, Append(arguments, Truncated), false);
            }
        }

        private static IReadOnlyList<string> Append(List<string> arguments, string last)
        {
            arguments.Add(last);

            return arguments;
        }

        /// <summary>One named argument: <c>Property = value</c>.</summary>
        private static string ReadNamed(ref SignatureBlob blob, MetadataImage image)
        {
            // 0x53 is a field, 0x54 a property; either way a name and a type follow.
            var kind = blob.ReadByte();

            if (kind != 0x53 && kind != 0x54)
                return null;

            var type = (CorElementType)blob.ReadByte();

            // An enum names its type before its name, so that the reader knows its width - which
            // it still does not, since the width is in the enum's own definition.
            if ((byte)type == 0x55)
                ReadString(ref blob);

            var name = ReadString(ref blob);

            if (name == null)
                return null;

            var value = (byte)type == 0x55 ? ReadPrimitive(ref blob, CorElementType.I4) : ReadByElement(ref blob, type);

            return value == null ? null : $"{name} = {value}";
        }

        /// <summary>A value whose type comes from the constructor's signature.</summary>
        private static string ReadValue(ref SignatureBlob blob, ClrSignatureType type, MetadataImage image)
        {
            if (type == null)
                return null;

            switch (type.ElementType)
            {
                case CorElementType.VALUETYPE:
                {
                    // An enum: the signature names the type but not what it is based on, and
                    // the blob holds only the value. Four bytes is what the compiler emits for
                    // every enum whose base type it was not told to change.
                    var value = ReadPrimitive(ref blob, CorElementType.I4);
                    var name = type.TypeName;

                    return name == null || value == null ? value : Enumerated(name, value);
                }

                case CorElementType.CLASS:
                    // The only class a constructor argument can be is System.Type.
                    return type.TypeName == "System.Type" ? Typed(ReadString(ref blob)) : null;

                default:
                    return ReadByElement(ref blob, type.ElementType);
            }
        }

        private static string ReadByElement(ref SignatureBlob blob, CorElementType type)
        {
            switch (type)
            {
                case CorElementType.STRING:
                {
                    var text = ReadString(ref blob);

                    return text == null ? "null" : Quoted(text);
                }

                case (CorElementType)0x50:
                    return Typed(ReadString(ref blob));

                case CorElementType.OBJECT:
                {
                    // A boxed value says what it is, then is it.
                    var boxed = (CorElementType)blob.ReadByte();

                    return ReadByElement(ref blob, boxed);
                }

                default:
                    return ReadPrimitive(ref blob, type);
            }
        }

        private static string ReadPrimitive(ref SignatureBlob blob, CorElementType type)
        {
            switch (type)
            {
                case CorElementType.BOOLEAN:
                    return blob.ReadByte() != 0 ? "true" : "false";

                case CorElementType.CHAR:
                    return "'" + (char)(blob.ReadByte() | (blob.ReadByte() << 8)) + "'";

                case CorElementType.I1:
                    return ((sbyte)blob.ReadByte()).ToString(CultureInfo.InvariantCulture);

                case CorElementType.U1:
                    return blob.ReadByte().ToString(CultureInfo.InvariantCulture);

                case CorElementType.I2:
                case CorElementType.U2:
                    return (blob.ReadByte() | (blob.ReadByte() << 8)).ToString(CultureInfo.InvariantCulture);

                case CorElementType.I4:
                case CorElementType.U4:
                    return ReadInt32(ref blob).ToString(CultureInfo.InvariantCulture);

                case CorElementType.I8:
                case CorElementType.U8:
                {
                    long low = (uint)ReadInt32(ref blob);
                    long high = (uint)ReadInt32(ref blob);

                    return ((high << 32) | low).ToString(CultureInfo.InvariantCulture);
                }

                case CorElementType.R4:
                    return BitConverter.Int32BitsToSingle(ReadInt32(ref blob))
                        .ToString("R", CultureInfo.InvariantCulture);

                case CorElementType.R8:
                {
                    long low = (uint)ReadInt32(ref blob);
                    long high = (uint)ReadInt32(ref blob);

                    return BitConverter.Int64BitsToDouble((high << 32) | low)
                        .ToString("R", CultureInfo.InvariantCulture);
                }

                default:
                    return null;
            }
        }

        private static int ReadInt32(ref SignatureBlob blob)
        {
            return blob.ReadByte() | (blob.ReadByte() << 8) | (blob.ReadByte() << 16) | (blob.ReadByte() << 24);
        }

        /// <summary>
        /// A length-prefixed UTF-8 string, or null for the 0xFF that means no string at all.
        /// </summary>
        private static string ReadString(ref SignatureBlob blob)
        {
            if (blob.PeekByte() == 0xFF)
            {
                blob.ReadByte();

                return null;
            }

            var length = (int)blob.ReadCompressedUInt();

            if (length == 0)
                return string.Empty;

            var bytes = new byte[length];

            for (var i = 0; i < length; i++)
                bytes[i] = blob.ReadByte();

            return Encoding.UTF8.GetString(bytes);
        }

        private static string Quoted(string text)
        {
            return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string Typed(string name)
        {
            return name == null ? "null" : $"typeof({Shorten(name)})";
        }

        /// <summary>
        /// An enum value written against its type, which is what source did - though only the
        /// number is in the blob, so the name of the member is not recoverable from here.
        /// </summary>
        private static string Enumerated(string type, string value)
        {
            return $"({Shorten(type)}){value}";
        }

        private static string Shorten(string name)
        {
            var comma = name.IndexOf(',');

            if (comma > 0)
                name = name.Substring(0, comma);

            var cut = name.LastIndexOfAny(new[] { '.', '+' });

            return cut < 0 ? name : name.Substring(cut + 1);
        }
    }
}