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
    /// The replacement runs with the target's arguments exactly as passed, so the two frames
    /// have to agree. Three shapes do - see <see cref="MethodPairing"/>. A <b>static</b>
    /// replacement whose first parameter is the target's declaring type is patched straight in;
    /// an <b>instance</b> replacement whose parameters already match what the target receives is
    /// a proxy object, and needs an instance passed alongside it. Anything else is refused.
    /// </description></item>
    /// <item><description>
    /// Two of those three shapes are wired up through a generated <b>thunk</b> rather than
    /// patched directly. One is the proxy case, which needs somewhere for the receiver to come
    /// from; the other is a static replacement for an instance method that returns a value too
    /// large for a register, because the hidden return buffer is passed <i>after</i> the
    /// receiver and so does not line up. The thunk is emitted as IL and compiled by the JIT, so
    /// the argument shuffle is never written by hand. See <see cref="ManagedAbi"/>.
    /// </description></item>
    /// <item><description>
    /// Generic methods, methods on generic types, varargs methods and methods on value types are
    /// refused: each needs a hidden argument, or has an entry point, that a redirect cannot
    /// honour.
    /// </description></item>
    /// <item><description>
    /// Thunk code is emitted into a dynamic assembly and is never reclaimed, one per distinct
    /// pairing. The proxy object itself is released on <see cref="Dispose"/>.
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

        /// <summary>Where the proxy is held for the thunk to find, or -1 when there is none.</summary>
        private int receiverSlot = -1;

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

        /// <summary>How the replacement stands in for the target.</summary>
        public MethodPairing Pairing { get; private set; }

        /// <summary>
        /// The proxy object the replacement runs on, or null when the replacement needs none.
        /// </summary>
        public object ReplacementReceiver { get; private set; }

        /// <summary>
        /// Whether the call goes through a generated adapter rather than reaching the
        /// replacement directly.
        /// </summary>
        public bool UsesThunk => this.ThunkEntryPoint != IntPtr.Zero;

        /// <summary>
        /// The entry point of the generated adapter, or <see cref="IntPtr.Zero"/> when the
        /// replacement was patched in directly.
        /// </summary>
        public IntPtr ThunkEntryPoint { get; private set; }

        /// <summary>The generated adapter, for diagnostics, or null when there is none.</summary>
        public MethodInfo Thunk { get; private set; }

        /// <summary>
        /// Redirects <paramref name="target"/> to <paramref name="replacement"/> until the
        /// returned handle is disposed.
        /// </summary>
        public static MethodDetour Redirect(
            MethodBase target,
            MethodBase replacement,
            bool allowInterfaceDispatch = false)
        {
            return Redirect(target, null, replacement, allowInterfaceDispatch);
        }

        /// <summary>
        /// Redirects <paramref name="target"/> so it runs <paramref name="replacement"/> on
        /// <paramref name="replacementReceiver"/> - a proxy object, free to carry state of its
        /// own - until the returned handle is disposed.
        /// </summary>
        /// <remarks>
        /// The replacement's parameters match what the target receives, its own receiver
        /// excluded: for an instance target that means the target's instance first, then the
        /// target's arguments.
        /// </remarks>
        public static MethodDetour Redirect(
            MethodBase target,
            object replacementReceiver,
            MethodBase replacement,
            bool allowInterfaceDispatch = false)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));

            if (replacementReceiver is Type)
                throw new MethodDetourException(
                    "The receiver is a Type. Pass the proxy object the replacement should run " +
                    "on, not its type - or use the overload taking two types for a static " +
                    "replacement.");

            replacement = ResolveAgainstReceiver(replacement, replacementReceiver);

            if (replacementReceiver != null
                && replacement.DeclaringType?.IsInstanceOfType(replacementReceiver) == false)
                throw new MethodDetourException(
                    $"The receiver is a {replacementReceiver.GetType().Name}, which does not " +
                    $"declare '{MethodPairingAnalysis.Describe(replacement)}'.");

            var pairing = MethodPairingAnalysis.Classify(target, replacement, replacementReceiver != null);
            if (!pairing.IsCompatible)
                throw new MethodDetourException(pairing.Reason);

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

            // A pairing whose frame does not match the target's is reached through a generated
            // adapter instead of directly. See MethodThunk.
            var thunk = pairing.Kind == MethodPairing.Direct
                ? null
                : MethodThunk.For(target, replacement, pairing.Kind);

            var to = thunk?.EntryPoint ?? replacement.MethodHandle.GetFunctionPointer();

            var detour = new MethodDetour(target, replacement, new List<Patch>())
            {
                Pairing = pairing.Kind,
                ReplacementReceiver = replacementReceiver,
                Thunk = thunk?.Method,
                ThunkEntryPoint = thunk?.EntryPoint ?? IntPtr.Zero,
                receiverSlot = thunk?.ReceiverSlot ?? -1
            };

            try
            {
                // The proxy has to be reachable before the first call can arrive, so bind it
                // ahead of any patch rather than after the last one.
                if (detour.receiverSlot >= 0)
                    DetourThunkSupport.Bind(detour.receiverSlot, detour, replacementReceiver);

                detour.Apply(to);
            }
            catch
            {
                detour.Dispose();
                throw;
            }

            return detour;
        }

        /// <summary>
        /// Points every dispatch path the target can be reached through at
        /// <paramref name="to"/>.
        /// </summary>
        private void Apply(IntPtr to)
        {
            var target = this.Target;
            var patched = DetourTargets.None;

            // Non-virtual and static calls go through the precode.
            var precode = MethodPrecode.Of(target);
            if (precode.HasDispatchSlot)
            {
                this.patches.Add(Patch.Apply(precode.DispatchSlot, to));
                patched |= DetourTargets.Precode;
            }

            // Virtual calls bypass the precode and read the vtable instead.
            var vtableSlot = target.IsVirtual ? MethodVtable.FindSlot(target) : IntPtr.Zero;
            if (vtableSlot != IntPtr.Zero)
            {
                this.patches.Add(Patch.Apply(vtableSlot, to));
                patched |= DetourTargets.Vtable;
            }

            this.PatchedTargets = patched;
            this.Precode = precode;
            this.VtableSlot = vtableSlot;

            // Refuse rather than half-redirect: a virtual method whose vtable slot we could not
            // find would keep running the original for every virtual call, with no error.
            if (target.IsVirtual && !patched.HasFlag(DetourTargets.Vtable))
                throw new MethodDetourException(
                    $"'{Describe(target)}' is virtual but its vtable slot could not be located, so " +
                    "virtual calls to it would silently keep running the original. Refusing the redirect.");

            if (patched == DetourTargets.None)
                throw new MethodDetourException(
                    $"Found no dispatch slot to redirect for '{Describe(target)}'.");
        }

        /// <summary>
        /// The implementation a virtual replacement resolves to on the proxy actually supplied,
        /// so a subclassed stand-in behaves the way a normal call on it would.
        /// </summary>
        private static MethodBase ResolveAgainstReceiver(MethodBase replacement, object receiver)
        {
            if (receiver == null
                || !(replacement is MethodInfo info)
                || info.IsStatic || !info.IsVirtual || info.IsFinal
                || receiver.GetType() == info.DeclaringType)
            {
                return replacement;
            }

            var baseDefinition = info.GetBaseDefinition();

            foreach (var candidate in receiver.GetType().GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (Equals(candidate.GetBaseDefinition(), baseDefinition))
                    return candidate;
            }

            return replacement;
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
                              ?? throw new MethodDetourException(
                                  $"No method '{replacementMethod}' on {replacementType}.");

            return Redirect(target, replacement, allowInterfaceDispatch);
        }

        /// <summary>
        /// Redirects a method by name so it runs on <paramref name="replacementReceiver"/>.
        /// Convenience for tests.
        /// </summary>
        public static MethodDetour Redirect(
            Type targetType,
            string targetMethod,
            object replacementReceiver,
            string replacementMethod,
            bool allowInterfaceDispatch = false)
        {
            if (replacementReceiver == null) throw new ArgumentNullException(nameof(replacementReceiver));

            var replacementType = replacementReceiver as Type ?? replacementReceiver.GetType();

            return Redirect(
                Find(targetType, targetMethod),
                replacementReceiver,
                Find(replacementType, replacementMethod),
                allowInterfaceDispatch);
        }

        /// <summary>
        /// Redirects <paramref name="target"/> to whatever <paramref name="replacement"/> points
        /// at. A delegate carries its receiver, so this covers a method group over a proxy
        /// object and a lambda over captured state alike.
        /// </summary>
        public static MethodDetour Redirect(
            MethodBase target,
            Delegate replacement,
            bool allowInterfaceDispatch = false)
        {
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));

            if (replacement.GetInvocationList().Length > 1)
                throw new MethodDetourException(
                    "The replacement is a multicast delegate. A dispatch slot holds one address, " +
                    "and only the last invocation's return value would survive, so combining " +
                    "stand-ins this way is refused.");

            if (replacement.Target != null && replacement.Method.IsStatic)
                throw new MethodDetourException(
                    "The replacement is a static method with a bound first argument. That " +
                    "argument occupies the parameter the target's receiver needs. Use an " +
                    "unbound method group, or an instance method on a proxy object.");

            return Redirect(target, replacement.Target, replacement.Method, allowInterfaceDispatch);
        }

        /// <summary>
        /// Redirects a method by name to whatever <paramref name="replacement"/> points at.
        /// Convenience for tests.
        /// </summary>
        public static MethodDetour Redirect(
            Type targetType,
            string targetMethod,
            Delegate replacement,
            bool allowInterfaceDispatch = false)
        {
            return Redirect(Find(targetType, targetMethod), replacement, allowInterfaceDispatch);
        }

        private static MethodBase Find(Type type, string name)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                                                         | BindingFlags.Instance | BindingFlags.Static;

            return type.GetMethod(name, all)
                   ?? throw new MethodDetourException($"No method '{name}' on {type}.");
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

        private static string Describe(MethodBase method)
        {
            return MethodPairingAnalysis.Describe(method);
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

            // Slots first, so no new call can reach the thunk, and only then let go of the
            // proxy - otherwise a call already on its way would find nothing to run on.
            Restore(this.patches);
            this.patches.Clear();

            DetourThunkSupport.Release(this.receiverSlot, this);

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