using System;
using System.Collections.Generic;
using System.Text;

namespace ClrSpector
{
    /// <summary>
    /// One type as a signature records it: an element type, plus whatever that element type
    /// carries with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a tree, because signature types are. <c>ref int[][]</c> is a BYREF wrapping an
    /// SZARRAY wrapping an SZARRAY wrapping I4, and <c>Dictionary&lt;string, int[]&gt;</c> is a
    /// GENERICINST over a CLASS with two argument subtrees. Walking
    /// <see cref="ElementType"/> and following <see cref="Element"/>,
    /// <see cref="GenericArguments"/> and <see cref="CustomModifiers"/> is the whole of it.
    /// </para>
    /// <para>
    /// A signature names types by token, not by name, so <see cref="TypeName"/> is resolved from
    /// the declaring module's metadata when it can be. It is a convenience over the tree, not the
    /// substance of it.
    /// </para>
    /// <para>
    /// Generic parameters stay as <see cref="CorElementType.VAR"/> and
    /// <see cref="CorElementType.MVAR"/> with an index. A MethodDef signature is the *open*
    /// definition - substituting the arguments of a particular instantiation needs the
    /// instantiation, which lives on the MethodDesc rather than in metadata.
    /// </para>
    /// </remarks>
    public sealed class ClrSignatureType
    {
        private static readonly ClrSignatureType[] NoArguments = new ClrSignatureType[0];

        private static readonly ClrCustomModifier[] NoModifiers = new ClrCustomModifier[0];

        internal ClrSignatureType(CorElementType elementType)
        {
            this.ElementType = elementType;
            this.GenericArguments = NoArguments;
            this.CustomModifiers = NoModifiers;
        }

        /// <summary>What kind of type this is.</summary>
        public CorElementType ElementType { get; }

        /// <summary>
        /// The type this one wraps: the element of an array, the target of a pointer or byref,
        /// the pinned type. Null for everything else.
        /// </summary>
        public ClrSignatureType Element { get; internal set; }

        /// <summary>
        /// The generic type being instantiated, for <see cref="CorElementType.GENERICINST"/>.
        /// </summary>
        public ClrSignatureType GenericType { get; internal set; }

        /// <summary>The instantiation's arguments; empty unless this is a GENERICINST.</summary>
        public IReadOnlyList<ClrSignatureType> GenericArguments { get; internal set; }

        /// <summary>
        /// Modifiers attached to this type, from <c>modreq</c> and <c>modopt</c>.
        /// </summary>
        public IReadOnlyList<ClrCustomModifier> CustomModifiers { get; internal set; }

        /// <summary>The table a CLASS or VALUETYPE token names.</summary>
        public MetadataTable TypeTable { get; internal set; }

        /// <summary>The row id within <see cref="TypeTable"/>; zero when there is no token.</summary>
        public uint TypeRowId { get; internal set; }

        /// <summary>
        /// The token's name, when the module's metadata could resolve it. Null otherwise.
        /// </summary>
        public string TypeName { get; internal set; }

        /// <summary>
        /// The parameter's index, for <see cref="CorElementType.VAR"/> (a type parameter) and
        /// <see cref="CorElementType.MVAR"/> (a method's own).
        /// </summary>
        public int GenericParameterIndex { get; internal set; }

        /// <summary>An array's dimension count. One for an SZARRAY.</summary>
        public int Rank { get; internal set; } = 1;

        /// <summary>A multi-dimension array's declared sizes, where it has them.</summary>
        public IReadOnlyList<int> ArraySizes { get; internal set; }

        /// <summary>A multi-dimension array's declared lower bounds, where it has them.</summary>
        public IReadOnlyList<int> ArrayLowerBounds { get; internal set; }

        /// <summary>The signature of a <see cref="CorElementType.FNPTR"/>.</summary>
        public ClrMethodSignature FunctionPointer { get; internal set; }

        /// <summary>True for a byref - a <c>ref</c> parameter or return.</summary>
        public bool IsByRef => this.ElementType == CorElementType.BYREF;

        /// <summary>True when this is one of the built-in element types with no token.</summary>
        public bool IsPrimitive
        {
            get
            {
                switch (this.ElementType)
                {
                    case CorElementType.BOOLEAN:
                    case CorElementType.CHAR:
                    case CorElementType.I1:
                    case CorElementType.U1:
                    case CorElementType.I2:
                    case CorElementType.U2:
                    case CorElementType.I4:
                    case CorElementType.U4:
                    case CorElementType.I8:
                    case CorElementType.U8:
                    case CorElementType.R4:
                    case CorElementType.R8:
                    case CorElementType.I:
                    case CorElementType.U:
                        return true;

                    default:
                        return false;
                }
            }
        }

        /// <summary>The C# name of a built-in element type, or null when it has none.</summary>
        public string KeywordName
        {
            get
            {
                switch (this.ElementType)
                {
                    case CorElementType.VOID: return "void";
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
                    case CorElementType.I: return "nint";
                    case CorElementType.U: return "nuint";
                    case CorElementType.TYPEDBYREF: return "System.TypedReference";
                    default: return null;
                }
            }
        }

        /// <summary>
        /// The MethodTable a substituted type variable resolved to, when this node came from
        /// closing an open signature. Zero otherwise.
        /// </summary>
        public IntPtr SubstitutedMethodTable { get; internal set; }

        /// <summary>
        /// A copy of this type with its type variables replaced by the given arguments.
        /// </summary>
        /// <remarks>
        /// A MethodDef signature is the open definition: <c>!0</c> is the declaring type's first
        /// parameter and <c>!!0</c> the method's own. Closing it means substituting the actual
        /// instantiation, which is not in metadata at all - a type's arguments come from its
        /// MethodTable's PerInstInfo and a generic method's from its own MethodDesc.
        ///
        /// An argument that is missing, or is itself a type variable rather than a real type, is
        /// left as it was rather than guessed at - so an open definition closes to itself, which
        /// is the honest result.
        /// </remarks>
        public ClrSignatureType WithArguments(
            IReadOnlyList<ClrSignatureType> typeArguments,
            IReadOnlyList<ClrSignatureType> methodArguments)
        {
            switch (this.ElementType)
            {
                case CorElementType.VAR:
                    return Pick(typeArguments, this.GenericParameterIndex) ?? this;

                case CorElementType.MVAR:
                    return Pick(methodArguments, this.GenericParameterIndex) ?? this;
            }

            var element = this.Element?.WithArguments(typeArguments, methodArguments);
            var open = this.GenericType?.WithArguments(typeArguments, methodArguments);

            ClrSignatureType[] arguments = null;
            if (this.GenericArguments.Count > 0)
            {
                arguments = new ClrSignatureType[this.GenericArguments.Count];
                for (var i = 0; i < arguments.Length; i++)
                    arguments[i] = this.GenericArguments[i].WithArguments(typeArguments, methodArguments);
            }

            // Nothing underneath changed, so there is nothing to copy.
            if (ReferenceEquals(element, this.Element)
                && ReferenceEquals(open, this.GenericType)
                && arguments == null)
            {
                return this;
            }

            return new ClrSignatureType(this.ElementType)
            {
                Element = element,
                GenericType = open,
                GenericArguments = arguments ?? this.GenericArguments,
                CustomModifiers = this.CustomModifiers,
                TypeTable = this.TypeTable,
                TypeRowId = this.TypeRowId,
                TypeName = this.TypeName,
                GenericParameterIndex = this.GenericParameterIndex,
                Rank = this.Rank,
                ArraySizes = this.ArraySizes,
                ArrayLowerBounds = this.ArrayLowerBounds,
                FunctionPointer = this.FunctionPointer,
                SubstitutedMethodTable = this.SubstitutedMethodTable
            };
        }

        private static ClrSignatureType Pick(IReadOnlyList<ClrSignatureType> arguments, int index)
        {
            return arguments != null && index >= 0 && index < arguments.Count
                ? arguments[index]
                : null;
        }

        /// <summary>
        /// A node standing for a type reached by MethodTable rather than by token - what a
        /// substituted type variable becomes.
        /// </summary>
        internal static ClrSignatureType ForMethodTable(ClrMethodTable table)
        {
            if (table == null)
                return null;

            var element = table.IsValueType ? CorElementType.VALUETYPE : CorElementType.CLASS;

            return new ClrSignatureType(element)
            {
                TypeName = table.MetadataName,
                SubstitutedMethodTable = table.Address
            };
        }

        /// <summary>Renders the type the way source would spell it.</summary>
        public override string ToString()
        {
            var text = new StringBuilder();
            this.Append(text);

            return text.ToString();
        }

        private void Append(StringBuilder text)
        {
            var keyword = this.KeywordName;
            if (keyword != null)
            {
                text.Append(keyword);
                this.AppendModifiers(text);
                return;
            }

            switch (this.ElementType)
            {
                case CorElementType.CLASS:
                case CorElementType.VALUETYPE:
                    text.Append(this.TypeName ?? $"{this.TypeTable}({this.TypeRowId})");
                    break;

                case CorElementType.BYREF:
                    text.Append("ref ");
                    this.Element?.Append(text);
                    break;

                case CorElementType.PTR:
                    this.Element?.Append(text);
                    text.Append('*');
                    break;

                case CorElementType.PINNED:
                    text.Append("pinned ");
                    this.Element?.Append(text);
                    break;

                case CorElementType.SZARRAY:
                    this.Element?.Append(text);
                    text.Append("[]");
                    break;

                case CorElementType.ARRAY:
                    this.Element?.Append(text);
                    text.Append('[').Append(new string(',', Math.Max(this.Rank - 1, 0))).Append(']');
                    break;

                case CorElementType.GENERICINST:
                {
                    // A generic type's metadata name carries its arity - "List`1" - which is
                    // redundant once the arguments follow it, so it is dropped for reading.
                    var open = this.GenericType?.ToString() ?? string.Empty;
                    var tick = open.LastIndexOf('`');

                    text.Append(tick > 0 ? open.Substring(0, tick) : open);
                    text.Append('<');
                    for (var i = 0; i < this.GenericArguments.Count; i++)
                    {
                        if (i > 0)
                            text.Append(", ");

                        this.GenericArguments[i].Append(text);
                    }

                    text.Append('>');
                    break;
                }

                case CorElementType.VAR:
                    text.Append('!').Append(this.GenericParameterIndex);
                    break;

                case CorElementType.MVAR:
                    text.Append("!!").Append(this.GenericParameterIndex);
                    break;

                case CorElementType.FNPTR:
                    text.Append("delegate*<");
                    if (this.FunctionPointer != null)
                    {
                        foreach (var parameter in this.FunctionPointer.Parameters)
                            text.Append(parameter.Type).Append(", ");

                        text.Append(this.FunctionPointer.ReturnType);
                    }

                    text.Append('>');
                    break;

                case CorElementType.INTERNAL:
                    text.Append("<internal type handle>");
                    break;

                default:
                    text.Append(this.ElementType.ToString());
                    break;
            }

            this.AppendModifiers(text);
        }

        private void AppendModifiers(StringBuilder text)
        {
            foreach (var modifier in this.CustomModifiers)
                text.Append(modifier.IsRequired ? " modreq(" : " modopt(").Append(modifier).Append(')');
        }
    }

    /// <summary>A <c>modreq</c> or <c>modopt</c> attached to a type in a signature.</summary>
    public sealed class ClrCustomModifier
    {
        internal ClrCustomModifier(bool isRequired, MetadataTable table, uint rowId, string name)
        {
            this.IsRequired = isRequired;
            this.TypeTable = table;
            this.TypeRowId = rowId;
            this.TypeName = name;
        }

        /// <summary>True for <c>modreq</c>, which a consumer may not ignore.</summary>
        public bool IsRequired { get; }

        public MetadataTable TypeTable { get; }

        public uint TypeRowId { get; }

        public string TypeName { get; }

        public override string ToString() => this.TypeName ?? $"{this.TypeTable}({this.TypeRowId})";
    }
}