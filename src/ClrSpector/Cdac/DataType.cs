using System.Collections.Generic;

namespace ClrSpector.Cdac
{
    /// <summary>
    /// A runtime structure's layout, as published by the contract descriptor.
    /// Offsets come from the running runtime; nothing here is hardcoded.
    /// </summary>
    public sealed class DataType
    {
        private readonly Dictionary<string, FieldLayout> fields;

        internal DataType(string name, uint? size, Dictionary<string, FieldLayout> fields)
        {
            this.Name = name;
            this.Size = size;
            this.fields = fields;
        }

        public string Name { get; }

        /// <summary>
        /// The structure's total size, when the descriptor published one (the "!" key).
        /// Absent for types whose size the runtime does not commit to.
        /// </summary>
        public uint? Size { get; }

        public IEnumerable<string> FieldNames => this.fields.Keys;

        /// <summary>
        /// The offset of <paramref name="field"/>, or a descriptive failure. A missing field
        /// means this runtime does not lay the structure out the way the caller assumes, so
        /// reading at a guessed offset would silently produce wrong data.
        /// </summary>
        public int this[string field]
        {
            get
            {
                if (this.fields.TryGetValue(field, out var layout))
                    return layout.Offset;

                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The contract descriptor describes '{this.Name}' but has no field '{field}'. " +
                    $"Known fields: {string.Join(", ", this.fields.Keys)}.");
            }
        }

        public bool TryGetField(string field, out FieldLayout layout)
        {
            return this.fields.TryGetValue(field, out layout);
        }

        public bool HasField(string field)
        {
            return this.fields.ContainsKey(field);
        }

        /// <summary>
        /// The structure's size, or a descriptive failure when the descriptor omitted it.
        /// Used where a size is needed to step over a structure.
        /// </summary>
        public uint RequiredSize
        {
            get
            {
                if (this.Size.HasValue)
                    return this.Size.Value;

                throw new ClrSpectorUnsupportedRuntimeException(
                    $"The contract descriptor does not publish a size for '{this.Name}'.");
            }
        }
    }
}
