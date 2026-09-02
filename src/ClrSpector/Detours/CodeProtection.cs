using System;
using System.Runtime.InteropServices;

namespace ClrSpector.Detours
{
    /// <summary>
    /// Makes runtime-owned code pages writable so a dispatch slot can be updated.
    /// </summary>
    internal static class CodeProtection
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int mprotect(IntPtr addr, nuint len, int prot);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, nuint size, uint newProtect, out uint oldProtect);

        private const int PosixReadWriteExecute = 0x1 | 0x2 | 0x4;
        private const uint WindowsExecuteReadWrite = 0x40;

        /// <summary>
        /// Makes the pages spanning <paramref name="length"/> bytes at <paramref name="address"/>
        /// readable, writable and executable.
        /// </summary>
        public static void MakeWritable(IntPtr address, int length)
        {
            var pageSize = Environment.SystemPageSize;
            var start = new IntPtr(address.ToInt64() & ~((long)pageSize - 1));
            var span = (nuint)(address.ToInt64() - start.ToInt64() + length);

            if (OperatingSystem.IsWindows())
            {
                if (!VirtualProtect(start, span, WindowsExecuteReadWrite, out _))
                    throw new MethodDetourException(
                        $"VirtualProtect failed for 0x{start.ToInt64():x} (error {Marshal.GetLastWin32Error()}).");

                return;
            }

            if (mprotect(start, span, PosixReadWriteExecute) != 0)
                throw new MethodDetourException(
                    $"mprotect failed for 0x{start.ToInt64():x} (errno {Marshal.GetLastWin32Error()}).");
        }
    }
}
