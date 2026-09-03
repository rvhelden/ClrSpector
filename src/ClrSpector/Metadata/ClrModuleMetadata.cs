using System;
using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// A module's ECMA-335 metadata, read out of the image the loader mapped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what lets a name be recovered without going through <see cref="Type"/> or
    /// <see cref="System.Reflection.MethodBase"/>. A MethodTable knows its TypeDef token and a
    /// MethodDesc knows its MethodDef token, but neither stores a name - the names live in the
    /// module's string heap, and until now the only way to reach them was to hand the token back
    /// to reflection.
    /// </para>
    /// <para>
    /// The metadata is found by walking the mapped image's own headers: the PE header's CLI data
    /// directory gives the COR20 header, which gives the metadata directory. Because the image is
    /// mapped rather than read from disk, a relative virtual address is simply an offset from the
    /// module base, so no section translation is needed.
    /// </para>
    /// <para>
    /// Nothing is copied. <see cref="MetadataReader"/> is pointed straight at the mapped bytes,
    /// which are read-only for the life of the module.
    /// </para>
    /// </remarks>
    public sealed unsafe class ClrModuleMetadata
    {
        /// <summary>Index of the CLI header in the PE data directories.</summary>
        private const int CliDirectoryIndex = 14;

        /// <summary>Where the data directories begin within the optional header.</summary>
        private const int DataDirectoriesOffsetPe32Plus = 112;

        private const int DataDirectoriesOffsetPe32 = 96;

        private const ushort Pe32PlusMagic = 0x20B;

        /// <summary>Offset of the metadata directory within the COR20 header.</summary>
        private const int Cor20MetadataDirectoryOffset = 8;

        private static readonly ConcurrentDictionary<IntPtr, ClrModuleMetadata> cache =
            new ConcurrentDictionary<IntPtr, ClrModuleMetadata>();

        private ClrModuleMetadata(IntPtr imageBase, IntPtr metadata, int size)
        {
            this.ImageBase = imageBase;
            this.MetadataAddress = metadata;
            this.MetadataSize = size;
            this.Reader = new MetadataReader((byte*)metadata, size);
        }

        /// <summary>The base address of the mapped image.</summary>
        public IntPtr ImageBase { get; }

        public IntPtr MetadataAddress { get; }

        public int MetadataSize { get; }

        /// <summary>The metadata itself, read in place.</summary>
        public MetadataReader Reader { get; }

        /// <summary>
        /// The metadata of <paramref name="module"/>, or null when it has no mapped image - a
        /// module built at runtime keeps its metadata elsewhere.
        /// </summary>
        public static ClrModuleMetadata Of(ClrModule module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));

            return AtImageBase(module.Base);
        }

        /// <summary>The metadata of the image mapped at <paramref name="imageBase"/>.</summary>
        public static ClrModuleMetadata AtImageBase(IntPtr imageBase)
        {
            if (imageBase == IntPtr.Zero)
                return null;

            return cache.GetOrAdd(imageBase, Read);
        }

        /// <summary>
        /// The namespace and name of a type, from its TypeDef token.
        /// </summary>
        public (string Namespace, string Name) TypeName(uint typeDefToken)
        {
            var handle = MetadataTokens.EntityHandle((int)typeDefToken);

            if (handle.Kind != HandleKind.TypeDefinition)
                return (null, null);

            var definition = this.Reader.GetTypeDefinition((TypeDefinitionHandle)handle);

            return (this.Reader.GetString(definition.Namespace), this.Reader.GetString(definition.Name));
        }

        /// <summary>
        /// A type's full name as metadata spells it - <c>Namespace.Name</c>, with a nested type
        /// joined to its declaring type by <c>+</c>.
        /// </summary>
        public string FullTypeName(uint typeDefToken)
        {
            var handle = MetadataTokens.EntityHandle((int)typeDefToken);

            if (handle.Kind != HandleKind.TypeDefinition)
                return null;

            var definition = this.Reader.GetTypeDefinition((TypeDefinitionHandle)handle);
            var name = this.Reader.GetString(definition.Name);

            // A nested type's own row carries only the short name; the enclosing type is a
            // separate row that has to be walked to.
            var declaring = definition.GetDeclaringType();
            if (!declaring.IsNil)
                return $"{this.FullTypeName((uint)MetadataTokens.GetToken(declaring))}+{name}";

            var space = this.Reader.GetString(definition.Namespace);

            return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
        }

        /// <summary>The name of a method, from its MethodDef token.</summary>
        public string MethodName(uint methodDefToken)
        {
            var handle = MetadataTokens.EntityHandle((int)methodDefToken);

            if (handle.Kind != HandleKind.MethodDefinition)
                return null;

            return this.Reader.GetString(this.Reader.GetMethodDefinition((MethodDefinitionHandle)handle).Name);
        }

        /// <summary>The name of a field, from its FieldDef token.</summary>
        public string FieldName(uint fieldDefToken)
        {
            var handle = MetadataTokens.EntityHandle((int)fieldDefToken);

            if (handle.Kind != HandleKind.FieldDefinition)
                return null;

            return this.Reader.GetString(this.Reader.GetFieldDefinition((FieldDefinitionHandle)handle).Name);
        }

        /// <summary>
        /// A readable name for any token an IL operand can carry, so a listing can be produced
        /// without reflection resolving anything.
        /// </summary>
        /// <remarks>
        /// A token that does not resolve comes back as its own hexadecimal value rather than
        /// throwing - a listing of a partly unreadable method is worth more than no listing.
        /// </remarks>
        public string TokenName(int token)
        {
            try
            {
                var handle = MetadataTokens.EntityHandle(token);

                switch (handle.Kind)
                {
                    case HandleKind.TypeDefinition:
                        return this.FullTypeName((uint)token);

                    case HandleKind.TypeReference:
                    {
                        var reference = this.Reader.GetTypeReference((TypeReferenceHandle)handle);
                        var space = this.Reader.GetString(reference.Namespace);
                        var name = this.Reader.GetString(reference.Name);

                        return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
                    }

                    case HandleKind.MethodDefinition:
                    {
                        var definition = this.Reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                        var owner = (uint)MetadataTokens.GetToken(definition.GetDeclaringType());

                        return $"{this.FullTypeName(owner)}::{this.Reader.GetString(definition.Name)}";
                    }

                    case HandleKind.FieldDefinition:
                    {
                        var definition = this.Reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                        var owner = (uint)MetadataTokens.GetToken(definition.GetDeclaringType());

                        return $"{this.FullTypeName(owner)}::{this.Reader.GetString(definition.Name)}";
                    }

                    case HandleKind.MemberReference:
                    {
                        var reference = this.Reader.GetMemberReference((MemberReferenceHandle)handle);
                        var parent = reference.Parent;
                        var owner = parent.Kind == HandleKind.TypeReference
                            ? this.TokenName(MetadataTokens.GetToken(parent))
                            : parent.Kind.ToString();

                        return $"{owner}::{this.Reader.GetString(reference.Name)}";
                    }

                    case HandleKind.TypeSpecification:
                    {
                        // A constructed generic has no name of its own - only an encoded
                        // signature, which has to be decoded to say anything useful about it.
                        var specification = this.Reader.GetTypeSpecification((TypeSpecificationHandle)handle);

                        return specification.DecodeSignature(new SignatureNames(this), null);
                    }

                    case HandleKind.MethodSpecification:
                        return $"{handle.Kind} 0x{token:x8}";

                    default:
                        return $"0x{token:x8}";
                }
            }
            catch (Exception)
            {
                return $"0x{token:x8}";
            }
        }

        /// <summary>The user string a <c>ldstr</c> token names.</summary>
        public string UserString(int token)
        {
            try
            {
                return this.Reader.GetUserString(MetadataTokens.UserStringHandle(token));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The relative virtual address of a method's body, or zero when it has none - an
        /// abstract method, or one the runtime implements itself.
        /// </summary>
        public int MethodBodyRva(uint methodDefToken)
        {
            var handle = MetadataTokens.EntityHandle((int)methodDefToken);

            if (handle.Kind != HandleKind.MethodDefinition)
                return 0;

            return this.Reader.GetMethodDefinition((MethodDefinitionHandle)handle).RelativeVirtualAddress;
        }

        private static ClrModuleMetadata Read(IntPtr imageBase)
        {
            var image = (byte*)imageBase;

            // PE headers: the DOS stub points at the PE signature, which is followed by the COFF
            // header and then the optional header holding the data directories.
            var peOffset = *(int*)(image + 0x3C);
            var optionalHeader = image + peOffset + 4 + 20;

            var magic = *(ushort*)optionalHeader;
            var directories = optionalHeader
                              + (magic == Pe32PlusMagic ? DataDirectoriesOffsetPe32Plus : DataDirectoriesOffsetPe32);

            var cliRva = *(uint*)(directories + CliDirectoryIndex * 8);
            if (cliRva == 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The image at 0x{imageBase.ToInt64():x} has no CLI header, so it carries no " +
                    "managed metadata.");

            // A mapped image places every section at its virtual address, so an RVA is just an
            // offset from the base - no section table walk needed.
            var cliHeader = image + cliRva;
            var metadataRva = *(uint*)(cliHeader + Cor20MetadataDirectoryOffset);
            var metadataSize = *(uint*)(cliHeader + Cor20MetadataDirectoryOffset + 4);

            if (metadataRva == 0 || metadataSize == 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The CLI header of the image at 0x{imageBase.ToInt64():x} names no metadata.");

            return new ClrModuleMetadata(imageBase, (IntPtr)(image + metadataRva), (int)metadataSize);
        }

        public override string ToString()
        {
            return $"metadata @0x{this.MetadataAddress.ToInt64():x} size={this.MetadataSize} " +
                   $"version={this.Reader.MetadataVersion}";
        }
    }
}
