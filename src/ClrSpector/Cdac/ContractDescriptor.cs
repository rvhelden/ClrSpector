using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClrSpector.Cdac
{
    /// <summary>
    /// The runtime's self-description: the cDAC contract descriptor exported by CoreCLR
    /// (.NET 9 and later) as the data symbol <c>DotNetRuntimeContractDescriptor</c>.
    ///
    /// It publishes the offsets of the very structures ClrSpector walks - MethodTable, EEClass,
    /// MethodDesc, MethodDescChunk and friends - so those offsets are read from the runtime at
    /// startup instead of being hardcoded per runtime version.
    /// </summary>
    public sealed unsafe class ContractDescriptor
    {
        /// <summary>"DNCCDAC\0" - the descriptor's leading 8 bytes.</summary>
        private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("DNCCDAC\0");

        private const string SymbolName = "DotNetRuntimeContractDescriptor";

        /// <summary>Bit 0 of the header flags: set when the runtime uses 64-bit pointers.</summary>
        private const uint Flags64Bit = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Header
        {
            public ulong Magic;
            public uint Flags;
            public uint DescriptorSize;
            public byte* Descriptor;
            public uint PointerDataCount;
            public uint Pad0;
            public nuint* PointerData;
        }

        private static readonly Lazy<ContractDescriptor> current =
            new Lazy<ContractDescriptor>(Load, isThreadSafe: true);

        /// <summary>The descriptor published by the runtime hosting this process.</summary>
        public static ContractDescriptor Current => current.Value;

        private readonly Dictionary<string, DataType> types;

        private ContractDescriptor(
            int version,
            string baseline,
            Dictionary<string, DataType> types,
            Globals globals,
            Dictionary<string, int> contracts)
        {
            this.Version = version;
            this.Baseline = baseline;
            this.types = types;
            this.Globals = globals;
            this.Contracts = contracts;
        }

        /// <summary>Descriptor format version. A change here is a signal to re-verify.</summary>
        public int Version { get; }

        /// <summary>The descriptor's baseline name (e.g. "empty").</summary>
        public string Baseline { get; }

        public Globals Globals { get; }

        /// <summary>Contract name to version, e.g. RuntimeTypeSystem -> 1.</summary>
        public IReadOnlyDictionary<string, int> Contracts { get; }

        public IEnumerable<string> TypeNames => this.types.Keys;

        /// <summary>
        /// The layout of a runtime structure, or a descriptive failure if this runtime does not
        /// describe it.
        /// </summary>
        public DataType GetDataType(string name)
        {
            if (this.types.TryGetValue(name, out var type))
                return type;

            throw new ClrSpectorUnsupportedRuntimeException(
                $"The contract descriptor does not describe the type '{name}'.");
        }

        public bool TryGetDataType(string name, out DataType type)
        {
            return this.types.TryGetValue(name, out type);
        }

        /// <summary>
        /// Requires a contract to be present at one of the versions this code was written
        /// against, so that an incompatible future revision fails loudly here.
        /// </summary>
        public void RequireContract(string name, params int[] supportedVersions)
        {
            if (!this.Contracts.TryGetValue(name, out var version))
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The contract descriptor does not publish the '{name}' contract.");

            if (Array.IndexOf(supportedVersions, version) < 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Contract '{name}' is version {version}; this build understands " +
                    $"version(s) {string.Join(", ", supportedVersions)}.");
        }

        private static ContractDescriptor Load()
        {
            var header = (Header*)ResolveSymbol();

            var magic = new ReadOnlySpan<byte>((byte*)&header->Magic, MagicBytes.Length);
            if (!magic.SequenceEqual(MagicBytes))
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The '{SymbolName}' export does not begin with the expected magic value.");

            var is64Bit = (header->Flags & Flags64Bit) != 0;
            if (is64Bit != (IntPtr.Size == 8))
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The descriptor reports {(is64Bit ? 64 : 32)}-bit pointers but this process " +
                    $"has {IntPtr.Size * 8}-bit pointers.");

            if (header->Descriptor == null || header->DescriptorSize == 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    "The descriptor's JSON payload is empty.");

            var json = Encoding.UTF8.GetString(header->Descriptor, (int)header->DescriptorSize);

            return Parse(json, header->PointerData, header->PointerDataCount);
        }

        private static IntPtr ResolveSymbol()
        {
            var attempts = new List<string>();

            // The runtime library sits alongside System.Private.CoreLib.
            var coreLib = typeof(object).Assembly.Location;
            if (!string.IsNullOrEmpty(coreLib))
            {
                var candidate = Path.Combine(Path.GetDirectoryName(coreLib), RuntimeLibraryName());
                attempts.Add(candidate);

                if (File.Exists(candidate)
                    && NativeLibrary.TryLoad(candidate, out var runtimeHandle)
                    && NativeLibrary.TryGetExport(runtimeHandle, SymbolName, out var fromRuntime))
                {
                    return fromRuntime;
                }
            }

            // Single-file and self-contained hosts link the runtime into the host executable.
            attempts.Add("the host executable");
            if (NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), SymbolName, out var fromHost))
                return fromHost;

            throw new ClrSpectorUnsupportedRuntimeException(
                $"Could not find the '{SymbolName}' export, which CoreCLR publishes from .NET 9 onwards. " +
                $"Looked in: {string.Join("; ", attempts)}.");
        }

        private static string RuntimeLibraryName()
        {
            if (OperatingSystem.IsWindows()) return "coreclr.dll";
            if (OperatingSystem.IsMacOS()) return "libcoreclr.dylib";
            return "libcoreclr.so";
        }

        private static ContractDescriptor Parse(string json, nuint* pointerData, uint pointerDataCount)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetInt32() : 0;
            var baseline = root.TryGetProperty("baseline", out var baselineElement) ? baselineElement.GetString() : null;

            var types = new Dictionary<string, DataType>(StringComparer.Ordinal);
            if (root.TryGetProperty("types", out var typesElement))
            {
                foreach (var typeProperty in typesElement.EnumerateObject())
                    types.Add(typeProperty.Name, ParseType(typeProperty.Name, typeProperty.Value));
            }

            var globalValues = new Dictionary<string, GlobalValue>(StringComparer.Ordinal);
            if (root.TryGetProperty("globals", out var globalsElement))
            {
                foreach (var globalProperty in globalsElement.EnumerateObject())
                    globalValues.Add(globalProperty.Name, ParseGlobal(globalProperty.Name, globalProperty.Value));
            }

            var contracts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (root.TryGetProperty("contracts", out var contractsElement))
            {
                foreach (var contractProperty in contractsElement.EnumerateObject())
                    contracts.Add(contractProperty.Name, contractProperty.Value.GetInt32());
            }

            return new ContractDescriptor(
                version,
                baseline,
                types,
                new Globals(globalValues, pointerData, pointerDataCount),
                contracts);
        }

        /// <summary>
        /// A type is an object of field names to offsets. A field is either a bare offset or
        /// <c>[offset, "typename"]</c>. The reserved key "!" carries the structure's size.
        /// </summary>
        private static DataType ParseType(string name, JsonElement element)
        {
            uint? size = null;
            var fields = new Dictionary<string, FieldLayout>(StringComparer.Ordinal);

            foreach (var field in element.EnumerateObject())
            {
                if (field.Name == "!")
                {
                    size = field.Value.GetUInt32();
                    continue;
                }

                fields.Add(field.Name, ParseField(name, field.Name, field.Value));
            }

            return new DataType(name, size, fields);
        }

        private static FieldLayout ParseField(string typeName, string fieldName, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    return new FieldLayout(element.GetInt32(), null);

                case JsonValueKind.Array:
                {
                    // [offset, "typename"]
                    var offset = 0;
                    string fieldTypeName = null;
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (index == 0) offset = item.GetInt32();
                        else if (index == 1) fieldTypeName = item.GetString();
                        index++;
                    }

                    return new FieldLayout(offset, fieldTypeName);
                }

                default:
                    throw new ClrSpectorUnsupportedRuntimeException(
                        $"Field '{typeName}.{fieldName}' has an unexpected descriptor encoding ({element.ValueKind}).");
            }
        }

        /// <summary>
        /// A global is <c>[value, "type"]</c> where value is either a literal (a hex string or a
        /// plain string) or <c>[index]</c> referring to the pointer-data table. A bare value
        /// without the type pair is also accepted.
        /// </summary>
        private static GlobalValue ParseGlobal(string name, JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
                return GlobalValue.FromLiteral(Literal(element));

            var first = default(JsonElement);
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (index == 0) first = item;
                index++;
            }

            if (index == 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Global '{name}' has an empty descriptor encoding.");

            // [[index], "pointer"] - indirect through the pointer-data table.
            if (first.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in first.EnumerateArray())
                    return GlobalValue.FromPointerData(item.GetUInt32());

                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Global '{name}' has an empty pointer-data reference.");
            }

            return GlobalValue.FromLiteral(Literal(first));
        }

        private static string Literal(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.GetRawText();
        }
    }
}
