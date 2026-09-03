using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// How many bytes a single MethodDesc occupies, which varies with its classification.
    /// </summary>
    /// <remarks>
    /// MethodDescs are not all the same size, and they are packed back-to-back inside a
    /// MethodDescChunk, so stepping from one to the next needs the size of the one just read.
    ///
    /// Up to .NET 10 the runtime published a <c>MethodDescSizeTable</c> global - a byte array
    /// indexed by the classification and size-affecting flags - and ClrSpector simply read it.
    /// .NET 11 removed that global. It is not a loss of information though: the table is only a
    /// precomputed form of "sizeof the concrete MethodDesc subclass, plus the optional trailing
    /// slots", and the descriptor publishes the size of every one of those types. So the table is
    /// reconstructed here from the descriptor rather than read out of the runtime, which works on
    /// both runtimes and keeps the "never hardcode a layout" property.
    ///
    /// A wrong size here desynchronises the whole chunk walk, so <see cref="ClrMethodTable"/>
    /// cross-checks every step against the MethodDesc's own ChunkIndex.
    /// </remarks>
    internal sealed class MethodDescSizes
    {
        /// <summary>MethodDesc.Flags bit 0x08 - a non-vtable slot is appended.</summary>
        private const ushort HasNonVtableSlotFlag = 0x0008;

        /// <summary>MethodDesc.Flags bit 0x10 - a MethodImpl structure is appended.</summary>
        private const ushort HasMethodImplFlag = 0x0010;

        /// <summary>MethodDesc.Flags bit 0x20 - a native code slot is appended.</summary>
        private const ushort HasNativeCodeSlotFlag = 0x0020;

        /// <summary>
        /// The descriptor type that describes each classification, indexed by the classification
        /// bits. The order is fixed by the runtime's own enum, so it is a positional mapping.
        /// </summary>
        private static readonly string[] TypeNamesByClassification =
        {
            "MethodDesc", // IL
            "FCallMethodDesc", // FCall
            "PInvokeMethodDesc", // PInvoke
            "EEImplMethodDesc", // EEImpl
            "ArrayMethodDesc", // Array
            "InstantiatedMethodDesc", // Instantiated
            "CLRToCOMCallMethodDesc", // ComInterop
            "DynamicMethodDesc" // Dynamic
        };

        private readonly uint?[] baseSizes;
        private readonly uint nonVtableSlotSize;
        private readonly uint methodImplSize;
        private readonly uint nativeCodeSlotSize;

        public MethodDescSizes(ContractDescriptor descriptor)
        {
            // A classification whose type this runtime does not describe stays null rather than
            // failing here: a build with COM interop compiled out never describes
            // CLRToCOMCallMethodDesc, and a process that contains no such method must still be
            // able to list the methods it does contain. The failure is raised in SizeOf, at the
            // point where such a MethodDesc is actually encountered.
            this.baseSizes = new uint?[TypeNamesByClassification.Length];
            for (var i = 0; i < TypeNamesByClassification.Length; i++)
            {
                if (descriptor.TryGetDataType(TypeNamesByClassification[i], out var type))
                    this.baseSizes[i] = type.Size;
            }

            this.nonVtableSlotSize = descriptor.GetDataType("NonVtableSlot").RequiredSize;
            this.methodImplSize = descriptor.GetDataType("MethodImpl").RequiredSize;
            this.nativeCodeSlotSize = descriptor.GetDataType("NativeCodeSlot").RequiredSize;
        }

        /// <summary>
        /// The byte size of a MethodDesc whose Flags field is <paramref name="flags"/>.
        /// </summary>
        public int SizeOf(ushort flags)
        {
            var classification = flags & ClrMethodDescription.ClassificationMask;
            var baseSize = this.baseSizes[classification];

            if (baseSize == null)
                throw new ClrSpectorUnsupportedRuntimeException(
                    $"A MethodDesc is classified as {(MethodClassification)classification}, but the " +
                    $"contract descriptor does not describe " +
                    $"'{TypeNamesByClassification[classification]}' (or gives it no size), so its " +
                    $"size cannot be determined and the chunk cannot be stepped through.");

            var size = baseSize.Value;

            if ((flags & HasNonVtableSlotFlag) != 0)
                size += this.nonVtableSlotSize;

            if ((flags & HasMethodImplFlag) != 0)
                size += this.methodImplSize;

            if ((flags & HasNativeCodeSlotFlag) != 0)
                size += this.nativeCodeSlotSize;

            return (int)size;
        }
    }
}