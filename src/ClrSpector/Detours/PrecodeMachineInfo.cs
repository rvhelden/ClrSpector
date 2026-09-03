using System;
using ClrSpector.Cdac;

namespace ClrSpector.Detours
{
    /// <summary>
    /// The runtime's own description of how precodes are built on this machine, read from the
    /// contract descriptor's <c>PrecodeMachineDescriptor</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the runtime's constants, not this library's assumptions. They are exposed mainly
    /// so a precode can be identified and reported rather than guessed at.
    /// </para>
    /// <para>
    /// Only <c>InvalidPrecodeType</c>, <c>StubPrecodeType</c> and <c>StubCodePageSize</c> are
    /// guaranteed. Everything else depends on what the runtime was built with - a target without
    /// COM interop publishes no <c>ThisPointerRetBufPrecodeType</c>, for one - so the rest is
    /// nullable and absence is reported rather than thrown. The runtime's own reader treats these
    /// fields the same way.
    /// </para>
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

        public byte? FixupPrecodeType { get; private set; }

        public byte? PInvokeImportPrecodeType { get; private set; }

        public byte? ThisPointerRetBufPrecodeType { get; private set; }

        public byte? UMEntryPrecodeType { get; private set; }

        public byte? InterpreterPrecodeType { get; private set; }

        public byte? DynamicHelperPrecodeType { get; private set; }

        /// <summary>
        /// Offset within a FixupPrecode of the code that follows its jump. On x64 this is 6,
        /// the same as the length of the rip-relative jump instruction the precode is made of.
        /// </summary>
        public byte? FixupCodeOffset { get; private set; }

        public byte? StubPrecodeSize { get; private set; }

        public byte? FixupStubPrecodeSize { get; private set; }

        /// <summary>
        /// The machine code a StubPrecode begins with, as the runtime built it.
        /// </summary>
        /// <remarks>
        /// Published so a precode can be recognised by comparing against the runtime's own
        /// template instead of against opcodes written down here. <see cref="StubIgnoredBytes"/>
        /// marks the positions that vary between instances - the embedded addresses - and must be
        /// masked out of the comparison. See <see cref="Matches"/>.
        /// </remarks>
        public byte[] StubBytes { get; private set; }

        /// <summary>Which <see cref="StubBytes"/> positions vary and must not be compared.</summary>
        public byte[] StubIgnoredBytes { get; private set; }

        /// <summary>The machine code a FixupPrecode begins with, as the runtime built it.</summary>
        public byte[] FixupBytes { get; private set; }

        /// <summary>Which <see cref="FixupBytes"/> positions vary and must not be compared.</summary>
        public byte[] FixupIgnoredBytes { get; private set; }

        /// <summary>
        /// Whether <paramref name="code"/> is an instance of the precode shape described by
        /// <paramref name="template"/>, ignoring the positions <paramref name="ignored"/> marks
        /// as varying.
        /// </summary>
        /// <remarks>
        /// This is the runtime's own answer to "is this a precode", rather than a hardcoded
        /// opcode test: the template comes from the same build that emitted the stub.
        /// </remarks>
        public static bool Matches(byte[] code, byte[] template, byte[] ignored)
        {
            if (code == null || template == null || code.Length < template.Length)
                return false;

            for (var i = 0; i < template.Length; i++)
            {
                if (ignored != null && i < ignored.Length && ignored[i] != 0)
                    continue;

                if (code[i] != template[i])
                    return false;
            }

            return true;
        }

        /// <summary>Whether these bytes look like a FixupPrecode built by this runtime.</summary>
        public bool IsFixupPrecode(byte[] code) =>
            this.FixupBytes != null && Matches(code, this.FixupBytes, this.FixupIgnoredBytes);

        /// <summary>Whether these bytes look like a StubPrecode built by this runtime.</summary>
        public bool IsStubPrecode(byte[] code) =>
            this.StubBytes != null && Matches(code, this.StubBytes, this.StubIgnoredBytes);

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

            var info = new PrecodeMachineInfo(address)
            {
                // Always published: without these nothing about a precode can be said at all.
                StubCodePageSize = reader.ReadUInt(layout["StubCodePageSize"]),
                InvalidPrecodeType = reader.ReadByte(layout["InvalidPrecodeType"]),
                StubPrecodeType = reader.ReadByte(layout["StubPrecodeType"]),

                FixupPrecodeType = OptionalByte(reader, layout, "FixupPrecodeType"),
                PInvokeImportPrecodeType = OptionalByte(reader, layout, "PInvokeImportPrecodeType"),
                ThisPointerRetBufPrecodeType = OptionalByte(reader, layout, "ThisPointerRetBufPrecodeType"),
                UMEntryPrecodeType = OptionalByte(reader, layout, "UMEntryPrecodeType"),
                InterpreterPrecodeType = OptionalByte(reader, layout, "InterpreterPrecodeType"),
                DynamicHelperPrecodeType = OptionalByte(reader, layout, "DynamicHelperPrecodeType"),
                FixupCodeOffset = OptionalByte(reader, layout, "FixupCodeOffset"),
                StubPrecodeSize = OptionalByte(reader, layout, "StubPrecodeSize"),
                FixupStubPrecodeSize = OptionalByte(reader, layout, "FixupStubPrecodeSize")
            };

            // The byte templates are sized by the corresponding precode size, so they can only be
            // read once that has been.
            info.StubBytes = OptionalBytes(reader, layout, "StubBytes", info.StubPrecodeSize);
            info.StubIgnoredBytes = OptionalBytes(reader, layout, "StubIgnoredBytes", info.StubPrecodeSize);
            info.FixupBytes = OptionalBytes(reader, layout, "FixupBytes", info.FixupStubPrecodeSize);
            info.FixupIgnoredBytes = OptionalBytes(reader, layout, "FixupIgnoredBytes", info.FixupStubPrecodeSize);

            return info;
        }

        private static byte? OptionalByte(MemoryReader reader, DataType layout, string field)
        {
            return layout.HasField(field) ? reader.ReadByte(layout[field]) : (byte?)null;
        }

        private static byte[] OptionalBytes(MemoryReader reader, DataType layout, string field, byte? length)
        {
            if (length == null || !layout.HasField(field))
                return null;

            var bytes = new byte[length.Value];
            var offset = layout[field];

            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = reader.ReadByte(offset + i);

            return bytes;
        }

        public override string ToString()
        {
            return $"PrecodeMachineDescriptor @0x{this.Address.ToInt64():x} " +
                   $"fixup={Describe(this.FixupPrecodeType)} stub={this.StubPrecodeType} " +
                   $"invalid={this.InvalidPrecodeType} " +
                   $"fixupCodeOffset={Describe(this.FixupCodeOffset)} " +
                   $"stubPrecodeSize={Describe(this.StubPrecodeSize)} " +
                   $"codePageSize={this.StubCodePageSize}";
        }

        private static string Describe(byte? value) => value?.ToString() ?? "-";
    }
}