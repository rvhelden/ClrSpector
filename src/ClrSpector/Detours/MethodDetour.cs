using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ClrSpector.Detours
{
    /// <summary>
    /// Redirects calls to one method so they run another, and puts things back when disposed.
    /// Lets a concrete method be stood in for under test without introducing an interface for
    /// the sake of mocking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method can be reached two different ways, and a redirect has to cover both:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Its precode.</b> Non-virtual and static calls go through a precode stub, which on x64
    /// is a rip-relative <c>jmp qword [rip+disp32]</c> through a single dispatch slot.
    /// </description></item>
    /// <item><description>
    /// <b>Its vtable slot.</b> A virtual call ignores the precode and reads the target straight
    /// out of the receiver's MethodTable vtable. Redirecting only the precode leaves virtual
    /// calls running the original - silently.
    /// </description></item>
    /// </list>
    /// <para>
    /// So both are patched wherever both exist, which also covers a virtual method invoked
    /// non-virtually (<c>base.M()</c>, or a call the JIT devirtualized). Each patch is a single
    /// pointer-sized store; the method's machine code is never modified.
    /// </para>
    /// <para><b>Limits worth knowing.</b></para>
    /// <list type="bullet">
    /// <item><description>
    /// A call the JIT has <b>inlined</b> cannot be intercepted, because no call happens at all.
    /// Mark methods you intend to redirect <see cref="MethodImplOptions.NoInlining"/>.
    /// </description></item>
    /// <item><description>
    /// <b>Tiered compilation</b> rewrites these same slots when it promotes a method to optimised
    /// code, which would silently drop the redirect. Disable tiering
    /// (<c>&lt;TieredCompilation&gt;false&lt;/TieredCompilation&gt;</c>) in test projects that
    /// rely on this.
    /// </description></item>
    /// <item><description>
    /// The replacement runs with the target's arguments exactly as passed. For an instance
    /// method that includes <c>this</c>, so prefer a <b>static</b> replacement whose first
    /// parameter is the target's declaring type.
    /// </description></item>
    /// <item><description>
    /// A vtable patch applies to the <b>declaring type</b>. Subclasses that inherit the slot
    /// unchanged are affected too, and because a subclass overriding nothing in a chunk shares
    /// its base's chunk, the reverse also holds: patching a base type's slot can be observed
    /// through a subclass. Overriding subclasses have their own slot and are unaffected.
    /// </description></item>
    /// <item><description>
    /// Process-wide and not thread-safe against concurrent calls to the target. Intended for
    /// tests, not production interception.
    /// </description></item>
    /// </list>
    /// </remarks>
    public sealed unsafe class MethodDetour : IDisposable
    {
        private readonly List<Patch> patches;

        private MethodDetour(MethodBase target, MethodBase replacement, List<Patch> patches)
        {
            this.Target = target;
            this.Replacement = replacement;
            this.patches = patches;
            this.IsActive = true;
        }

        public MethodBase Target { get; }

        public MethodBase Replacement { get; }

        /// <summary>False once the redirect has been undone.</summary>
        public bool IsActive { get; private set; }

        /// <summary>Which dispatch paths this redirect patched. Useful in diagnostics.</summary>
        public DetourTargets PatchedTargets { get; private set; }

        /// <summary>The target's precode, as decoded when the redirect was applied.</summary>
        public MethodPrecode Precode { get; private set; }

        /// <summary>
        /// The vtable slot that was patched, or <see cref="IntPtr.Zero"/> for a method that does
        /// not dispatch virtually.
        /// </summary>
        public IntPtr VtableSlot { get; private set; }

        /// <summary>
        /// Redirects <paramref name="target"/> to <paramref name="replacement"/> until the
        /// returned handle is disposed.
        /// </summary>
        public static MethodDetour Redirect(
            MethodBase target,
            MethodBase replacement,
            bool allowInterfaceDispatch = false)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));

            EnsureCompatible(target, replacement);

            if (!allowInterfaceDispatch && ImplementsInterfaceMethod(target))
                throw new MethodDetourException(
                    $"'{Describe(target)}' implements an interface method. A call made through an " +
                    "interface reference while redirected is cached by the runtime's interface " +
                    "dispatch and is NOT undone on dispose - the redirect leaks permanently and " +
                    "process-wide, reaching even instances created afterwards. Redirect the " +
                    "interface method itself, or pass allowInterfaceDispatch: true if you are sure " +
                    "the method is never called through an interface reference.");

            // Both must be jitted before their entry points mean anything.
            RuntimeHelpers.PrepareMethod(target.MethodHandle);
            RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

            var to = replacement.MethodHandle.GetFunctionPointer();
            var patches = new List<Patch>();
            var patched = DetourTargets.None;

            // Non-virtual and static calls go through the precode.
            var precode = MethodPrecode.Of(target);
            if (precode.HasDispatchSlot)
            {
                patches.Add(Patch.Apply(precode.DispatchSlot, to));
                patched |= DetourTargets.Precode;
            }

            // Virtual calls bypass the precode and read the vtable instead.
            var vtableSlot = target.IsVirtual ? MethodVtable.FindSlot(target) : IntPtr.Zero;
            if (vtableSlot != IntPtr.Zero)
            {
                patches.Add(Patch.Apply(vtableSlot, to));
                patched |= DetourTargets.Vtable;
            }

            // Refuse rather than half-redirect: a virtual method whose vtable slot we could not
            // find would keep running the original for every virtual call, with no error.
            if (target.IsVirtual && !patched.HasFlag(DetourTargets.Vtable))
            {
                Restore(patches);
                throw new MethodDetourException(
                    $"'{Describe(target)}' is virtual but its vtable slot could not be located, so " +
                    "virtual calls to it would silently keep running the original. Refusing the redirect.");
            }

            if (patched == DetourTargets.None)
                throw new MethodDetourException(
                    $"Found no dispatch slot to redirect for '{Describe(target)}'.");

            return new MethodDetour(target, replacement, patches)
            {
                PatchedTargets = patched,
                Precode = precode,
                VtableSlot = vtableSlot
            };
        }

        /// <summary>
        /// Redirects a method by name. Convenience for tests.
        /// </summary>
        public static MethodDetour Redirect(
            Type targetType,
            string targetMethod,
            Type replacementType,
            string replacementMethod,
            bool allowInterfaceDispatch = false)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static;

            var target = targetType.GetMethod(targetMethod, all)
                         ?? throw new MethodDetourException($"No method '{targetMethod}' on {targetType}.");
            var replacement = replacementType.GetMethod(replacementMethod, all)
                              ?? throw new MethodDetourException($"No method '{replacementMethod}' on {replacementType}.");

            return Redirect(target, replacement, allowInterfaceDispatch);
        }

        /// <summary>
        /// Whether this method is the implementation of some interface method on its declaring
        /// type - which makes it reachable through interface dispatch, whose caching a redirect
        /// cannot undo.
        /// </summary>
        private static bool ImplementsInterfaceMethod(MethodBase method)
        {
            var declaringType = method.DeclaringType;
            if (declaringType == null || declaringType.IsInterface || declaringType.ContainsGenericParameters)
                return false;

            foreach (var contract in declaringType.GetInterfaces())
            {
                InterfaceMapping mapping;
                try
                {
                    mapping = declaringType.GetInterfaceMap(contract);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (mapping.TargetMethods.Any(t => t != null
                                                   && t.MetadataToken == method.MetadataToken
                                                   && Equals(t.Module, method.Module)))
                {
                    return true;
                }
            }

            return false;
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

            if (target.IsAbstract)
                throw new MethodDetourException(
                    $"'{Describe(target)}' is abstract and has no implementation to redirect. " +
                    "Redirect the overriding method on a concrete type instead.");

            var targetParameters = EffectiveParameters(target).ToList();
            var replacementParameters = EffectiveParameters(replacement).ToList();

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

        private static void Restore(List<Patch> patches)
        {
            foreach (var patch in patches)
                patch.Undo();
        }

        /// <summary>Restores every patched slot. Safe to call more than once.</summary>
        public void Dispose()
        {
            if (!this.IsActive)
                return;

            Restore(this.patches);
            this.IsActive = false;
        }

        /// <summary>One redirected pointer slot and the value it held.</summary>
        private readonly struct Patch
        {
            private Patch(IntPtr address, IntPtr original)
            {
                this.Address = address;
                this.Original = original;
            }

            public IntPtr Address { get; }

            public IntPtr Original { get; }

            public static Patch Apply(IntPtr address, IntPtr value)
            {
                CodeProtection.MakeWritable(address, IntPtr.Size);

                var original = *(IntPtr*)address;
                *(IntPtr*)address = value;

                return new Patch(address, original);
            }

            public void Undo()
            {
                *(IntPtr*)this.Address = this.Original;
            }
        }
    }

    /// <summary>Which dispatch paths a redirect patched.</summary>
    [Flags]
    public enum DetourTargets
    {
        None = 0,

        /// <summary>The precode dispatch slot, used by non-virtual and static calls.</summary>
        Precode = 1,

        /// <summary>The MethodTable vtable slot, used by virtual calls.</summary>
        Vtable = 2
    }
}
