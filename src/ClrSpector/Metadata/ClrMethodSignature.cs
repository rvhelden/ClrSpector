using System;
using System.Collections.Generic;
using System.Text;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// One parameter of a method, as its signature and the Param table describe it together.
    /// </summary>
    /// <remarks>
    /// The two halves come from different places. The signature blob carries the type and nothing
    /// else - it has no names, and no notion of <c>out</c>. The name and the direction live in the
    /// Param table, which is why a parameter needs both to be described: a signature alone renders
    /// <c>out double</c> as <c>ref double</c>, correctly but uselessly.
    /// </remarks>
    public sealed class ClrMethodParameter
    {
        /// <summary>Param.Flags bit 0x0001 - the parameter is <c>in</c>.</summary>
        private const ushort InFlag = 0x0001;

        /// <summary>Param.Flags bit 0x0002 - the parameter is <c>out</c>.</summary>
        private const ushort OutFlag = 0x0002;

        /// <summary>Param.Flags bit 0x0010 - the parameter is optional.</summary>
        private const ushort OptionalFlag = 0x0010;

        internal ClrMethodParameter(int index, ClrSignatureType type)
        {
            this.Index = index;
            this.Type = type;
        }

        /// <summary>The parameter's position, counting from zero and ignoring <c>this</c>.</summary>
        public int Index { get; }

        /// <summary>The parameter's type.</summary>
        public ClrSignatureType Type { get; }

        /// <summary>The declared name, or null when the module records none.</summary>
        public string Name { get; internal set; }

        /// <summary>The Param table's flags, or zero when there is no Param row.</summary>
        public ushort Flags { get; internal set; }

        public bool IsIn => (this.Flags & InFlag) != 0;

        /// <summary>True for an <c>out</c> parameter, which the signature records only as a byref.</summary>
        public bool IsOut => (this.Flags & OutFlag) != 0;

        public bool IsOptional => (this.Flags & OptionalFlag) != 0;

        /// <summary>
        /// True when this parameter is past the vararg sentinel, so it is one of the extra
        /// arguments a call site supplies rather than part of the declared list.
        /// </summary>
        public bool IsVarArgExtra { get; internal set; }

        public override string ToString()
        {
            var direction = this.IsOut ? "out " : this.IsIn && this.Type.IsByRef ? "in " : string.Empty;

            // A byref already renders as "ref", which out and in replace rather than decorate.
            var type = direction.Length > 0 && this.Type.IsByRef
                ? this.Type.Element?.ToString() ?? this.Type.ToString()
                : this.Type.ToString();

            return $"{direction}{type} {this.Name ?? "arg" + this.Index}".TrimEnd();
        }
    }

    /// <summary>
    /// A method's return type and parameters, decoded from its signature blob.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A MethodDesc stores no signature, only a token - just as it stores no name. The signature
    /// is a MethodDefSig blob in the declaring module's metadata, and this decodes it in place,
    /// with no <c>System.Reflection</c> and no <c>System.Reflection.Metadata</c> involved: nothing
    /// has to be loaded, no <c>Type</c> has to exist, and a constructed generic that
    /// <c>Module.ResolveMethod</c> would refuse is decoded like anything else.
    /// </para>
    /// <para>
    /// What this cannot do is substitute a generic instantiation. A MethodDef signature is the
    /// open definition, so <c>T</c> stays <c>!!0</c>; the actual arguments live on the MethodDesc
    /// (an InstantiatedMethodDesc's PerInstInfo), not in metadata.
    /// </para>
    /// </remarks>
    public sealed unsafe class ClrMethodSignature
    {
        /// <summary>The low nibble of the header byte selects the calling convention.</summary>
        private const byte CallingConventionMask = 0x0F;

        private const byte VarArgConvention = 0x05;

        /// <summary>Header bit 0x10 - a generic parameter count follows the header.</summary>
        private const byte GenericFlag = 0x10;

        /// <summary>Header bit 0x20 - the method takes a <c>this</c> the signature omits.</summary>
        private const byte HasThisFlag = 0x20;

        private const byte ExplicitThisFlag = 0x40;

        /// <summary>A guard on a count read out of a blob, so a bad read cannot allocate wildly.</summary>
        private const uint MaximumParameters = 0xFFFF;

        private ClrMethodSignature()
        {
        }

        /// <summary>The raw header byte, for callers that want the convention verbatim.</summary>
        public byte Header { get; private set; }

        /// <summary>True when the method has an implicit <c>this</c>, so it is an instance method.</summary>
        public bool HasThis => (this.Header & HasThisFlag) != 0;

        public bool HasExplicitThis => (this.Header & ExplicitThisFlag) != 0;

        /// <summary>True when the method is generic; see <see cref="GenericParameterCount"/>.</summary>
        public bool IsGeneric => (this.Header & GenericFlag) != 0;

        public bool IsVarArg => (this.Header & CallingConventionMask) == VarArgConvention;

        /// <summary>How many type parameters the method declares.</summary>
        public int GenericParameterCount { get; private set; }

        public ClrSignatureType ReturnType { get; private set; }

        /// <summary>The parameters, in order, not counting <c>this</c>.</summary>
        public IReadOnlyList<ClrMethodParameter> Parameters { get; private set; }

        /// <summary>
        /// How many parameters precede the vararg sentinel. Equal to the parameter count unless
        /// the method is vararg.
        /// </summary>
        public int RequiredParameterCount { get; private set; }

        /// <summary>
        /// True when this came off the MethodDesc rather than out of metadata, which is why its
        /// parameters have no names and its named types no names.
        /// </summary>
        public bool IsStored { get; private set; }

        /// <summary>
        /// True when this signature has had an instantiation substituted into it, so its type
        /// variables have been replaced by real types.
        /// </summary>
        public bool IsClosed { get; private set; }

        /// <summary>
        /// A copy of this signature with its type variables replaced by an instantiation.
        /// </summary>
        /// <remarks>
        /// Returns this same instance when nothing was substituted, so an already-closed
        /// signature - or an open definition with no instantiation to close it with - costs
        /// nothing and reads the same.
        /// </remarks>
        public ClrMethodSignature WithArguments(
            IReadOnlyList<ClrSignatureType> typeArguments,
            IReadOnlyList<ClrSignatureType> methodArguments)
        {
            var returned = this.ReturnType?.WithArguments(typeArguments, methodArguments);
            var changed = !ReferenceEquals(returned, this.ReturnType);

            var parameters = new ClrMethodParameter[this.Parameters.Count];
            for (var i = 0; i < parameters.Length; i++)
            {
                var original = this.Parameters[i];
                var type = original.Type.WithArguments(typeArguments, methodArguments);

                changed |= !ReferenceEquals(type, original.Type);

                parameters[i] = new ClrMethodParameter(original.Index, type)
                {
                    Name = original.Name,
                    Flags = original.Flags,
                    IsVarArgExtra = original.IsVarArgExtra
                };
            }

            if (!changed)
                return this;

            return new ClrMethodSignature
            {
                Header = this.Header,
                GenericParameterCount = this.GenericParameterCount,
                ReturnType = returned,
                Parameters = parameters,
                RequiredParameterCount = this.RequiredParameterCount,
                IsStored = this.IsStored,
                IsClosed = true
            };
        }

        /// <summary>
        /// Decodes the signature of <paramref name="method"/>, or null when its module has no
        /// mapped metadata to read it from.
        /// </summary>
        public static ClrMethodSignature Of(ClrMethodDescription method)
        {
            if (method == null)
                throw new System.ArgumentNullException(nameof(method));

            var metadata = method.Metadata;
            var image = metadata?.Image;
            var rowId = method.MetadataToken & 0x00FFFFFF;

            var hasRow = image != null
                         && rowId != 0
                         && rowId <= (uint)image.RowCount(MetadataTable.MethodDef);

            if (!hasRow)
                return FromStoredSignature(method, image);

            // MethodDef column 4 is the signature blob, column 5 the first of its Param rows.
            var blob = image.Blob(image.ReadColumn(MetadataTable.MethodDef, rowId, 4));
            var signature = Decode(ref blob, image);

            signature.ReadParameterNames(image, rowId);
            signature.IsStored = false;

            return signature;
        }

        /// <summary>
        /// Decodes the signature a MethodDesc carries itself, for the methods that have no
        /// metadata row to read one from.
        /// </summary>
        /// <remarks>
        /// A dynamic method was never in any module's tables, and an array's Get, Set and Address
        /// are synthesised per array type rather than declared - so for those the runtime keeps
        /// the blob on the MethodDesc. It is the same MethodDefSig encoding, which is why the same
        /// decoder reads it; what it lacks is a module to resolve its tokens against, so type
        /// names come back null while the shape of every type is still recovered in full.
        /// </remarks>
        private static ClrMethodSignature FromStoredSignature(
            ClrMethodDescription method, MetadataImage image)
        {
            var address = method.StoredSignatureAddress;
            var length = method.StoredSignatureLength;

            if (address == IntPtr.Zero || length == 0)
                return null;

            var blob = new SignatureBlob((byte*)address, (int)length);
            var signature = Decode(ref blob, image);

            signature.IsStored = true;

            return signature;
        }

        /// <summary>Decodes a MethodDefSig or MethodRefSig from <paramref name="blob"/>.</summary>
        internal static ClrMethodSignature Decode(ref SignatureBlob blob, MetadataImage image)
        {
            var signature = new ClrMethodSignature();
            signature.Header = blob.ReadByte();

            if (signature.IsGeneric)
                signature.GenericParameterCount = (int)blob.ReadCompressedUInt();

            var parameterCount = blob.ReadCompressedUInt();

            if (parameterCount > MaximumParameters)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"A method signature declares {parameterCount} parameters, which is beyond " +
                    $"anything ECMA-335 allows, so the blob is not being read correctly.");

            signature.ReturnType = SignatureTypeReader.ReadType(ref blob, image);

            var parameters = new List<ClrMethodParameter>((int)parameterCount);
            var required = (int)parameterCount;
            var pastSentinel = false;

            for (var i = 0; i < (int)parameterCount; i++)
            {
                // The sentinel sits between the declared parameters and a vararg call's extras.
                if (blob.PeekByte() == (byte)CorElementType.SENTINEL)
                {
                    blob.ReadByte();
                    required = i;
                    pastSentinel = true;
                }

                var type = SignatureTypeReader.ReadType(ref blob, image);

                parameters.Add(new ClrMethodParameter(i, type) { IsVarArgExtra = pastSentinel });
            }

            signature.Parameters = parameters;
            signature.RequiredParameterCount = required;

            return signature;
        }

        /// <summary>
        /// Fills in names and directions from the Param table.
        /// </summary>
        /// <remarks>
        /// A MethodDef's ParamList is the first of a run that ends where the next method's begins,
        /// which is how ECMA-335 stores every one-to-many relationship in the tables - so the run
        /// length comes from the following row, or from the table's end for the last method.
        ///
        /// Sequence 0 is the return value's own row, which carries its attributes rather than a
        /// parameter's, so it is skipped here.
        /// </remarks>
        private void ReadParameterNames(MetadataImage image, uint methodRowId)
        {
            var paramCount = image.RowCount(MetadataTable.Param);
            if (paramCount == 0)
                return;

            var first = image.ReadColumn(MetadataTable.MethodDef, methodRowId, 5);
            var methodCount = (uint)image.RowCount(MetadataTable.MethodDef);

            var last = methodRowId < methodCount
                ? image.ReadColumn(MetadataTable.MethodDef, methodRowId + 1, 5)
                : (uint)paramCount + 1;

            for (var rowId = first; rowId < last && rowId <= (uint)paramCount; rowId++)
            {
                var flags = (ushort)image.ReadColumn(MetadataTable.Param, rowId, 0);
                var sequence = (int)image.ReadColumn(MetadataTable.Param, rowId, 1);

                if (sequence <= 0 || sequence > this.Parameters.Count)
                    continue;

                var parameter = this.Parameters[sequence - 1];
                parameter.Flags = flags;
                parameter.Name = image.String(image.ReadColumn(MetadataTable.Param, rowId, 2));
            }
        }

        public override string ToString()
        {
            var text = new StringBuilder();

            text.Append(this.ReturnType).Append(' ');

            if (this.IsGeneric)
                text.Append('<').Append(this.GenericParameterCount).Append(" generic>");

            text.Append('(');
            for (var i = 0; i < this.Parameters.Count; i++)
            {
                if (i > 0)
                    text.Append(", ");

                if (i == this.RequiredParameterCount)
                    text.Append("__arglist, ");

                text.Append(this.Parameters[i]);
            }

            text.Append(')');

            return text.ToString();
        }
    }
}
