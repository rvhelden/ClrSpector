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

            var slot = SlotNumberOf(methodTable, method);
            if (slot < 0 || slot >= methodTable.NumberOfVirtuals)
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
            var token = (uint)method.MetadataToken;

            var match = methodTable.Methods.FirstOrDefault(m => m.MetadataToken == token);

            return match == null ? -1 : match.SlotNumber;
        }
    }
}
