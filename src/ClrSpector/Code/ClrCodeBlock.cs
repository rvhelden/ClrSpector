using System;
using System.Reflection;

namespace ClrSpector
{
    /// <summary>What kind of code occupies an address.</summary>
    public enum ClrCodeKind
    {
        /// <summary>Code the jit produced, and the only kind whose method can be named here.</summary>
        Jitted,

        /// <summary>
        /// One of the runtime's own stubs rather than a method body - a precode, a virtual
        /// dispatch stub, a delegate invoke stub and so on.
        /// </summary>
        Stub,

        /// <summary>Code compiled ahead of time and loaded from a module image.</summary>
        ReadyToRun,

        /// <summary>IL being interpreted rather than compiled.</summary>
        Interpreted,

        /// <summary>A managed code range whose kind this library does not decode.</summary>
        Unknown
    }

    /// <summary>
    /// One region of managed code, as found by looking up an address in
    /// <see cref="ClrCodeMap"/>.
    /// </summary>
    public sealed class ClrCodeBlock
    {
        private ClrCodeBlock(IntPtr address, IntPtr rangeSection, ClrCodeKind kind)
        {
            this.Address = address;
            this.RangeSection = rangeSection;
            this.Kind = kind;
        }

        /// <summary>The address that was looked up.</summary>
        public IntPtr Address { get; }

        /// <summary>The runtime's RangeSection covering this address.</summary>
        public IntPtr RangeSection { get; }

        public ClrCodeKind Kind { get; }

        /// <summary>
        /// The first byte of the method's code, or <see cref="IntPtr.Zero"/> when the kind does
        /// not have one.
        /// </summary>
        public IntPtr CodeStart { get; private set; }

        /// <summary>The code header the runtime keeps just behind the code.</summary>
        public IntPtr CodeHeader { get; private set; }

        /// <summary>
        /// The MethodDesc this code belongs to, or <see cref="IntPtr.Zero"/> for anything but
        /// jitted code.
        /// </summary>
        public IntPtr MethodDesc { get; private set; }

        /// <summary>
        /// How far into the method the looked-up address sits. Zero means it is the entry point.
        /// </summary>
        public long OffsetIntoMethod =>
            this.CodeStart == IntPtr.Zero ? 0 : this.Address.ToInt64() - this.CodeStart.ToInt64();

        /// <summary>
        /// The runtime's stub kind, when <see cref="Kind"/> is <see cref="ClrCodeKind.Stub"/>.
        /// The code header slot holds a small integer instead of a pointer for these.
        /// </summary>
        public int StubKind { get; private set; }

        internal static ClrCodeBlock Jitted(
            IntPtr address, IntPtr rangeSection, IntPtr codeStart, IntPtr codeHeader, IntPtr methodDesc)
        {
            return new ClrCodeBlock(address, rangeSection, ClrCodeKind.Jitted)
            {
                CodeStart = codeStart,
                CodeHeader = codeHeader,
                MethodDesc = methodDesc
            };
        }

        internal static ClrCodeBlock Stub(IntPtr address, IntPtr rangeSection, IntPtr codeStart, int stubKind)
        {
            return new ClrCodeBlock(address, rangeSection, ClrCodeKind.Stub)
            {
                CodeStart = codeStart,
                StubKind = stubKind
            };
        }

        internal static ClrCodeBlock OtherKind(IntPtr address, IntPtr rangeSection, ClrCodeKind kind)
        {
            return new ClrCodeBlock(address, rangeSection, kind);
        }

        /// <summary>
        /// The method this code belongs to, or null when there is no MethodDesc to resolve.
        /// </summary>
        /// <remarks>
        /// A MethodDesc address is what a <see cref="RuntimeMethodHandle"/> wraps, so reflection
        /// can turn it back into a method. Resolution can still fail - a MethodDesc for a dynamic
        /// method or a generic instantiation may not round-trip - so this returns null rather
        /// than throwing.
        /// </remarks>
        public MethodBase ResolveMethod()
        {
            if (this.MethodDesc == IntPtr.Zero)
                return null;

            try
            {
                return MethodBase.GetMethodFromHandle(
                    RuntimeMethodHandle.FromIntPtr(this.MethodDesc));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public override string ToString()
        {
            var where = $"0x{this.Address.ToInt64():x}";

            if (this.Kind != ClrCodeKind.Jitted)
                return this.Kind == ClrCodeKind.Stub
                    ? $"{where} {this.Kind} kind={this.StubKind} start=0x{this.CodeStart.ToInt64():x}"
                    : $"{where} {this.Kind}";

            var method = this.ResolveMethod();
            var name = method == null
                ? $"md=0x{this.MethodDesc.ToInt64():x}"
                : $"{method.DeclaringType?.Name}.{method.Name}";

            return $"{where} {name}+0x{this.OffsetIntoMethod:x} (start=0x{this.CodeStart.ToInt64():x})";
        }
    }
}
