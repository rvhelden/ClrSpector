using System;
using System.Collections.Generic;
using System.Runtime;
using System.Text;

namespace ClrSpector.Cdac
{
    /// <summary>
    /// The GC's own contract descriptor: the layouts of the generation table, heap segments and
    /// regions that a heap walk needs.
    /// </summary>
    /// <remarks>
    /// The GC publishes a descriptor separate from the runtime's, and unlike
    /// <c>DotNetRuntimeContractDescriptor</c> it is <b>not exported</b> - no export names it and
    /// no global in the runtime descriptor points at it. The GC is pluggable, so its descriptor
    /// cannot be a fixed export of the runtime.
    ///
    /// What the runtime does instead is embed one descriptor per GC flavour it was built with and
    /// leave them in its data section. On .NET 11 x64 there are two, a few kilobytes from the
    /// exported runtime header: one for workstation and one for server, each naming itself in a
    /// <c>GCIdentifiers</c> global ("workstation, regions, background," and
    /// "server, regions, background, dynamic_heap"). Only one of them describes the GC actually
    /// running.
    ///
    /// So they are found by searching the runtime module's readable regions for the
    /// <c>DNCCDAC</c> header magic, and the right one is picked by matching <c>GCIdentifiers</c>
    /// against <see cref="GCSettings.IsServerGC"/>. Picking the wrong one would not crash - it
    /// would silently report a plausible but wrong heap - so an ambiguous or empty result fails
    /// loudly instead.
    ///
    /// .NET 10 and earlier publish no GC descriptor at all, which is why heap walking needs
    /// .NET 11.
    /// </remarks>
    public static class GcContractDescriptor
    {
        /// <summary>"DNCCDAC\0" - shared with the runtime descriptor's header.</summary>
        private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("DNCCDAC\0");

        /// <summary>The contract every GC descriptor publishes, and the runtime's does not.</summary>
        private const string GcContractName = "GC";

        /// <summary>The global naming which GC flavour a descriptor describes.</summary>
        private const string IdentifiersGlobal = "GCIdentifiers";

        private static readonly Lazy<ContractDescriptor> current =
            new Lazy<ContractDescriptor>(Load, isThreadSafe: true);

        /// <summary>The GC descriptor matching the GC this process is running.</summary>
        public static ContractDescriptor Current => current.Value;

        /// <summary>
        /// The <c>GCIdentifiers</c> string of the selected descriptor, e.g.
        /// "workstation, regions, background,". Useful in a dump header and in failure messages.
        /// </summary>
        public static string Identifiers => Current.Globals.Text(IdentifiersGlobal);

        /// <summary>True when the selected descriptor describes a region-based heap.</summary>
        public static bool UsesRegions => Identifiers.Contains("regions");

        /// <summary>True when the selected descriptor describes server GC.</summary>
        public static bool IsServer => Identifiers.Contains("server");

        private static ContractDescriptor Load()
        {
            // The exported runtime header is the anchor: the GC headers sit in the same module.
            var anchor = ContractDescriptor.ResolveSymbol();
            var candidates = FindGcDescriptors(anchor);

            if (candidates.Count == 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    "No GC contract descriptor was found in the runtime module. .NET 10 and " +
                    "earlier do not publish one, so the GC heap cannot be walked on this " +
                    "runtime; .NET 11 or later is required.");

            var wantServer = GCSettings.IsServerGC;
            var matches = new List<ContractDescriptor>();
            foreach (var candidate in candidates)
            {
                var identifiers = candidate.Globals.Has(IdentifiersGlobal)
                    ? candidate.Globals.Text(IdentifiersGlobal)
                    : string.Empty;

                // "server" and "workstation" are distinct tokens, so a plain containment test
                // separates the two flavours without parsing the list.
                var describesServer = identifiers.Contains("server");
                if (describesServer == wantServer)
                    matches.Add(candidate);
            }

            if (matches.Count == 1)
                return matches[0];

            throw new ClrSpectorUnsupportedRuntimeException(
                $"Expected exactly one GC contract descriptor for " +
                $"{(wantServer ? "server" : "workstation")} GC but found {matches.Count}. " +
                $"Descriptors present: {Describe(candidates)}. Reading the wrong one would " +
                $"report a plausible but wrong heap, so no choice is made.");
        }

        private static string Describe(IReadOnlyList<ContractDescriptor> candidates)
        {
            var parts = new List<string>();
            foreach (var candidate in candidates)
            {
                parts.Add(candidate.Globals.Has(IdentifiersGlobal)
                    ? $"\"{candidate.Globals.Text(IdentifiersGlobal)}\""
                    : "<no GCIdentifiers>");
            }

            return parts.Count == 0 ? "<none>" : string.Join(", ", parts);
        }

        /// <summary>
        /// Scans the readable regions of the module holding <paramref name="anchor"/> for
        /// descriptor headers that publish the GC contract.
        /// </summary>
        private static unsafe IReadOnlyList<ContractDescriptor> FindGcDescriptors(IntPtr anchor)
        {
            var found = new List<ContractDescriptor>();

            foreach (var region in ProcessMemoryRegions.OfModuleContaining(anchor))
            {
                var start = (byte*)region.Start;

                // The header begins with an eight-byte magic value and the linker gives it at
                // least pointer alignment, so only aligned positions need testing.
                for (long offset = 0; offset + MagicBytes.Length <= region.Length; offset += sizeof(long))
                {
                    var position = start + offset;
                    if (!MatchesMagic(position))
                        continue;

                    // The runtime's own descriptor shares the magic; it is not a GC descriptor.
                    if (position == (byte*)anchor)
                        continue;

                    // A header found by scanning is not guaranteed to be one - the magic could
                    // occur in unrelated data - so a candidate that will not parse is skipped
                    // rather than allowed to fail the whole search.
                    ContractDescriptor candidate;
                    try
                    {
                        candidate = ContractDescriptor.FromHeader(
                            (IntPtr)position,
                            $"the descriptor header found at 0x{(long)position:x}");
                    }
                    catch (ClrSpectorUnsupportedRuntimeException)
                    {
                        continue;
                    }

                    if (candidate.Contracts.ContainsKey(GcContractName))
                        found.Add(candidate);
                }
            }

            return found;
        }

        private static unsafe bool MatchesMagic(byte* position)
        {
            for (var i = 0; i < MagicBytes.Length; i++)
            {
                if (position[i] != MagicBytes[i])
                    return false;
            }

            return true;
        }
    }
}
