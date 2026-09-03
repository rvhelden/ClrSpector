using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// Decodes one type out of a signature blob, per ECMA-335 II.23.2.12.
    /// </summary>
    /// <remarks>
    /// Signature types nest, so this recurses: the element types that take an operand read it by
    /// calling back in. Depth is capped, because the depth here is driven entirely by bytes read
    /// out of memory - a blob being misread would otherwise recurse until the stack ran out,
    /// which is not a failure a caller can catch.
    /// </remarks>
    internal static class SignatureTypeReader
    {
        /// <summary>Deeper than any real signature; a blob that needs more is being misread.</summary>
        private const int MaximumDepth = 64;

        /// <summary>A guard on counts read from the blob, so a bad read cannot allocate wildly.</summary>
        private const uint MaximumCount = 0xFFFF;

        public static ClrSignatureType ReadType(ref SignatureBlob blob, MetadataImage image)
        {
            return ReadType(ref blob, image, 0);
        }

        private static ClrSignatureType ReadType(ref SignatureBlob blob, MetadataImage image, int depth)
        {
            if (depth > MaximumDepth)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"A signature nested more than {MaximumDepth} types deep, so the blob is not " +
                    $"being read correctly.");

            // Modifiers and pinning precede the type they apply to, and there can be a run of
            // them, so they are collected first and attached to whatever turns up.
            List<ClrCustomModifier> modifiers = null;

            while (true)
            {
                var peek = (CorElementType)blob.PeekByte();

                if (peek != CorElementType.CMOD_REQD && peek != CorElementType.CMOD_OPT)
                    break;

                blob.ReadByte();

                var token = blob.ReadTypeDefOrRefOrSpec();
                modifiers ??= new List<ClrCustomModifier>(1);
                modifiers.Add(new ClrCustomModifier(
                    peek == CorElementType.CMOD_REQD,
                    token.Table,
                    token.RowId,
                    NameOf(image, token.Table, token.RowId)));
            }

            var elementType = (CorElementType)blob.ReadByte();
            var type = new ClrSignatureType(elementType);

            if (modifiers != null)
                type.CustomModifiers = modifiers;

            switch (elementType)
            {
                case CorElementType.CLASS:
                case CorElementType.VALUETYPE:
                {
                    var token = blob.ReadTypeDefOrRefOrSpec();
                    type.TypeTable = token.Table;
                    type.TypeRowId = token.RowId;
                    type.TypeName = NameOf(image, token.Table, token.RowId);
                    break;
                }

                case CorElementType.BYREF:
                case CorElementType.PTR:
                case CorElementType.SZARRAY:
                case CorElementType.PINNED:
                    type.Element = ReadType(ref blob, image, depth + 1);
                    break;

                case CorElementType.ARRAY:
                {
                    type.Element = ReadType(ref blob, image, depth + 1);
                    type.Rank = (int)Bounded(blob.ReadCompressedUInt(), "an array rank");

                    var sizeCount = Bounded(blob.ReadCompressedUInt(), "an array size count");
                    var sizes = new int[sizeCount];
                    for (var i = 0; i < sizeCount; i++)
                        sizes[i] = (int)blob.ReadCompressedUInt();

                    var boundCount = Bounded(blob.ReadCompressedUInt(), "an array bound count");
                    var bounds = new int[boundCount];
                    for (var i = 0; i < boundCount; i++)
                        bounds[i] = blob.ReadCompressedInt();

                    type.ArraySizes = sizes;
                    type.ArrayLowerBounds = bounds;
                    break;
                }

                case CorElementType.GENERICINST:
                {
                    // The instantiated type is itself a CLASS or VALUETYPE with its own token.
                    type.GenericType = ReadType(ref blob, image, depth + 1);

                    var argumentCount = Bounded(blob.ReadCompressedUInt(), "a generic argument count");
                    var arguments = new ClrSignatureType[argumentCount];
                    for (var i = 0; i < argumentCount; i++)
                        arguments[i] = ReadType(ref blob, image, depth + 1);

                    type.GenericArguments = arguments;
                    break;
                }

                case CorElementType.VAR:
                case CorElementType.MVAR:
                    type.GenericParameterIndex = (int)blob.ReadCompressedUInt();
                    break;

                case CorElementType.FNPTR:
                    type.FunctionPointer = ClrMethodSignature.Decode(ref blob, image);
                    break;

                case CorElementType.INTERNAL:
                    // A runtime-generated signature can embed a TypeHandle directly. It is never
                    // persisted, and there is no token to resolve, so the pointer is stepped over.
                    for (var i = 0; i < System.IntPtr.Size; i++)
                        blob.ReadByte();

                    break;
            }

            return type;
        }

        private static uint Bounded(uint value, string what)
        {
            if (value > MaximumCount)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"A signature gives {value} as {what}, which is implausible, so the blob is " +
                    $"not being read correctly.");

            return value;
        }

        /// <summary>
        /// The name a TypeDef or TypeRef token carries. A TypeSpec has none - its row is another
        /// signature - so it is decoded rather than named.
        /// </summary>
        private static string NameOf(MetadataImage image, MetadataTable table, uint rowId)
        {
            if (rowId == 0 || rowId > (uint)image.RowCount(table))
                return null;

            switch (table)
            {
                case MetadataTable.TypeDef:
                {
                    // TypeDef: Flags, Name, Namespace, ...
                    var name = image.String(image.ReadColumn(table, rowId, 1));
                    var space = image.String(image.ReadColumn(table, rowId, 2));

                    return Join(space, name);
                }

                case MetadataTable.TypeRef:
                {
                    // TypeRef: ResolutionScope, Name, Namespace
                    var name = image.String(image.ReadColumn(table, rowId, 1));
                    var space = image.String(image.ReadColumn(table, rowId, 2));

                    return Join(space, name);
                }

                case MetadataTable.TypeSpec:
                {
                    var blob = image.Blob(image.ReadColumn(table, rowId, 0));

                    return ReadType(ref blob, image, 0).ToString();
                }

                default:
                    return null;
            }
        }

        private static string Join(string space, string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            return string.IsNullOrEmpty(space) ? name : space + "." + name;
        }
    }
}
