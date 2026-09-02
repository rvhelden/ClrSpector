using System.Collections.Generic;

namespace ClrSpectorTests
{
    /// <summary>
    /// A type with generic methods, so method decoding is exercised against generic parameters
    /// and generic type arguments as well as plain ones.
    /// </summary>
    public class GenericSampleClass<TItem>
    {
        public TItem Stored { get; set; }

        public TItem Echo(TItem value) => value;

        public TResult Convert<TResult>(TItem value, TResult fallback) => fallback;

        public void AddRange<TOther>(List<TItem> items, TOther marker, int count)
        {
        }

        public static int Count(IEnumerable<TItem> items) => 0;
    }
}
