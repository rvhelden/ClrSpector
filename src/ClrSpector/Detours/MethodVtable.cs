using System;
using System.Linq;
using System.Reflection;
using ClrSpector.Cdac;

namespace ClrSpector.Detours
{
    /// <summary>
    /// Locates the vtable slot a virtual method dispatches through.
    /// </summary>
    /// <remarks>
    /// A virtual call does not go through the method's precode - it reads the target straight out
    /// of the object's MethodTable vtable, which the runtime has already backpatched to the real
    /// code. That is why redirecting the precode alone leaves virtual calls untouched.
    ///
    /// The vtable is an array of chunk pointers beginning immediately after the MethodTable's
    /// fixed fields, with 8 slots per chunk:
    ///
    ///     chunkPointer = *(MethodTable + MethodTable.Size + (slot / 8) * pointerSize)
    ///     slotAddress  = chunkPointer + (slot % 8) * pointerSize
    ///
    /// Chunks are *not* necessarily contiguous, and a derived type that overrides nothing in a
    /// chunk shares its base type's chunk - which can sit at a lower address than the MethodTable
    /// itself. Verified: a subclass overriding only a late slot had chunk 0 at MethodTable-232
    /// (shared with the base) and chunk 1 at MethodTable+80, and treating the vtable as one
    /// contiguous array read the wrong address for the overridden slot.
    /// </remarks>
    public static class MethodVtable
    {
        /// <summary>
        /// Slots per vtable chunk. A CoreCLR compile-time constant, not published by the
        /// contract descriptor; verified against types with more than one chunk.
        /// </summary>
        private const int SlotsPerChunk = 8;

        /// <summary>Slots per vtable chunk, as verified against multi-chunk types.</summary>
        public static int SlotsPerVtableChunk => SlotsPerChunk;

        /// <summary>
        /// The vtable slot index the runtime assigned to <paramref name="method"/>, or -1 when
        /// it does not occupy one.
        /// </summary>
        public static int FindSlotNumber(MethodBase method)
        {
            var declaringType = method?.DeclaringType;
            if (declaringType == null || declaringType.IsInterface || declaringType.ContainsGenericParameters)
                return -1;

            var methodTable = ClrObject.From(declaringType).MethodTable;
            var slot = SlotNumberOf(methodTable, method);

            return slot < methodTable.NumberOfVirtuals ? slot : -1;
        }

        /// <summary>
        /// The address of <paramref name="method"/>'s vtable slot in its declaring type, or
        /// <see cref="IntPtr.Zero"/> when the method does not occupy one. Writing here redirects
        /// every virtual call that dispatches through this type.
        /// </summary>
        public static IntPtr FindSlot(MethodBase method)
        {
            var declaringType = method.DeclaringType;
            if (declaringType == null || declaringType.IsInterface || declaringType.ContainsGenericParameters)
                return IntPtr.Zero;

            var methodTable = ClrObject.From(declaringType).MethodTable;

            return FindSlot(methodTable, SlotNumberOf(methodTable, method));
        }

        /// <summary>
        /// The address of a method's vtable slot, taken from its MethodDesc.
        /// </summary>
        /// <remarks>
        /// Better than the reflection route as well as reflection-free: a MethodDesc records its
        /// own slot number, so there is no need to match metadata tokens across the type's
        /// methods to find it.
        /// </remarks>
        public static IntPtr FindSlot(ClrMethodDescription method)
        {
            if (method == null || method.MethodTablePointer == IntPtr.Zero)
                return IntPtr.Zero;

            var methodTable = ClrMethodTable.Create(new MemoryReader(method.MethodTablePointer));

            return FindSlot(methodTable, method.SlotNumber);
        }

        /// <summary>The address of one numbered virtual slot in a type's vtable.</summary>
        public static IntPtr FindSlot(ClrMethodTable methodTable, int slot)
        {
            if (methodTable == null || slot < 0 || slot >= methodTable.NumberOfVirtuals)
                return IntPtr.Zero;

            var headerSize = methodTable.Size;
            if (headerSize == 0)
                throw new ClrSpectorUnsupportedRuntimeException(
                    "The contract descriptor does not publish a MethodTable size, so the vtable " +
                    "cannot be located.");

            var chunkPointerAddress = methodTable.Address + (int)headerSize + (slot / SlotsPerChunk) * IntPtr.Size;
            var chunkPointer = new MemoryReader(chunkPointerAddress).ReadIntPtr(0);
            if (chunkPointer == IntPtr.Zero)
                return IntPtr.Zero;

            return chunkPointer + (slot % SlotsPerChunk) * IntPtr.Size;
        }

        /// <summary>
        /// The vtable slot the runtime assigned to this method, found by matching the decoded
        /// MethodDescs against the method's metadata token.
        /// </summary>
        private static int SlotNumberOf(ClrMethodTable methodTable, MethodBase method)
        {
            var match = methodTable.FindMethod(method);

            return match == null ? -1 : match.SlotNumber;
        }

        /// <summary>
        /// The address of a <b>non-virtual</b> slot - one the runtime assigned above
        /// <c>NumVirtuals</c> - or <see cref="IntPtr.Zero"/> when this method does not occupy one.
        /// </summary>
        /// <remarks>
        /// These do not live in the vtable chunks. They sit in an array that grows <i>backwards</i>
        /// from a point inside the MethodTable's auxiliary data:
        /// <code>
        /// slotAddress = AuxiliaryData + OffsetToNonVirtualSlots
        ///               - (1 + slot - NumVirtuals) * pointerSize
        /// </code>
        /// A non-virtual slot is only allocated for a method that needs a stable indirection
        /// without a vtable entry, so most methods have neither and this returns zero.
        /// </remarks>
        public static IntPtr FindNonVirtualSlot(MethodBase method)
        {
            var declaringType = method?.DeclaringType;
            if (declaringType == null || declaringType.IsInterface || declaringType.ContainsGenericParameters)
                return IntPtr.Zero;

            var methodTable = ClrObject.From(declaringType).MethodTable;

            var slot = SlotNumberOf(methodTable, method);
            if (slot < methodTable.NumberOfVirtuals || slot >= TotalSlotCount(methodTable))
                return IntPtr.Zero;

            if (methodTable.AuxiliaryData == IntPtr.Zero)
                return IntPtr.Zero;

            var layout = ContractDescriptor.Current.GetDataType("MethodTableAuxiliaryData");
            var offsetToNonVirtualSlots =
                new MemoryReader(methodTable.AuxiliaryData).ReadInt(layout["OffsetToNonVirtualSlots"]);

            var arrayEnd = methodTable.AuxiliaryData + offsetToNonVirtualSlots;

            return arrayEnd - (1 + slot - methodTable.NumberOfVirtuals) * IntPtr.Size;
        }

        /// <summary>
        /// Every slot the type has: its virtuals, plus the non-virtual slots its EEClass records.
        /// Only a canonical MethodTable owns non-virtual slots.
        /// </summary>
        public static int TotalSlotCount(ClrMethodTable methodTable)
        {
            var nonVirtual = methodTable.IsCanonicalMethodTable && methodTable.EEClass != null
                ? methodTable.EEClass.NumberOfNonVirtualSlots
                : 0;

            return methodTable.NumberOfVirtuals + nonVirtual;
        }
    }
}
