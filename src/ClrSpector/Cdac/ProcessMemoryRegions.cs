using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace ClrSpector.Cdac
{
    /// <summary>
    /// The readable memory regions of a loaded module, so a search can be confined to memory
    /// that is actually mapped.
    /// </summary>
    /// <remarks>
    /// <see cref="GcContractDescriptor"/> has to search a module's data for a header the runtime
    /// does not export. Scanning a fixed window around a known address would be simpler but would
    /// eventually read an unmapped page, and an access violation in a process reading its own
    /// internals is unrecoverable. So the scan is driven by the operating system's own view of
    /// what is mapped.
    /// </remarks>
    internal static class ProcessMemoryRegions
    {
        /// <summary>A committed, readable span of the module that contains <paramref name="anchor"/>.</summary>
        public readonly struct Region
        {
            public Region(IntPtr start, long length)
            {
                this.Start = start;
                this.Length = length;
            }

            public IntPtr Start { get; }

            public long Length { get; }
        }

        /// <summary>
        /// Every readable region belonging to the same module mapping as <paramref name="anchor"/>.
        /// </summary>
        public static IReadOnlyList<Region> OfModuleContaining(IntPtr anchor)
        {
            if (OperatingSystem.IsWindows())
                return WindowsRegions(anchor);

            if (OperatingSystem.IsLinux())
                return LinuxRegions(anchor);

            throw new ClrSpectorUnsupportedRuntimeException(
                "Locating the GC contract descriptor needs the process's memory map, which is " +
                "only implemented for Windows and Linux. The runtime contract descriptor is " +
                "exported and so is unaffected; only the GC contract is unavailable here.");
        }

        /// <summary>
        /// True when <paramref name="length"/> bytes can be read at <paramref name="address"/>.
        /// </summary>
        /// <remarks>
        /// A heap walk reaches MethodTable pointers taken from memory it is still establishing
        /// trust in. Dereferencing one that does not point at mapped memory is an access
        /// violation, and an access violation in a process reading its own internals cannot be
        /// caught or recovered - it takes the process down. So a pointer is checked against the
        /// operating system's map before it is followed. Callers cache the answer; this is far
        /// too slow to call per object.
        /// </remarks>
        public static bool IsReadable(IntPtr address, long length)
        {
            if (address == IntPtr.Zero || length <= 0)
                return false;

            if (OperatingSystem.IsWindows())
                return WindowsIsReadable(address, length);

            if (OperatingSystem.IsLinux())
                return LinuxIsReadable(address, length);

            // Nowhere else gets this far: locating the GC descriptor already failed.
            return true;
        }

        private static unsafe bool WindowsIsReadable(IntPtr address, long length)
        {
            if (VirtualQuery(address, out var info, (nuint)sizeof(MemoryBasicInformation)) == 0)
                return false;

            if (info.State != MemCommit
                || (info.Protect & PageGuard) != 0
                || (info.Protect & PageNoAccess) != 0
                || (info.Protect & ReadableProtections) == 0)
            {
                return false;
            }

            var regionEnd = info.BaseAddress.ToInt64() + (long)info.RegionSize;
            return address.ToInt64() + length <= regionEnd;
        }

        private static bool LinuxIsReadable(IntPtr address, long length)
        {
            var start = address.ToInt64();
            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                if (!TryParseMapsLine(line, out var from, out var to, out var readable, out _))
                    continue;

                if (start >= from && start < to)
                    return readable && start + length <= to;
            }

            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint Alignment1;
            public nuint RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint Alignment2;
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern nuint VirtualQuery(
            IntPtr address, out MemoryBasicInformation buffer, nuint length);

        private const uint MemCommit = 0x1000;
        private const uint PageNoAccess = 0x01;
        private const uint PageGuard = 0x100;

        /// <summary>Protections that permit a plain read.</summary>
        private const uint ReadableProtections = 0x02 // PAGE_READONLY
                                                 | 0x04 // PAGE_READWRITE
                                                 | 0x08 // PAGE_WRITECOPY
                                                 | 0x20 // PAGE_EXECUTE_READ
                                                 | 0x40 // PAGE_EXECUTE_READWRITE
                                                 | 0x80; // PAGE_EXECUTE_WRITECOPY

        private static unsafe IReadOnlyList<Region> WindowsRegions(IntPtr anchor)
        {
            var size = (nuint)sizeof(MemoryBasicInformation);

            if (VirtualQuery(anchor, out var anchorInfo, size) == 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"VirtualQuery failed for 0x{anchor.ToInt64():x} " +
                    $"(error {Marshal.GetLastWin32Error()}).");

            // Every section of a mapped image shares the image's allocation base, so this walks
            // the whole module and stops at the first region belonging to something else.
            var allocationBase = anchorInfo.AllocationBase;
            var regions = new List<Region>();
            var cursor = allocationBase;

            while (true)
            {
                if (VirtualQuery(cursor, out var info, size) == 0)
                    break;

                if (info.AllocationBase != allocationBase)
                    break;

                if (info.State == MemCommit
                    && (info.Protect & PageGuard) == 0
                    && (info.Protect & PageNoAccess) == 0
                    && (info.Protect & ReadableProtections) != 0)
                {
                    regions.Add(new Region(info.BaseAddress, (long)info.RegionSize));
                }

                var next = (long)info.BaseAddress + (long)info.RegionSize;
                if (next <= cursor.ToInt64())
                    break;

                cursor = new IntPtr(next);
            }

            return regions;
        }

        /// <summary>
        /// Reads /proc/self/maps and returns the readable mappings of the same file as the
        /// mapping that contains <paramref name="anchor"/>.
        /// </summary>
        private static IReadOnlyList<Region> LinuxRegions(IntPtr anchor)
        {
            var lines = File.ReadAllLines("/proc/self/maps");
            var target = anchor.ToInt64();

            string module = null;
            foreach (var line in lines)
            {
                if (TryParseMapsLine(line, out var start, out var end, out var readable, out var path)
                    && target >= start && target < end)
                {
                    module = path;
                    break;
                }
            }

            if (string.IsNullOrEmpty(module))
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"No /proc/self/maps entry covers 0x{target:x}, so the module holding the " +
                    $"contract descriptors could not be identified.");

            var regions = new List<Region>();
            foreach (var line in lines)
            {
                if (TryParseMapsLine(line, out var start, out var end, out var readable, out var path)
                    && readable
                    && path == module)
                {
                    regions.Add(new Region(new IntPtr(start), end - start));
                }
            }

            return regions;
        }

        /// <summary>
        /// Parses one /proc/self/maps line: "start-end perms offset dev inode path".
        /// </summary>
        private static bool TryParseMapsLine(
            string line, out long start, out long end, out bool readable, out string path)
        {
            start = 0;
            end = 0;
            readable = false;
            path = null;

            var fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
                return false;

            var dash = fields[0].IndexOf('-');
            if (dash <= 0)
                return false;

            if (!long.TryParse(fields[0].Substring(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out start)
                || !long.TryParse(fields[0].Substring(dash + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out end))
            {
                return false;
            }

            readable = fields[1].Length > 0 && fields[1][0] == 'r';
            path = fields.Length >= 6 ? fields[5] : string.Empty;
            return true;
        }
    }
}