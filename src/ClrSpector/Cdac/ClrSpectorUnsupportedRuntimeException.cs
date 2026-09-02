using System;

namespace ClrSpector.Cdac
{
    /// <summary>
    /// Thrown when the hosting runtime does not describe itself in a way ClrSpector can read.
    /// ClrSpector never guesses a layout: if the contract descriptor is missing, malformed, or
    /// does not describe a structure we need, we fail here rather than read arbitrary memory.
    /// </summary>
    public class ClrSpectorUnsupportedRuntimeException : Exception
    {
        public ClrSpectorUnsupportedRuntimeException(string message)
            : base(message + Environment.NewLine + "Runtime: " + ClrRuntimeInfo.Describe())
        {
        }
    }
}
