using System;
using System.Runtime.InteropServices;

namespace ClrSpector.Cdac
{
    internal static class ClrRuntimeInfo
    {
        /// <summary>
        /// A one-line description of the host, included in every failure message so that a
        /// layout mismatch report identifies the runtime it came from.
        /// </summary>
        public static string Describe()
        {
            return RuntimeInformation.FrameworkDescription
                   + " / " + RuntimeInformation.RuntimeIdentifier
                   + " / pointer size " + IntPtr.Size;
        }
    }
}
