namespace ClrSpector
{
    public enum CorElementType : byte
    {
        END = 0x00,
        VOID = 0x01,
        BOOLEAN = 0x02,
        CHAR = 0x03,
        I1 = 0x04,
        U1 = 0x05,
        I2 = 0x06,
        U2 = 0x07,
        I4 = 0x08,
        U4 = 0x09,
        I8 = 0x0a,
        U8 = 0x0b,
        R4 = 0x0c,
        R8 = 0x0d,
        STRING = 0x0e,

        // every type above PTR will be simple type
        PTR = 0x0f,     // PTR <type>
        BYREF = 0x10,     // BYREF <type>

        // Please use VALUETYPE. VALUECLASS is deprecated.
        VALUETYPE = 0x11,     // VALUETYPE <class Token>
        CLASS = 0x12,     // CLASS <class Token>
        VAR = 0x13,     // a class type variable VAR <number>
        ARRAY = 0x14,     // MDARRAY <type> <rank> <bcount> <bound1> ... <lbcount> <lb1> ...
        GENERICINST = 0x15,     // GENERICINST <generic type> <argCnt> <arg1> ... <argn>
        TYPEDBYREF = 0x16,     // TYPEDREF  (it takes no args) a typed referece to some other type

        I = 0x18,     // native integer size
        U = 0x19,     // native unsigned integer size
        FNPTR = 0x1b,     // FNPTR <complete sig for the function including calling convention>
        OBJECT = 0x1c,     // Shortcut for System.Object
        SZARRAY = 0x1d,     // Shortcut for single dimension zero lower bound array
                                         // SZARRAY <type>
        MVAR = 0x1e,     // a method type variable MVAR <number>

        // This is only for binding
        CMOD_REQD = 0x1f,     // required C modifier : E_T_CMOD_REQD <mdTypeRef/mdTypeDef>
        CMOD_OPT = 0x20,     // optional C modifier : E_T_CMOD_OPT <mdTypeRef/mdTypeDef>

        // This is for signatures generated internally (which will not be persisted in any way).
        INTERNAL = 0x21,     // INTERNAL <typehandle>

        // Note that this is the max of base type excluding modifiers
        MAX = 0x22,     // first invalid element type


        MODIFIER = 0x40,
        SENTINEL = 0x01 | MODIFIER, // sentinel for varargs
        PINNED = 0x05 | MODIFIER,
    }
}
