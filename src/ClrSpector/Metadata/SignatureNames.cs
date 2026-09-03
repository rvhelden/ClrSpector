using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace ClrSpector
{
    /// <summary>
    /// Turns a metadata signature blob into a readable type name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A TypeSpec - which is what a constructed generic like <c>IComparable&lt;Order&gt;</c> is
    /// recorded as - has no name of its own. Its row holds an encoded signature, and the only way
    /// to a name is to decode it. <see cref="SignatureDecoder{TType,TGenericContext}"/> does the
    /// walking; this supplies the naming at each step.
    /// </para>
    /// <para>
    /// Names are for reading, not for round-tripping: this produces
    /// <c>System.IComparable&lt;AbiProbe.Order&gt;</c> rather than the mangled
    /// <c>IComparable`1[[...]]</c> form, because the point is a legible listing.
    /// </para>
    /// </remarks>
    internal sealed class SignatureNames : ISignatureTypeProvider<string, object>
    {
        private readonly ClrModuleMetadata metadata;

        public SignatureNames(ClrModuleMetadata metadata)
        {
            this.metadata = metadata;
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            switch (typeCode)
            {
                case PrimitiveTypeCode.Boolean: return "bool";
                case PrimitiveTypeCode.Byte: return "byte";
                case PrimitiveTypeCode.Char: return "char";
                case PrimitiveTypeCode.Double: return "double";
                case PrimitiveTypeCode.Int16: return "short";
                case PrimitiveTypeCode.Int32: return "int";
                case PrimitiveTypeCode.Int64: return "long";
                case PrimitiveTypeCode.IntPtr: return "nint";
                case PrimitiveTypeCode.Object: return "object";
                case PrimitiveTypeCode.SByte: return "sbyte";
                case PrimitiveTypeCode.Single: return "float";
                case PrimitiveTypeCode.String: return "string";
                case PrimitiveTypeCode.TypedReference: return "typedref";
                case PrimitiveTypeCode.UInt16: return "ushort";
                case PrimitiveTypeCode.UInt32: return "uint";
                case PrimitiveTypeCode.UInt64: return "ulong";
                case PrimitiveTypeCode.UIntPtr: return "nuint";
                case PrimitiveTypeCode.Void: return "void";
                default: return typeCode.ToString();
            }
        }

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            return this.metadata.FullTypeName((uint)MetadataTokens.GetToken(handle));
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            return this.metadata.TokenName(MetadataTokens.GetToken(handle));
        }

        public string GetTypeFromSpecification(
            MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        }

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape)
        {
            return elementType + "[" + new string(',', shape.Rank - 1) + "]";
        }

        public string GetByReferenceType(string elementType) => "ref " + elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPinnedType(string elementType) => "pinned " + elementType;

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            // The definition's name still carries its arity suffix, which the argument list makes
            // redundant - `List`1<int>` reads worse than `List<int>`.
            var tick = genericType.LastIndexOf('`');
            var name = tick < 0 ? genericType : genericType.Substring(0, tick);

            return $"{name}<{string.Join(", ", typeArguments)}>";
        }

        public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;

        public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        {
            return unmodifiedType;
        }

        public string GetFunctionPointerType(MethodSignature<string> signature)
        {
            return $"delegate*<{string.Join(", ", signature.ParameterTypes)}, {signature.ReturnType}>";
        }
    }
}
