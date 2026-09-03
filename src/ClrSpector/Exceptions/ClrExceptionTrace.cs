using System;
using System.Collections.Generic;
using System.Reflection;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// One frame of the stack trace an exception captured, as the runtime stored it.
    /// </summary>
    public sealed class ClrExceptionFrame
    {
        /// <summary>The instruction pointer recorded for this frame.</summary>
        public IntPtr InstructionPointer { get; internal set; }

        /// <summary>The MethodDesc of the method the frame was in.</summary>
        public IntPtr MethodDesc { get; internal set; }

        /// <summary>The runtime's raw flags for this frame.</summary>
        public int Flags { get; internal set; }

        /// <summary>
        /// True for the last frame carried over from a rethrow's original trace, which is where
        /// "End of stack trace from previous location" comes from.
        /// </summary>
        public bool IsLastFrameFromForeignStackTrace { get; internal set; }

        /// <summary>The method this frame was in, or null when the MethodDesc will not resolve.</summary>
        public MethodBase ResolveMethod()
        {
            if (this.MethodDesc == IntPtr.Zero)
                return null;

            try
            {
                return MethodBase.GetMethodFromHandle(RuntimeMethodHandle.FromIntPtr(this.MethodDesc));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public override string ToString()
        {
            var method = this.ResolveMethod();
            var name = method == null
                ? $"md=0x{this.MethodDesc.ToInt64():x}"
                : $"{method.DeclaringType?.FullName}.{method.Name}";

            return $"at {name}  ip=0x{this.InstructionPointer.ToInt64():x}";
        }
    }

    /// <summary>
    /// Reads the stack trace an exception object is carrying, straight out of the heap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Exception.StackTrace"/> gives you a formatted string, built once and only if
    /// the runtime feels like it. The underlying data is an array of
    /// <c>(instruction pointer, MethodDesc)</c> pairs on the exception object, and that is what
    /// this reads - so each frame comes back as something you can resolve to a
    /// <see cref="MethodBase"/> or look up in <see cref="ClrCodeMap"/>, rather than as text to
    /// parse.
    /// </para>
    /// <para>
    /// It also works on an exception that was never thrown far enough to have its string built,
    /// and on one caught and stashed long ago.
    /// </para>
    /// </remarks>
    public static unsafe class ClrExceptionTrace
    {
        /// <summary>The frame carried over from an earlier trace after a rethrow.</summary>
        private const int LastFrameFromForeignStackTraceFlag = 0x0001;

        /// <summary>The recorded IP has already been adjusted to point at the call.</summary>
        private const int IpAdjustedFlag = 0x0002;

        /// <summary>The frame resumed an async continuation rather than being called normally.</summary>
        private const int ContinuationFlag = 0x0008;

        /// <summary>
        /// The frames <paramref name="exception"/> captured, outermost first, or an empty list
        /// when it captured none.
        /// </summary>
        public static IReadOnlyList<ClrExceptionFrame> Of(Exception exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));

            var frames = new List<ClrExceptionFrame>();

            var descriptor = ContractDescriptor.Current;
            var exceptionLayout = descriptor.GetDataType("Exception");

            var address = ClrHeapObject.AddressOf(exception);
            var stackTraceObject = new MemoryReader(address).ReadIntPtr(exceptionLayout["_stackTrace"]);

            if (stackTraceObject == IntPtr.Zero)
                return frames;

            // _stackTrace holds a byte array; its elements start after the array header.
            var arrayHeaderSize = (int)descriptor.GetDataType("Array").RequiredSize;
            var payload = stackTraceObject + arrayHeaderSize;

            var headerLayout = descriptor.GetDataType("StackTraceArrayHeader");
            var count = new MemoryReader(payload).ReadUInt(headerLayout["Size"]);

            if (count == 0)
                return frames;

            var elementLayout = descriptor.GetDataType("StackTraceElement");
            var elementSize = (int)elementLayout.RequiredSize;

            var cursor = payload + (int)headerLayout.RequiredSize;

            for (var i = 0u; i < count; i++)
            {
                var reader = new MemoryReader(cursor);

                var ip = reader.ReadIntPtr(elementLayout["Ip"]);
                var flags = reader.ReadInt(elementLayout["Flags"]);

                // On x64 the first frame's IP is the return address, which points just past the
                // call. Backing up a byte keeps it inside the calling instruction, so a lookup
                // lands in the right method even when the call is the last instruction.
                if (i == 0
                    && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                        == System.Runtime.InteropServices.Architecture.X64
                    && (flags & (IpAdjustedFlag | ContinuationFlag)) == 0)
                {
                    ip -= 1;
                }

                frames.Add(new ClrExceptionFrame
                {
                    InstructionPointer = ip,
                    MethodDesc = reader.ReadIntPtr(elementLayout["MethodDesc"]),
                    Flags = flags,
                    IsLastFrameFromForeignStackTrace = (flags & LastFrameFromForeignStackTraceFlag) != 0
                });

                cursor += elementSize;
            }

            return frames;
        }

        /// <summary>
        /// The captured trace rendered as text, one frame per line. Built from the runtime's own
        /// frame array rather than from <see cref="Exception.StackTrace"/>.
        /// </summary>
        public static string Dump(Exception exception)
        {
            var frames = Of(exception);

            if (frames.Count == 0)
                return $"{exception.GetType().FullName}: {exception.Message}  <no captured frames>";

            var text = new System.Text.StringBuilder();
            text.AppendLine($"{exception.GetType().FullName}: {exception.Message}");

            foreach (var frame in frames)
            {
                text.AppendLine($"   {frame}");

                if (frame.IsLastFrameFromForeignStackTrace)
                    text.AppendLine("   --- end of stack trace from a previous throw ---");
            }

            return text.ToString().TrimEnd();
        }
    }
}
