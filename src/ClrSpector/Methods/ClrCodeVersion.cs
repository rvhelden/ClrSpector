using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// How thoroughly the jit compiled a method's code. Tiered compilation starts a method cheap
    /// and recompiles it once it proves hot, so this changes over a method's life.
    /// </summary>
    /// <remarks>
    /// The values are the runtime's own, not this library's numbering - including the
    /// <see cref="Unknown"/> sentinel, which is what a method whose tier has not been recorded
    /// reads as.
    /// </remarks>
    public enum ClrOptimizationTier : uint
    {
        /// <summary>Quick, unoptimised code with call counting attached.</summary>
        Tier0 = 0,

        /// <summary>Optimised code, promoted after the method proved hot.</summary>
        Tier1 = 1,

        /// <summary>Optimised code produced for an on-stack replacement of a loop.</summary>
        Tier1Osr = 2,

        /// <summary>Fully optimised without going through tiering.</summary>
        Optimized = 3,

        /// <summary>Tier 0 with extra instrumentation gathering profile data.</summary>
        Tier0Instrumented = 4,

        /// <summary>Tier 1 with extra instrumentation gathering profile data.</summary>
        Tier1Instrumented = 5,

        /// <summary>No tier recorded - the usual state until the runtime writes one.</summary>
        Unknown = 0xFFFFFFFF
    }

    /// <summary>
    /// One version of a method's native code. A method has several when tiered compilation has
    /// recompiled it, or when a profiler has rejitted it.
    /// </summary>
    public sealed class ClrNativeCodeVersion
    {
        /// <summary>The NativeCodeVersionNode this was read from.</summary>
        public IntPtr Address { get; internal set; }

        /// <summary>The MethodDesc this version belongs to.</summary>
        public IntPtr MethodDesc { get; internal set; }

        /// <summary>The compiled code itself.</summary>
        public IntPtr NativeCode { get; internal set; }

        public ClrOptimizationTier OptimizationTier { get; internal set; }

        /// <summary>Which IL version this native code was compiled from.</summary>
        public ulong IlVersionId { get; internal set; }

        public uint Flags { get; internal set; }

        public override string ToString()
        {
            return $"NativeCodeVersion @0x{this.Address.ToInt64():x} tier={this.OptimizationTier} " +
                   $"code=0x{this.NativeCode.ToInt64():x} ilVersion={this.IlVersionId}";
        }
    }

    /// <summary>
    /// A method's code-versioning state: which tier its current code is at, and every alternate
    /// native version the runtime is keeping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the other half of the tiering story. <see cref="ClrMethodDescription.IsEligibleForTieredCompilation"/>
    /// says whether the runtime <i>may</i> recompile a method;
    /// <see cref="ClrCodeVersionState.OptimizationTier"/> says what it has actually done so far.
    /// </para>
    /// <para>
    /// A method that has never been recompiled has no versioning state at all - the runtime only
    /// allocates one when a second version appears - so an absent state is the normal case rather
    /// than an error.
    /// </para>
    /// </remarks>
    public sealed unsafe class ClrCodeVersionState
    {
        private ClrCodeVersionState()
        {
        }

        /// <summary>The MethodDescCodeData this was read from, or zero when there is none.</summary>
        public IntPtr CodeData { get; private set; }

        /// <summary>
        /// The tier of the code the method currently runs, as the runtime records it on the
        /// method itself.
        /// </summary>
        public ClrOptimizationTier OptimizationTier { get; private set; }

        /// <summary>
        /// The method's temporary entry point - the precode that stands in before the real code
        /// exists.
        /// </summary>
        public IntPtr TemporaryEntryPoint { get; private set; }

        /// <summary>
        /// The MethodDescVersioningState, or zero when the runtime has not needed one.
        /// </summary>
        public IntPtr VersioningState { get; private set; }

        /// <summary>Raw versioning-state flags, when there is a versioning state.</summary>
        public byte VersioningFlags { get; private set; }

        /// <summary>
        /// Every native code version the runtime is keeping for this method, newest first.
        /// Empty when the method has only ever been compiled once.
        /// </summary>
        public IReadOnlyList<ClrNativeCodeVersion> NativeVersions { get; private set; }

        /// <summary>True when more than one native version exists - so it has been recompiled.</summary>
        public bool HasBeenRecompiled => this.NativeVersions.Count > 0;

        /// <summary>
        /// Reads the versioning state of a method, or null when its MethodDesc records no code
        /// data at all (a method never prepared for execution).
        /// </summary>
        public static ClrCodeVersionState Of(ClrMethodDescription method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));

            if (method.CodeData == IntPtr.Zero)
                return null;

            var descriptor = ContractDescriptor.Current;
            var codeDataLayout = descriptor.GetDataType("MethodDescCodeData");
            var reader = new MemoryReader(method.CodeData);

            var state = new ClrCodeVersionState
            {
                CodeData = method.CodeData,
                TemporaryEntryPoint = reader.ReadIntPtr(codeDataLayout["TemporaryEntryPoint"]),
                OptimizationTier = (ClrOptimizationTier)reader.ReadUInt(codeDataLayout["OptimizationTier"]),
                VersioningState = reader.ReadIntPtr(codeDataLayout["VersioningState"]),
                NativeVersions = new List<ClrNativeCodeVersion>()
            };

            if (state.VersioningState != IntPtr.Zero)
                state.ReadNativeVersions(descriptor);

            return state;
        }

        private void ReadNativeVersions(ContractDescriptor descriptor)
        {
            var stateLayout = descriptor.GetDataType("MethodDescVersioningState");
            var nodeLayout = descriptor.GetDataType("NativeCodeVersionNode");

            var stateReader = new MemoryReader(this.VersioningState);
            this.VersioningFlags = stateReader.ReadByte(stateLayout["Flags"]);

            var versions = new List<ClrNativeCodeVersion>();
            var node = stateReader.ReadIntPtr(stateLayout["NativeCodeVersionNode"]);

            // The nodes form a linked list, one per alternate compilation of this method.
            while (node != IntPtr.Zero)
            {
                var nodeReader = new MemoryReader(node);

                versions.Add(new ClrNativeCodeVersion
                {
                    Address = node,
                    MethodDesc = nodeReader.ReadIntPtr(nodeLayout["MethodDesc"]),
                    NativeCode = nodeReader.ReadIntPtr(nodeLayout["NativeCode"]),
                    OptimizationTier = (ClrOptimizationTier)nodeReader.ReadUInt(nodeLayout["OptimizationTier"]),
                    IlVersionId = (ulong)nodeReader.ReadIntPtr(nodeLayout["ILVersionId"]).ToInt64(),
                    Flags = nodeReader.ReadUInt(nodeLayout["Flags"])
                });

                node = nodeReader.ReadIntPtr(nodeLayout["Next"]);
            }

            this.NativeVersions = versions;
        }

        public override string ToString()
        {
            var recompiled = this.HasBeenRecompiled
                ? $" versions={this.NativeVersions.Count}"
                : string.Empty;

            return $"tier={this.OptimizationTier} tempEntry=0x{this.TemporaryEntryPoint.ToInt64():x}" +
                   recompiled;
        }
    }
}
