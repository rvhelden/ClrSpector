using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ClrSpector
{
    /// <summary>
    /// A module's portable PDB, and the local variable names only it has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Local names are the one part of a method that is nowhere in the runtime. A method body
    /// records its locals' types and nothing else - ECMA-335 has no name column for a local -
    /// and the runtime never loads a PDB, so no amount of reading its structures will produce
    /// one. Only the compiler's debug output has them, in a portable PDB's
    /// <see cref="MetadataTable.LocalVariable"/> table, indexed by the same slot number the IL
    /// loads and stores by.
    /// </para>
    /// <para>
    /// A portable PDB is itself an ECMA-335 metadata container - the same root, streams and
    /// tables as a module's own - so <see cref="MetadataImage"/> reads it with the schema
    /// extended by the PDB's tables and nothing else. There are two places to find one, and both
    /// are named by the mapped image's debug directory:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>Embedded</b> (<c>DebugType=embedded</c>): the PDB is in the image itself, deflated,
    /// in a debug directory entry of type 17. That is read straight out of mapped memory, like
    /// everything else here.
    /// </item>
    /// <item>
    /// <b>A file beside the assembly</b> (the default): a CodeView entry names its path and a
    /// GUID. That path is read from disk - the one place this library touches a file - and the
    /// PDB is only accepted when its own id matches the GUID the image recorded, so a stale PDB
    /// from an earlier build is rejected rather than believed.
    /// </item>
    /// </list>
    /// </remarks>
    public sealed unsafe class ClrModuleSymbols
    {
        /// <summary>The PE data directory that lists the debug directory entries.</summary>
        private const int DebugDirectoryIndex = 6;

        /// <summary>One debug directory entry is 28 bytes.</summary>
        private const int DebugEntrySize = 28;

        /// <summary>Debug entry type 2: a CodeView record naming a PDB file.</summary>
        private const uint CodeViewType = 2;

        /// <summary>Debug entry type 17: a portable PDB embedded in the image.</summary>
        private const uint EmbeddedPortablePdbType = 17;

        /// <summary>"RSDS" - the CodeView flavour that names a portable PDB.</summary>
        private const uint CodeViewSignature = 0x53445352;

        /// <summary>"MPDB" - the header an embedded portable PDB begins with.</summary>
        private const uint EmbeddedSignature = 0x4244504D;

        /// <summary>LocalVariable.Attributes bit 0: the compiler asks debuggers to hide it.</summary>
        private const ushort DebuggerHidden = 0x0001;

        private static readonly ConcurrentDictionary<IntPtr, ClrModuleSymbols> Cache =
            new ConcurrentDictionary<IntPtr, ClrModuleSymbols>();

        private readonly MetadataImage image;

        /// <summary>Slot names per MethodDef row id, read on first use.</summary>
        private Dictionary<uint, Dictionary<int, string>> localNames;

        private ClrModuleSymbols(IntPtr imageBase, MetadataImage image, string source, bool embedded)
        {
            this.ImageBase = imageBase;
            this.image = image;
            this.Source = source;
            this.IsEmbedded = embedded;
        }

        /// <summary>The base address of the module these symbols belong to.</summary>
        public IntPtr ImageBase { get; }

        /// <summary>Where the PDB was found: "embedded", or the path it was read from.</summary>
        public string Source { get; }

        /// <summary>True when the PDB came out of the image rather than off disk.</summary>
        public bool IsEmbedded { get; }

        /// <summary>The PDB's metadata, for anything beyond names.</summary>
        public MetadataImage Image => this.image;

        /// <summary>
        /// The symbols of <paramref name="module"/>, or null when it has none to be found.
        /// </summary>
        public static ClrModuleSymbols Of(ClrModule module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));

            return AtImageBase(module.Base);
        }

        /// <summary>
        /// The symbols of the image mapped at <paramref name="imageBase"/>, or null when the
        /// image names no PDB, the PDB is missing, or it does not match the image.
        /// </summary>
        /// <remarks>
        /// The result is cached per image, misses included: a module with no PDB is not worth
        /// looking for twice, and looking involves the file system.
        /// </remarks>
        public static ClrModuleSymbols AtImageBase(IntPtr imageBase)
        {
            if (imageBase == IntPtr.Zero)
                return null;

            var symbols = Cache.GetOrAdd(imageBase, Read);

            return symbols.image == null ? null : symbols;
        }

        /// <summary>
        /// The names of a method's local slots, by slot number. Empty when the PDB has none for
        /// it - a method the compiler generated, or one whose locals it marked hidden.
        /// </summary>
        /// <remarks>
        /// A slot can be named differently in different lexical scopes; what comes back is the
        /// first name each slot is given, which is the outermost scope that declares it.
        /// </remarks>
        public IReadOnlyDictionary<int, string> LocalNames(uint methodDefToken)
        {
            var rowId = methodDefToken & 0x00FFFFFF;

            return (this.localNames ??= this.ReadLocalNames()).TryGetValue(rowId, out var names)
                ? names
                : (IReadOnlyDictionary<int, string>)new Dictionary<int, string>();
        }

        public override string ToString()
        {
            if (this.image == null)
                return $"no symbols for the image at 0x{this.ImageBase.ToInt64():x}";

            return $"portable pdb from {this.Source}, " +
                   $"{this.image.RowCount(MetadataTable.LocalVariable)} named locals in " +
                   $"{this.image.RowCount(MetadataTable.LocalScope)} scopes";
        }

        /// <summary>
        /// Reads every named slot in the PDB once, grouped by the method that declares it.
        /// </summary>
        /// <remarks>
        /// One pass rather than a search per method: the scopes of a method are a run in a table
        /// sorted by method, but the variables of a scope are a run bounded by the <i>next</i>
        /// scope's start - which makes walking the whole table the simplest correct way to read
        /// it, and cheap enough at a few thousand rows.
        /// </remarks>
        private Dictionary<uint, Dictionary<int, string>> ReadLocalNames()
        {
            var names = new Dictionary<uint, Dictionary<int, string>>();

            var scopes = (uint)this.image.RowCount(MetadataTable.LocalScope);
            var variables = (uint)this.image.RowCount(MetadataTable.LocalVariable);

            if (scopes == 0 || variables == 0)
                return names;

            for (var scope = 1u; scope <= scopes; scope++)
            {
                // LocalScope: Method, ImportScope, VariableList, ConstantList, StartOffset, Length.
                var method = this.image.ReadColumn(MetadataTable.LocalScope, scope, 0);
                var first = this.image.ReadColumn(MetadataTable.LocalScope, scope, 2);

                var last = scope < scopes
                    ? this.image.ReadColumn(MetadataTable.LocalScope, scope + 1, 2)
                    : variables + 1;

                if (first == 0 || first > variables)
                    continue;

                if (!names.TryGetValue(method, out var slots))
                {
                    slots = new Dictionary<int, string>();
                    names[method] = slots;
                }

                for (var variable = first; variable < last && variable <= variables; variable++)
                {
                    // LocalVariable: Attributes, Index, Name.
                    var attributes = (ushort)this.image.ReadColumn(MetadataTable.LocalVariable, variable, 0);

                    if ((attributes & DebuggerHidden) != 0)
                        continue;

                    var slot = (int)this.image.ReadColumn(MetadataTable.LocalVariable, variable, 1);
                    var name = this.image.String(this.image.ReadColumn(MetadataTable.LocalVariable, variable, 2));

                    if (!string.IsNullOrEmpty(name) && !slots.ContainsKey(slot))
                        slots[slot] = name;
                }
            }

            return names;
        }

        /// <summary>
        /// Finds and opens whichever PDB the image at <paramref name="imageBase"/> names, or an
        /// instance with no metadata when there is none to open.
        /// </summary>
        /// <remarks>
        /// Nothing here throws. A missing, stale or unreadable PDB is an absence of names, not
        /// an error: everything else about the method is still readable without them.
        /// </remarks>
        private static ClrModuleSymbols Read(IntPtr imageBase)
        {
            var none = new ClrModuleSymbols(imageBase, null, null, false);

            try
            {
                var directory = ClrModuleMetadata.DataDirectory(imageBase, DebugDirectoryIndex);

                if (directory.Rva == 0 || directory.Size < DebugEntrySize)
                    return none;

                var entries = (int)(directory.Size / DebugEntrySize);
                var image = (byte*)imageBase;

                // The embedded PDB is preferred: it needs no file, and cannot be the wrong one.
                for (var i = 0; i < entries; i++)
                {
                    var entry = image + directory.Rva + i * DebugEntrySize;

                    if (*(uint*)(entry + 12) != EmbeddedPortablePdbType)
                        continue;

                    var embedded = ReadEmbedded(image + *(uint*)(entry + 20), *(uint*)(entry + 16));

                    if (embedded != null)
                        return Open(imageBase, embedded, "embedded", true, null);
                }

                for (var i = 0; i < entries; i++)
                {
                    var entry = image + directory.Rva + i * DebugEntrySize;

                    if (*(uint*)(entry + 12) != CodeViewType)
                        continue;

                    var symbols = ReadCodeView(imageBase, image + *(uint*)(entry + 20), *(uint*)(entry + 16));

                    if (symbols != null)
                        return symbols;
                }

                return none;
            }
            catch (Exception)
            {
                return none;
            }
        }

        /// <summary>
        /// Inflates an embedded portable PDB. The entry holds a four-byte signature, the size it
        /// decompresses to, and then a raw deflate stream.
        /// </summary>
        private static byte[] ReadEmbedded(byte* data, uint size)
        {
            if (size <= 8 || *(uint*)data != EmbeddedSignature)
                return null;

            var uncompressed = *(uint*)(data + 4);

            if (uncompressed == 0 || uncompressed > int.MaxValue)
                return null;

            var bytes = new byte[uncompressed];

            using (var compressed = new UnmanagedMemoryStream(data + 8, size - 8))
            using (var inflater = new DeflateStream(compressed, CompressionMode.Decompress))
            {
                var read = 0;

                while (read < bytes.Length)
                {
                    var got = inflater.Read(bytes, read, bytes.Length - read);

                    if (got == 0)
                        return null;

                    read += got;
                }
            }

            return bytes;
        }

        /// <summary>
        /// Opens the PDB a CodeView entry names: a signature, the id the PDB must match, an age,
        /// and the path it was written to.
        /// </summary>
        private static ClrModuleSymbols ReadCodeView(IntPtr imageBase, byte* data, uint size)
        {
            if (size <= 24 || *(uint*)data != CodeViewSignature)
                return null;

            var id = new byte[16];
            for (var i = 0; i < id.Length; i++)
                id[i] = data[4 + i];

            var recorded = new MemoryReader((IntPtr)data).ReadNullTerminatedString(24);
            var path = FindPdb(imageBase, recorded);

            if (path == null)
                return null;

            return Open(imageBase, File.ReadAllBytes(path), path, false, id);
        }

        /// <summary>
        /// Where the PDB actually is: the path the compiler recorded if it is still there, or
        /// the same file name next to the assembly, which is where the build copied it.
        /// </summary>
        private static string FindPdb(IntPtr imageBase, string recorded)
        {
            if (string.IsNullOrEmpty(recorded))
                return null;

            if (File.Exists(recorded))
                return recorded;

            var directory = DirectoryOf(imageBase);

            if (directory == null)
                return null;

            var beside = Path.Combine(directory, Path.GetFileName(recorded));

            return File.Exists(beside) ? beside : null;
        }

        /// <summary>
        /// The directory the assembly mapped at <paramref name="imageBase"/> was loaded from.
        /// </summary>
        /// <remarks>
        /// The recorded path is where the PDB was written on the machine that built it, which is
        /// not where it is once the build has copied it next to the assembly. Matching the image
        /// base against the loaded assemblies is the way to that directory that does not involve
        /// asking the operating system about mapped files.
        /// </remarks>
        private static string DirectoryOf(IntPtr imageBase)
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                        continue;

                    if (Marshal.GetHINSTANCE(assembly.ManifestModule) == imageBase)
                        return Path.GetDirectoryName(assembly.Location);
                }
            }
            catch (Exception)
            {
                // Not being able to name the assembly's directory is just one fewer place to
                // look for the PDB.
            }

            return null;
        }

        /// <summary>
        /// Reads <paramref name="bytes"/> as a portable PDB, rejecting one that does not belong
        /// to the image.
        /// </summary>
        /// <remarks>
        /// The bytes are pinned for as long as the symbols live, because
        /// <see cref="MetadataImage"/> reads rows in place rather than copying them. The
        /// instance is cached per module, so the pin lasts as long as the process - the same
        /// deal as the mapped image it stands in for.
        /// </remarks>
        private static ClrModuleSymbols Open(
            IntPtr imageBase, byte[] bytes, string source, bool embedded, byte[] expectedId)
        {
            if (bytes == null || bytes.Length == 0)
                return new ClrModuleSymbols(imageBase, null, null, false);

            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            var image = MetadataImage.At(pinned.AddrOfPinnedObject(), bytes.Length);

            if (!image.IsPortablePdb || !Matches(image.PdbId, expectedId))
            {
                pinned.Free();

                return new ClrModuleSymbols(imageBase, null, null, false);
            }

            return new ClrModuleSymbols(imageBase, image, source, embedded);
        }

        /// <summary>
        /// Whether a PDB's own id is the one the image asked for. A PDB from an earlier build of
        /// the same assembly parses perfectly and describes different code, so this is the check
        /// that stops it being trusted.
        /// </summary>
        private static bool Matches(byte[] id, byte[] expected)
        {
            if (expected == null)
                return true;

            if (id == null || id.Length < expected.Length)
                return false;

            for (var i = 0; i < expected.Length; i++)
            {
                if (id[i] != expected[i])
                    return false;
            }

            return true;
        }
    }
}
