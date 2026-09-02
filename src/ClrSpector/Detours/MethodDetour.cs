using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ClrSpector.Detours
{
    /// <summary>
    /// Redirects calls to one method so they run another, and puts things back when disposed.
    /// Lets a concrete, non-virtual method be stood in for under test without introducing an
    /// interface for the sake of mocking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A jitted method is reached through a <em>precode</em> stub, which on x64 is a
    /// rip-relative <c>jmp qword [rip+disp32]</c> through a single dispatch slot. Redirecting
    /// means writing the replacement's entry point into that slot: one pointer-sized store that
    /// is trivially reversible. The method's own machine code is never modified.
    /// </para>
    /// <para>
    /// Because every caller reaches the method through that slot, the redirect applies to direct
    /// calls, delegates and reflection alike - all three are verified by the tests.
    /// </para>
    /// <para><b>Limits worth knowing.</b></para>
    /// <list type="bullet">
    /// <item><description>
    /// A call the JIT has <b>inlined</b> cannot be intercepted, because no call happens at all.
    /// Mark methods you intend to redirect <see cref="MethodImplOptions.NoInlining"/>.
    /// </description></item>
    /// <item><description>
    /// <b>Tiered compilation</b> rewrites this same slot when it promotes a method to optimised
    /// code, which would silently drop the redirect. Disable tiering
    /// (<c>&lt;TieredCompilation&gt;false&lt;/TieredCompilation&gt;</c>) in test projects that
    /// rely on this.
    /// </description></item>
    /// <item><description>
    /// The replacement runs with the target's arguments exactly as passed. For an instance
    /// method that includes <c>this</c>, so prefer a <b>static</b> replacement whose first
    /// parameter is the target's declaring type; a differently-typed instance replacement would
    /// receive a <c>this</c> of the wrong type.
    /// </description></item>
    /// <item><description>
    /// Process-wide and not thread-safe against concurrent calls to the target. Intended for
    /// tests, not production interception.
    /// </description></item>
    /// </list>
    /// </remarks>
    public sealed unsafe class MethodDetour : IDisposable
    {
        /// <summary>Opcode bytes for <c>jmp qword [rip+disp32]</c>.</summary>
        private const byte JmpOpcode0 = 0xFF;
        private const byte JmpOpcode1 = 0x25;

        /// <summary>Length of the rip-relative jmp instruction.</summary>
        private const int JmpInstructionLength = 6;

        private readonly IntPtr* slot;
        private readonly IntPtr original;

        private MethodDetour(MethodBase target, MethodBase replacement, IntPtr* slot, IntPtr original)
        {
            this.Target = target;
            this.Replacement = replacement;
            this.slot = slot;
            this.original = original;
            this.IsActive = true;
        }

        public MethodBase Target { get; }

        public MethodBase Replacement { get; }

        /// <summary>False once the redirect has been undone.</summary>
        public bool IsActive { get; private set; }

        /// <summary>
        /// Redirects <paramref name="target"/> to <paramref name="replacement"/> until the
        /// returned handle is disposed.
        /// </summary>
        public static MethodDetour Redirect(MethodBase target, MethodBase replacement)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));

            EnsureCompatible(target, replacement);

            // Both must be jitted before their entry points mean anything.
            RuntimeHelpers.PrepareMethod(target.MethodHandle);
            RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

            var entryPoint = target.MethodHandle.GetFunctionPointer();
            var slot = FindDispatchSlot(entryPoint, target);
            var replacementEntryPoint = replacement.MethodHandle.GetFunctionPointer();

            CodeProtection.MakeWritable((IntPtr)slot, IntPtr.Size);

            var original = *slot;
            *slot = replacementEntryPoint;

            return new MethodDetour(target, replacement, slot, original);
        }

        /// <summary>
        /// Redirects an instance or static method by name. Convenience for tests.
        /// </summary>
        public static MethodDetour Redirect(Type targetType, string targetMethod, Type replacementType, string replacementMethod)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static;

            var target = targetType.GetMethod(targetMethod, all)
                         ?? throw new MethodDetourException($"No method '{targetMethod}' on {targetType}.");
            var replacement = replacementType.GetMethod(replacementMethod, all)
                              ?? throw new MethodDetourException($"No method '{replacementMethod}' on {replacementType}.");

            return Redirect(target, replacement);
        }

        /// <summary>
        /// Locates the dispatch slot behind a method's precode by decoding the rip-relative jump
        /// that the precode is made of.
        /// </summary>
        private static IntPtr* FindDispatchSlot(IntPtr entryPoint, MethodBase target)
        {
            var code = (byte*)entryPoint;

            if (code[0] != JmpOpcode0 || code[1] != JmpOpcode1)
                throw new MethodDetourException(
                    $"'{Describe(target)}' does not begin with a rip-relative jump " +
                    $"(found {code[0]:x2} {code[1]:x2} at 0x{entryPoint.ToInt64():x}), so it has no single " +
                    "dispatch slot to redirect. This method shape is not supported.");

            var displacement = *(int*)(code + 2);

            return (IntPtr*)(code + JmpInstructionLength + displacement);
        }

        /// <summary>
        /// Rejects pairings that would corrupt the stack or reinterpret <c>this</c>.
        /// </summary>
        private static void EnsureCompatible(MethodBase target, MethodBase replacement)
        {
            var targetReturn = ReturnTypeOf(target);
            var replacementReturn = ReturnTypeOf(replacement);

            if (targetReturn != replacementReturn)
                throw new MethodDetourException(
                    $"Return types differ: '{Describe(target)}' returns {targetReturn.Name} but " +
                    $"'{Describe(replacement)}' returns {replacementReturn.Name}.");

            var targetParameters = EffectiveParameters(target);
            var replacementParameters = EffectiveParameters(replacement);

            if (!targetParameters.SequenceEqual(replacementParameters))
                throw new MethodDetourException(
                    $"Parameter lists differ. '{Describe(target)}' takes " +
                    $"({string.Join(", ", targetParameters.Select(t => t.Name))}) but " +
                    $"'{Describe(replacement)}' takes " +
                    $"({string.Join(", ", replacementParameters.Select(t => t.Name))}). " +
                    "For an instance target, the replacement's first parameter stands in for 'this'.");
        }

        /// <summary>
        /// The parameters a method actually receives: an instance method receives its declaring
        /// type as a leading <c>this</c> argument.
        /// </summary>
        private static IEnumerable<Type> EffectiveParameters(MethodBase method)
        {
            var parameters = new List<Type>();

            if (!method.IsStatic)
                parameters.Add(method.DeclaringType);

            parameters.AddRange(method.GetParameters().Select(p => p.ParameterType));

            return parameters;
        }

        private static Type ReturnTypeOf(MethodBase method)
        {
            return method is MethodInfo info ? info.ReturnType : typeof(void);
        }

        private static string Describe(MethodBase method)
        {
            return $"{method.DeclaringType?.Name}.{method.Name}";
        }

        /// <summary>Restores the original dispatch target. Safe to call more than once.</summary>
        public void Dispose()
        {
            if (!this.IsActive)
                return;

            *this.slot = this.original;
            this.IsActive = false;
        }
    }
}
