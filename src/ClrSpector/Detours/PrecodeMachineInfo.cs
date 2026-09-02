using System;
using ClrSpector.Cdac;

namespace ClrSpector.Detours
{
    /// <summary>
    /// The runtime's own description of how precodes are built on this machine, read from the
    /// contract descriptor's <c>PrecodeMachineDescriptor</c>.
    /// </summary>
    /// <remarks>
    /// These are the runtime's constants, not this library's assumptions. They are exposed mainly
    /// so a precode can be identified and reported rather than guessed at.
    /// </remarks>
    public sealed unsafe class PrecodeMachineInfo
    {
        private static readonly Lazy<PrecodeMachineInfo> current =
            new Lazy<PrecodeMachineInfo>(Load, isThreadSafe: true);

        public static PrecodeMachineInfo Current => current.Value;

        private PrecodeMachineInfo(IntPtr address)
        {
            this.Address = address;
        }

        /// <summary>Where the descriptor lives in the runtime.</summary>
        public IntPtr Address { get; }

        public uint StubCodePageSize { get; private set; }

        public byte InvalidPrecodeType { get; private set; }

        public byte StubPrecodeType { get; private set; }

        public byte FixupPrecodeType { get; private set; }

        public byte PInvokeImportPrecodeType { get; private set; }

        public byte ThisPointerRetBufPrecodeType { get; private set; }

        public byte UMEntryPrecodeType { get; private set; }

        /// <summary>
        /// Offset within a FixupPrecode of the code that follows its jump. On x64 this is 6,
        /// the same as the length of the rip-relative jump instruction the precode is made of.
        /// </summary>
        public byte FixupCodeOffset { get; private set; }

        public byte StubPrecodeSize { get; private set; }

        public byte FixupStubPrecodeSize { get; private set; }

        private static PrecodeMachineInfo Load()
        {
            var descriptor = ContractDescriptor.Current;
            var platformMetadata = descriptor.GetDataType("PlatformMetadata");
            var layout = descriptor.GetDataType("PrecodeMachineDescriptor");

            // PlatformMetadata is a struct in the runtime's image, so the global's address is the
            // struct itself - it is not a pointer variable and must not be dereferenced. The
            // PrecodeMachineDescriptor is embedded within it.
            var address = descriptor.Globals.Address("PlatformMetadata")
                          + platformMetadata["PrecodeMachineDescriptor"];

            var reader = new MemoryReader(address);

            return new PrecodeMachineInfo(address)
            {
                StubCodePageSize = reader.ReadUInt(layout["StubCodePageSize"]),
                InvalidPrecodeType = reader.ReadByte(layout["InvalidPrecodeType"]),
                StubPrecodeType = reader.ReadByte(layout["StubPrecodeType"]),
                FixupPrecodeType = reader.ReadByte(layout["FixupPrecodeType"]),
                PInvokeImportPrecodeType = reader.ReadByte(layout["PInvokeImportPrecodeType"]),
                ThisPointerRetBufPrecodeType = reader.ReadByte(layout["ThisPointerRetBufPrecodeType"]),
                UMEntryPrecodeType = reader.ReadByte(layout["UMEntryPrecodeType"]),
                FixupCodeOffset = reader.ReadByte(layout["FixupCodeOffset"]),
                StubPrecodeSize = reader.ReadByte(layout["StubPrecodeSize"]),
                FixupStubPrecodeSize = reader.ReadByte(layout["FixupStubPrecodeSize"])
            };
        }

        public override string ToString()
        {
            return $"PrecodeMachineDescriptor @0x{this.Address.ToInt64():x} " +
                   $"fixup={this.FixupPrecodeType} stub={this.StubPrecodeType} " +
                   $"invalid={this.InvalidPrecodeType} fixupCodeOffset={this.FixupCodeOffset} " +
                   $"stubPrecodeSize={this.StubPrecodeSize} codePageSize={this.StubCodePageSize}";
        }
    }
}
