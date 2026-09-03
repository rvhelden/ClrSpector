using System;
using System.Runtime.InteropServices;

namespace ClrSpector.Detours
{
    /// <summary>
    /// The parts of the managed calling convention a redirect has to agree with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Arguments are passed in this order:
    /// </para>
    /// <code>
    /// [this] [return buffer] [generics context | varargs cookie] [user arguments]*
    /// </code>
    /// <para>
    /// The <b>return buffer</b> is the part that catches redirects out. A return value too large
    /// to travel in a register is written through a hidden pointer the caller passes in, and on
    /// x86/x64 that pointer is an ordinary argument sitting <i>after</i> <c>this</c>. So an
    /// instance method returning a <see cref="decimal"/> receives
    /// <c>(this, returnBuffer, ...)</c> while a static stand-in taking the instance as its first
    /// parameter receives <c>(returnBuffer, instance, ...)</c> - every argument shifted by one,
    /// the instance reinterpreted as a buffer, and the return value written over the object.
    /// Verified: doing this wrote the returned value into the target object's first field and
    /// killed the next GC.
    /// </para>
    /// <para>
    /// arm64 is the exception: it has a dedicated return-buffer register (<c>x8</c>) outside the
    /// argument sequence, so the buffer never shifts anything and the two shapes agree.
    /// </para>
    /// </remarks>
    internal static class ManagedAbi
    {
        /// <summary>
        /// Whether the hidden return-buffer pointer is an ordinary argument placed after
        /// <c>this</c>, so that adding or removing a leading instance argument moves it.
        /// False on arm64, where it has a register of its own.
        /// </summary>
        public static bool ReturnBufferFollowsThis =>
            RuntimeInformation.ProcessArchitecture != Architecture.Arm64;

        /// <summary>
        /// Whether a value of this type is returned through a hidden buffer rather than in a
        /// register.
        /// </summary>
        /// <remarks>
        /// Deliberately pessimistic: anything that is not obviously register-sized is treated as
        /// buffer-returned. Guessing "yes" only routes a pairing onto the thunk path, which is
        /// correct either way; guessing "no" would corrupt memory.
        /// </remarks>
        public static bool ReturnsViaHiddenBuffer(Type returnType)
        {
            if (returnType == null || returnType == typeof(void))
                return false;

            if (!returnType.IsValueType || returnType.IsPointer || returnType.IsByRef)
                return false;

            if (returnType.IsEnum || returnType.IsPrimitive)
                return false;

            return true;
        }
    }
}
