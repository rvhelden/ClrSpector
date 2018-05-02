namespace ClrSpector.Methods
{
    public enum PrecodeType : byte
    {
        Invalid = 0xCC,
        StubX86 = 0xED,
        StubX64 = 0x40,
        NDirectImportX86 = 0xC0,
        NDirectImportX64 = 0x48,
        RemotingX86 = 0x90,
        Fixup = 0x5F,
        ThisPtrRetBufPrecodeX86 = 0xC2,
        ThisPtrRetBufPrecodeX64 = 0x90,
    }
}
