using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// The runtime's internal view of a managed type, read from the live runtime.
    /// </summary>
    public class ClrObject
    {
        public ClrMethodTable MethodTable { get; private set; }

        /// <summary>
        /// Decodes the runtime structures behind <typeparamref name="T"/>.
        /// </summary>
        public static ClrObject From<T>()
        {
            return From(typeof(T));
        }

        /// <summary>
        /// Decodes the runtime structures behind <paramref name="type"/>.
        /// </summary>
        public static ClrObject From(System.Type type)
        {
            // A type handle is the address of the type's MethodTable.
            var reader = new MemoryReader(type.TypeHandle.Value);

            return new ClrObject
            {
                MethodTable = ClrMethodTable.Create(reader)
            };
        }

        /// <summary>
        /// The descriptor this process is being decoded with. Exposed for diagnostics.
        /// </summary>
        public static ContractDescriptor Descriptor => ContractDescriptor.Current;
    }
}
