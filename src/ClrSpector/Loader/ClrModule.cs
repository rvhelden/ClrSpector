using System;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// A loaded module as the runtime sees it, and the token-to-runtime-structure maps it keeps.
    /// </summary>
    /// <remarks>
    /// The interesting part is the maps. A module holds a lookup table from each metadata token
    /// to the runtime structure for it, so a <b>token</b> - or a name resolved to a token - can
    /// reach a MethodTable or a MethodDesc without a <see cref="Type"/> or
    /// <see cref="System.Reflection.MethodBase"/> ever existing. That is the direction reflection
    /// cannot go: it will hand you a <see cref="Type"/> for a token, but not the runtime's
    /// MethodTable for one.
    /// </remarks>
    public sealed unsafe class ClrModule
    {
        /// <summary>A token's row id is its low three bytes; the top byte names the table.</summary>
        private const uint RowIdMask = 0x00FFFFFF;

        private readonly DataType layout;
        private readonly DataType lookupMapLayout;

        private ClrModule(IntPtr address, ContractDescriptor descriptor)
        {
            this.Address = address;
            this.layout = descriptor.GetDataType("Module");
            this.lookupMapLayout = descriptor.GetDataType("ModuleLookupMap");

            var reader = new MemoryReader(address);

            this.Assembly = reader.ReadIntPtr(this.layout["Assembly"]);
            this.Base = reader.ReadIntPtr(this.layout["Base"]);
            this.Flags = reader.ReadUInt(this.layout["Flags"]);
            this.LoaderAllocator = reader.ReadIntPtr(this.layout["LoaderAllocator"]);

            // SimpleName is narrow, Path is wide - the runtime stores them differently.
            this.SimpleName = ReadNarrow(reader.ReadIntPtr(this.layout["SimpleName"]));
            this.Path = this.layout.HasField("Path")
                ? ReadWide(reader.ReadIntPtr(this.layout["Path"]))
                : null;
        }

        /// <summary>The runtime's Module.</summary>
        public IntPtr Address { get; }

        /// <summary>The module's Assembly.</summary>
        public IntPtr Assembly { get; }

        /// <summary>The loaded image's base address, or zero for a module with no image.</summary>
        public IntPtr Base { get; }

        public uint Flags { get; }

        /// <summary>The LoaderAllocator this module's runtime structures are allocated from.</summary>
        public IntPtr LoaderAllocator { get; }

        /// <summary>The module's simple name, e.g. "System.Private.CoreLib".</summary>
        public string SimpleName { get; }

        /// <summary>The module's file path, when it has one.</summary>
        public string Path { get; }

        /// <summary>Reads the runtime's Module for a reflection module.</summary>
        /// <remarks>
        /// Reached through a type the module declares, since a MethodTable records its module and
        /// reflection offers no direct route to it.
        /// </remarks>
        public static ClrModule Of(Type typeInModule)
        {
            if (typeInModule == null) throw new ArgumentNullException(nameof(typeInModule));

            var module = ClrObject.From(typeInModule).MethodTable.Module;

            return At(module);
        }

        /// <summary>Reads the runtime's Module at <paramref name="address"/>.</summary>
        public static ClrModule At(IntPtr address)
        {
            return address == IntPtr.Zero ? null : new ClrModule(address, ContractDescriptor.Current);
        }

        /// <summary>
        /// The MethodTable for a TypeDef token, or <see cref="IntPtr.Zero"/> when the module has
        /// not loaded that type yet.
        /// </summary>
        /// <remarks>
        /// A type the module declares but which nothing has touched has no MethodTable at all -
        /// the runtime builds one on first use - so zero means "not loaded", not "no such type".
        /// </remarks>
        public IntPtr TypeDefToMethodTable(uint typeDefToken)
        {
            return this.Lookup("TypeDefToMethodTableMap", typeDefToken);
        }

        /// <summary>The MethodTable for a TypeRef token this module references.</summary>
        public IntPtr TypeRefToMethodTable(uint typeRefToken)
        {
            return this.Lookup("TypeRefToMethodTableMap", typeRefToken);
        }

        /// <summary>The MethodDesc for a MethodDef token, or zero when not yet created.</summary>
        public IntPtr MethodDefToMethodDesc(uint methodDefToken)
        {
            return this.Lookup("MethodDefToDescMap", methodDefToken);
        }

        /// <summary>The FieldDesc for a FieldDef token, or zero when not yet created.</summary>
        public IntPtr FieldDefToFieldDesc(uint fieldDefToken)
        {
            return this.Lookup("FieldDefToDescMap", fieldDefToken);
        }

        /// <summary>
        /// The decoded type for a TypeDef token, or null when the module has not loaded it.
        /// </summary>
        public ClrMethodTable TypeDefToType(uint typeDefToken)
        {
            var methodTable = this.TypeDefToMethodTable(typeDefToken);

            return methodTable == IntPtr.Zero
                ? null
                : ClrMethodTable.Create(new MemoryReader(methodTable));
        }

        /// <summary>
        /// Walks one of the module's token maps.
        /// </summary>
        /// <remarks>
        /// A map is a linked list of blocks, each covering <c>Count</c> consecutive row ids from
        /// where the previous one stopped. Entries steal their low bits for flags, which
        /// <c>SupportedFlagsMask</c> names and which have to be masked off before the value is a
        /// pointer.
        /// </remarks>
        private IntPtr Lookup(string mapField, uint token)
        {
            if (!this.layout.HasField(mapField))
                return IntPtr.Zero;

            var rid = token & RowIdMask;
            if (rid == 0)
                return IntPtr.Zero;

            // The map field is embedded in the Module, so its address is the first block.
            var map = this.Address + this.layout[mapField];

            var countOffset = this.lookupMapLayout["Count"];
            var nextOffset = this.lookupMapLayout["Next"];
            var dataOffset = this.lookupMapLayout["TableData"];
            var maskOffset = this.lookupMapLayout["SupportedFlagsMask"];

            var flagsMask = (ulong)new MemoryReader(map).ReadIntPtr(maskOffset).ToInt64();

            var index = rid;

            while (map != IntPtr.Zero)
            {
                var reader = new MemoryReader(map);
                var count = (uint)reader.ReadIntPtr(countOffset).ToInt64();

                if (index < count)
                {
                    var tableData = reader.ReadIntPtr(dataOffset);
                    if (tableData == IntPtr.Zero)
                        return IntPtr.Zero;

                    var raw = (ulong)new MemoryReader(tableData)
                        .ReadIntPtr((int)(index * (uint)IntPtr.Size)).ToInt64();

                    return new IntPtr((long)(raw & ~flagsMask));
                }

                index -= count;
                map = reader.ReadIntPtr(nextOffset);
            }

            return IntPtr.Zero;
        }

        private static string ReadNarrow(IntPtr address)
        {
            return address == IntPtr.Zero ? null : new MemoryReader(address).ReadNullTerminatedString(0);
        }

        private static string ReadWide(IntPtr address)
        {
            return address == IntPtr.Zero ? null : new MemoryReader(address).ReadNullTerminatedWideString(0);
        }

        public override string ToString()
        {
            return $"Module @0x{this.Address.ToInt64():x} {this.SimpleName} " +
                   $"base=0x{this.Base.ToInt64():x} flags=0x{this.Flags:x}";
        }
    }
}
