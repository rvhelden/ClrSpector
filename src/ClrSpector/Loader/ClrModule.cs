using System;
using System.Collections.Generic;
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

            this.FileName = this.layout.HasField("FileName")
                ? ReadWide(reader.ReadIntPtr(this.layout["FileName"]))
                : null;

            this.PEAssembly = this.Pointer(reader, "PEAssembly");
            this.ReadyToRunInfo = this.Pointer(reader, "ReadyToRunInfo");
            this.DynamicMetadata = this.Pointer(reader, "DynamicMetadata");
            this.GrowableSymbolStream = this.Pointer(reader, "GrowableSymbolStream");
            this.AvailableTypeParams = this.Pointer(reader, "AvailableTypeParams");
            this.InstMethodHashTable = this.Pointer(reader, "InstMethodHashTable");
            this.DynamicILBlobTable = this.Pointer(reader, "DynamicILBlobTable");
            this.EnCClassList = this.Pointer(reader, "EnCClassList");

            if (this.layout.HasField("MetadataGeneration"))
                this.MetadataGeneration = reader.ReadUInt(this.layout["MetadataGeneration"]);
        }

        /// <summary>A pointer field, or zero when this runtime does not publish it.</summary>
        private IntPtr Pointer(MemoryReader reader, string field)
        {
            return this.layout.HasField(field) ? reader.ReadIntPtr(this.layout[field]) : IntPtr.Zero;
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

        /// <summary>
        /// The module's file name. Distinct from <see cref="Path"/>, which is the full path, and
        /// from <see cref="SimpleName"/>, which drops the extension.
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// The PEAssembly - the loaded file behind this module, and the route to its
        /// <c>PEImage</c> and metadata importer.
        /// </summary>
        public IntPtr PEAssembly { get; }

        /// <summary>
        /// The module's ReadyToRun information, or zero when it was not compiled ahead of time.
        /// </summary>
        /// <remarks>
        /// Non-zero means the module ships precompiled native code, which is why some of its
        /// methods have an entry point before anything has jitted them. The structure behind this
        /// is described by the descriptor's <c>ReadyToRunInfo</c> type and read by the
        /// ExecutionManager contract; this is the address, not a decode of it.
        /// </remarks>
        public IntPtr ReadyToRunInfo { get; }

        /// <summary>True when the module carries ahead-of-time compiled code.</summary>
        public bool IsReadyToRun => this.ReadyToRunInfo != IntPtr.Zero;

        /// <summary>
        /// Metadata the module grew at runtime, for a module built by <c>AssemblyBuilder</c>.
        /// </summary>
        /// <remarks>
        /// A dynamic module has no mapped image, so <see cref="ClrModuleMetadata"/> finds nothing
        /// for it - its metadata is here instead, in a growable buffer rather than a PE section.
        /// </remarks>
        public IntPtr DynamicMetadata { get; }

        /// <summary>The in-memory symbol stream, when debug symbols were supplied at runtime.</summary>
        public IntPtr GrowableSymbolStream { get; }

        /// <summary>
        /// The hash table of type parameters instantiated from this module - the arrays, pointers
        /// and byrefs built over its types, which have no metadata row of their own.
        /// </summary>
        public IntPtr AvailableTypeParams { get; }

        /// <summary>
        /// The hash table of instantiated generic methods, which likewise have no metadata row -
        /// <c>List&lt;int&gt;.Add</c> is not in any module's MethodDef table.
        /// </summary>
        public IntPtr InstMethodHashTable { get; }

        /// <summary>
        /// Every constructed generic method the runtime has built for this module.
        /// </summary>
        /// <remarks>
        /// <para>
        /// These are the MethodDescs metadata cannot reach. <c>Echo&lt;int&gt;</c> has no
        /// MethodDef row - only <c>Echo&lt;T&gt;</c> does - and it is not in its type's
        /// MethodDescChunks either, because the runtime creates it on first use and files it
        /// here. Enumerating this table is the only way to see which instantiations a process
        /// has actually made.
        /// </para>
        /// <para>
        /// Read lazily, so a caller can stop early; the table is not copied. It is also live -
        /// a new instantiation can appear while it is being walked - so a long walk of a busy
        /// process may see a table mid-resize, which the walk handles by following the older
        /// bucket array too.
        /// </para>
        /// <para>
        /// The walk is exhaustive: it returns exactly as many entries as
        /// <see cref="InstantiatedMethodCount"/> reports. Measured 11 of 11 in a small assembly
        /// and 156 of 156 in CoreLib. Compare the two anyway if it matters, since a table that
        /// grows mid-walk will legitimately end above where it started.
        /// </para>
        /// </remarks>
        public IEnumerable<ClrMethodDescription> InstantiatedMethods
        {
            get
            {
                // An entry packs flags into the low bits of its MethodDesc pointer, which
                // MethodDescAlignment leaves free. Measured: entries came back as 0x...3a, two
                // bytes off an 8-aligned MethodDesc - and decoding that unmasked reads every
                // field at the wrong offset, which produces a plausible-looking wrong method
                // rather than a failure.
                var alignment = (long)ContractDescriptor.Current.Globals.Number("MethodDescAlignment");
                var mask = ~(alignment - 1);

                foreach (var value in DacEnumerableHashTable.Values(
                             this.InstMethodHashTable, "InstMethodHashTable"))
                {
                    ClrMethodDescription method = null;

                    try
                    {
                        method = ClrMethodDescription.At(new IntPtr(value.ToInt64() & mask));
                    }
                    catch (ClrSpectorUnsupportedRuntimeException)
                    {
                        // An entry that will not decode is skipped rather than ending the walk.
                    }

                    if (method != null)
                        yield return method;
                }
            }
        }

        /// <summary>
        /// The same walk, with the flags each entry carries alongside its MethodDesc.
        /// </summary>
        /// <remarks>
        /// The table stores these in the spare low bits of the MethodDesc pointer rather than in
        /// a field, so they come for free once the pointer has been masked - and they say
        /// something the MethodDesc alone does not: whether this entry is the unboxing stub for a
        /// value type, and whether the method needs its instantiation handed to it at run time
        /// because it shares code with other instantiations.
        /// </remarks>
        public IEnumerable<(ClrMethodDescription Method, InstantiatedMethodFlags Flags)>
            InstantiatedMethodEntries
        {
            get
            {
                var alignment = (long)ContractDescriptor.Current.Globals.Number("MethodDescAlignment");
                var mask = ~(alignment - 1);

                foreach (var value in DacEnumerableHashTable.Values(
                             this.InstMethodHashTable, "InstMethodHashTable"))
                {
                    ClrMethodDescription method = null;

                    try
                    {
                        method = ClrMethodDescription.At(new IntPtr(value.ToInt64() & mask));
                    }
                    catch (ClrSpectorUnsupportedRuntimeException)
                    {
                        // An entry that will not decode is skipped rather than ending the walk.
                    }

                    if (method != null)
                        yield return (method, (InstantiatedMethodFlags)(value.ToInt64() & ~mask));
                }
            }
        }

        /// <summary>
        /// How many entries the instantiated-method table records, for checking a walk of it.
        /// </summary>
        /// <remarks>
        /// This is the number of table entries, not of distinct methods: two entries can name
        /// the same MethodDesc. Measured: CoreLib reported 149 entries over 140 distinct
        /// MethodDescs.
        ///
        /// It is also the yardstick for <see cref="InstantiatedMethods"/>, which matches it
        /// exactly on a table that is not being changed underneath the walk.
        /// </remarks>
        public long InstantiatedMethodCount =>
            DacEnumerableHashTable.CountOf(this.InstMethodHashTable, "InstMethodHashTable");

        /// <summary>
        /// Every constructed generic type, array, pointer and byref the runtime has built for
        /// this module, as type handles.
        /// </summary>
        /// <remarks>
        /// The type-level counterpart, and it has the same problem: <c>List&lt;int&gt;</c> and
        /// <c>int[]</c> have no TypeDef row of their own. Not every handle is a MethodTable -
        /// a pointer or byref type is a TypeDesc - so check with
        /// <see cref="ClrMethodTable.IsMethodTableHandle"/> before decoding one.
        /// </remarks>
        public IEnumerable<IntPtr> ConstructedTypeHandles =>
            DacEnumerableHashTable.Values(this.AvailableTypeParams, "EETypeHashTable");

        /// <summary>How many entries the constructed-type table records.</summary>
        public long ConstructedTypeCount =>
            DacEnumerableHashTable.CountOf(this.AvailableTypeParams, "EETypeHashTable");

        /// <summary>IL supplied at runtime for a method, keyed by token; used by profilers and ENC.</summary>
        public IntPtr DynamicILBlobTable { get; }

        /// <summary>The types edit-and-continue has changed in this module.</summary>
        public IntPtr EnCClassList { get; }

        /// <summary>
        /// How many times this module's metadata has been updated in place, by edit-and-continue
        /// or a hot reload. Zero for a module nothing has edited.
        /// </summary>
        /// <remarks>
        /// Worth checking before trusting anything read from the mapped image: an updated module's
        /// current metadata lives in the runtime's own tables, not in the file it was loaded from,
        /// so a non-zero generation means <see cref="ClrModuleMetadata"/> is reading a stale view.
        /// </remarks>
        public uint MetadataGeneration { get; }

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
        /// <summary>
        /// The Module at <paramref name="address"/>, or null when there is not one there.
        /// </summary>
        /// <remarks>
        /// The address is validated before it is read. Callers reach this with pointers taken out
        /// of other structures - a MethodTable's Module field, an AssemblyRef map entry - and one
        /// of those can be null, stale or torn if the structure it came from was not itself
        /// trustworthy. Reading unmapped memory raises an access violation, which in .NET is a
        /// fatal error rather than a catchable exception, so there is no recovering from having
        /// read it: the check has to come first.
        /// </remarks>
        public static ClrModule At(IntPtr address)
        {
            var descriptor = ContractDescriptor.Current;

            if (!IsReadableStructure(address, descriptor, "Module"))
                return null;

            return new ClrModule(address, descriptor);
        }

        /// <summary>
        /// Whether a structure of the named descriptor type can be read at
        /// <paramref name="address"/>.
        /// </summary>
        /// <remarks>
        /// The alignment test comes first because it costs nothing and no runtime structure is
        /// ever unaligned; the page probe behind it is memoised per page.
        /// </remarks>
        internal static bool IsReadableStructure(
            IntPtr address, ContractDescriptor descriptor, string typeName)
        {
            if (address == IntPtr.Zero || address.ToInt64() % IntPtr.Size != 0)
                return false;

            var size = descriptor.GetDataType(typeName).Size ?? (uint)IntPtr.Size;

            return ProcessMemoryRegions.IsReadable(address, size);
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

        /// <summary>
        /// The Module an AssemblyRef token resolves to, or zero when this module has not needed
        /// that reference yet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the module's own view of its dependencies: metadata lists what it references
        /// by name, and this map records what each of those names turned out to be at run time.
        /// Zero means the reference has not been bound yet, since the runtime binds them lazily -
        /// not that the dependency is missing.
        /// </para>
        /// <para>
        /// It stores a <b>Module</b>, not an Assembly, despite the descriptor calling the field
        /// <c>ManifestModuleReferencesMap</c> and the runtime's own setter being named
        /// <c>StoreAssemblyRef</c>. That setter takes an <c>Assembly*</c> and stores
        /// <c>value-&gt;GetModule()</c>, and the field is declared
        /// <c>LookupMap&lt;PTR_Module&gt;</c> (<c>ceeload.h</c>). Reading it as an Assembly
        /// produces a structure that decodes without complaint and reports an empty name and a
        /// null manifest module - so the mistake shows up as a resolution that quietly never
        /// succeeds rather than as a crash. <see cref="AssemblyRefToAssembly"/> is the other step.
        /// </para>
        /// </remarks>
        public IntPtr AssemblyRefToModule(uint assemblyRefToken)
        {
            return this.Lookup("ManifestModuleReferencesMap", assemblyRefToken);
        }

        /// <summary>
        /// The Assembly an AssemblyRef token resolves to, or zero when the reference has not been
        /// bound yet.
        /// </summary>
        /// <remarks>
        /// One hop further than <see cref="AssemblyRefToModule"/>, since what the map holds is
        /// the referenced module and the assembly is the module's owner.
        /// </remarks>
        public IntPtr AssemblyRefToAssembly(uint assemblyRefToken)
        {
            var module = this.AssemblyRefToModule(assemblyRefToken);

            return module == IntPtr.Zero ? IntPtr.Zero : At(module).Assembly;
        }

        /// <summary>
        /// The runtime structure a MemberRef token resolves to - a MethodDesc or a FieldDesc,
        /// depending on what the reference names.
        /// </summary>
        /// <remarks>
        /// A MemberRef is how a module names a member of a type it does not declare, so this is
        /// the map that turns a cross-assembly call site into the callee's own MethodDesc.
        /// </remarks>
        public IntPtr MemberRefToDesc(uint memberRefToken)
        {
            return this.Lookup("MemberRefToDescMap", memberRefToken);
        }

        /// <summary>
        /// The IL code versioning state for a MethodDef token, or zero when the method has never
        /// been re-jitted.
        /// </summary>
        /// <remarks>
        /// Non-zero means something has given the method an alternative body - a profiler ReJIT,
        /// or a hot reload - so the IL in the mapped image is no longer what runs.
        /// </remarks>
        public IntPtr MethodDefToILCodeVersioningState(uint methodDefToken)
        {
            return this.Lookup("MethodDefToILCodeVersioningStateMap", methodDefToken);
        }

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