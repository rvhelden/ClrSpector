namespace ClrSpector
{
    /// <summary>
    /// The ECMA-335 metadata tables, by their table number.
    /// </summary>
    /// <remarks>
    /// The numbers are the ones the <c>#~</c> stream's Valid bitmask uses, and the high byte of a
    /// metadata token is the same value - so a MethodDef token 0x06000001 is row 1 of
    /// <see cref="MethodDef"/>. See ECMA-335 II.22.
    /// </remarks>
    public enum MetadataTable : byte
    {
        Module = 0x00,
        TypeRef = 0x01,
        TypeDef = 0x02,
        FieldPtr = 0x03,
        Field = 0x04,
        MethodPtr = 0x05,
        MethodDef = 0x06,
        ParamPtr = 0x07,
        Param = 0x08,
        InterfaceImpl = 0x09,
        MemberRef = 0x0A,
        Constant = 0x0B,
        CustomAttribute = 0x0C,
        FieldMarshal = 0x0D,
        DeclSecurity = 0x0E,
        ClassLayout = 0x0F,
        FieldLayout = 0x10,
        StandAloneSig = 0x11,
        EventMap = 0x12,
        EventPtr = 0x13,
        Event = 0x14,
        PropertyMap = 0x15,
        PropertyPtr = 0x16,
        Property = 0x17,
        MethodSemantics = 0x18,
        MethodImpl = 0x19,
        ModuleRef = 0x1A,
        TypeSpec = 0x1B,
        ImplMap = 0x1C,
        FieldRva = 0x1D,
        EncLog = 0x1E,
        EncMap = 0x1F,
        Assembly = 0x20,
        AssemblyProcessor = 0x21,
        AssemblyOS = 0x22,
        AssemblyRef = 0x23,
        AssemblyRefProcessor = 0x24,
        AssemblyRefOS = 0x25,
        File = 0x26,
        ExportedType = 0x27,
        ManifestResource = 0x28,
        NestedClass = 0x29,
        GenericParam = 0x2A,
        MethodSpec = 0x2B,
        GenericParamConstraint = 0x2C,

        // ---------- the tables only a portable PDB has ----------

        /// <summary>A source file a method's sequence points refer to.</summary>
        Document = 0x30,

        /// <summary>A method's sequence points, and the document they are in.</summary>
        MethodDebugInformation = 0x31,

        /// <summary>
        /// A lexical scope over a run of IL, holding the locals declared in it - which is where
        /// a local's name is, since the method body itself records only its type.
        /// </summary>
        LocalScope = 0x32,

        /// <summary>One named local variable slot.</summary>
        LocalVariable = 0x33,

        LocalConstant = 0x34,

        ImportScope = 0x35,

        StateMachineMethod = 0x36,

        CustomDebugInformation = 0x37
    }
}
