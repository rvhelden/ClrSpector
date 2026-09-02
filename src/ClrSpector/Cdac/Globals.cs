using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClrSpector.Cdac
{
    /// <summary>
    /// The contract descriptor's global values.
    /// </summary>
    /// <remarks>
    /// A global is published either as a literal (<c>["0x8", "uint64"]</c>) or as an indirect
    /// reference (<c>[[23], "pointer"]</c>) into the descriptor's pointer-data table.
    ///
    /// For an indirect global, <c>pointer_data[index]</c> is the <b>address of the runtime
    /// symbol</b>, not the value held in it. What to do next therefore depends on the symbol's
    /// native type, and getting this wrong yields plausible-looking garbage rather than a crash:
    ///
    ///   * a pointer variable (e.g. <c>g_pObjectClass</c>, a <c>MethodTable*</c>) needs one
    ///     dereference - use <see cref="Dereference"/>. Verified: dereferencing ObjectMethodTable,
    ///     StringMethodTable and ObjectArrayMethodTable yields exactly
    ///     <c>typeof(object|string|object[]).TypeHandle.Value</c>, while the undereferenced value
    ///     matches nothing.
    ///   * an array or table (e.g. the MethodDesc classification size table) is already at that
    ///     address - use <see cref="Address"/>.
    /// </remarks>
    public sealed unsafe class Globals
    {
        private readonly Dictionary<string, GlobalValue> values;
        private readonly nuint* pointerData;
        private readonly uint pointerDataCount;

        internal Globals(Dictionary<string, GlobalValue> values, nuint* pointerData, uint pointerDataCount)
        {
            this.values = values;
            this.pointerData = pointerData;
            this.pointerDataCount = pointerDataCount;
        }

        public IEnumerable<string> Names => this.values.Keys;

        public bool Has(string name) => this.values.ContainsKey(name);

        /// <summary>A literal numeric global, such as MethodDescAlignment.</summary>
        public ulong Number(string name)
        {
            var global = this.Get(name);
            if (global.IsIndirect)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Global '{name}' is an indirect (pointer-data) global; read it with Address or Dereference, not Number.");

            var text = global.Literal;
            try
            {
                return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? ulong.Parse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : ulong.Parse(text, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Global '{name}' has value '{text}', which is not a number.");
            }
        }

        /// <summary>A literal string global, such as Architecture.</summary>
        public string Text(string name)
        {
            var global = this.Get(name);
            if (global.IsIndirect)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Global '{name}' is an indirect (pointer-data) global and has no string value.");

            return global.Literal;
        }

        /// <summary>
        /// The address of the runtime symbol backing an indirect global. Use this for globals
        /// that name an array or table, where the symbol's address is already the data.
        /// </summary>
        public IntPtr Address(string name)
        {
            var global = this.Get(name);
            if (!global.IsIndirect)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Global '{name}' is a literal ('{global.Literal}'), not a pointer-data reference.");

            if (global.PointerDataIndex >= this.pointerDataCount)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Global '{name}' refers to pointer-data slot {global.PointerDataIndex}, " +
                    $"but the descriptor publishes only {this.pointerDataCount} slots.");

            return (IntPtr)(void*)this.pointerData[global.PointerDataIndex];
        }

        /// <summary>
        /// The value held in a pointer-typed runtime symbol: one dereference of
        /// <see cref="Address"/>. Use this for globals that name a pointer variable.
        /// </summary>
        public IntPtr Dereference(string name)
        {
            var address = this.Address(name);
            if (address == IntPtr.Zero)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"Global '{name}' has a null address in the descriptor's pointer data.");

            return *(IntPtr*)address;
        }

        private GlobalValue Get(string name)
        {
            if (this.values.TryGetValue(name, out var global))
                return global;

            throw new ClrSpectorUnsupportedRuntimeException(
                $"The contract descriptor has no global '{name}'.");
        }
    }

    internal readonly struct GlobalValue
    {
        private GlobalValue(string literal, uint pointerDataIndex, bool isIndirect)
        {
            this.Literal = literal;
            this.PointerDataIndex = pointerDataIndex;
            this.IsIndirect = isIndirect;
        }

        public string Literal { get; }
        public uint PointerDataIndex { get; }
        public bool IsIndirect { get; }

        public static GlobalValue FromLiteral(string literal) => new GlobalValue(literal, 0, false);
        public static GlobalValue FromPointerData(uint index) => new GlobalValue(null, index, true);
    }
}
