using System;

namespace ClrSpector.Detours
{
    /// <summary>Thrown when a method cannot be redirected.</summary>
    public class MethodDetourException : Exception
    {
        public MethodDetourException(string message) : base(message)
        {
        }
    }
}
