using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ClrSpector.Detours
{
    /// <summary>How a replacement stands in for its target, and therefore how it is wired up.</summary>
    public enum MethodPairing
    {
        /// <summary>
        /// The replacement can occupy the target's dispatch slot as it is: its arguments arrive
        /// exactly as the target's would. Patched straight in, with no thunk and no overhead.
        /// </summary>
        Direct,

        /// <summary>
        /// A static replacement standing in for an instance method whose return value travels in
        /// a hidden buffer. The two shapes disagree by one argument, so a generated instance
        /// thunk re-forms the call. See <see cref="ManagedAbi"/>.
        /// </summary>
        AbiShim,

        /// <summary>
        /// The replacement is an instance method whose parameters already match what the target
        /// receives - a proxy object, needing a receiver of its own. A generated thunk supplies
        /// it, since a dispatch slot holds a code address and nothing else.
        /// </summary>
        ReceiverShift
    }

    /// <summary>The outcome of classifying a target/replacement pairing.</summary>
    internal readonly struct MethodPairingResult
    {
        private MethodPairingResult(MethodPairing kind, bool isCompatible, string reason)
        {
            this.Kind = kind;
            this.IsCompatible = isCompatible;
            this.Reason = reason;
        }

        public MethodPairing Kind { get; }

        public bool IsCompatible { get; }

        /// <summary>Why the pairing was refused, or null when it was not.</summary>
        public string Reason { get; }

        public static MethodPairingResult Compatible(MethodPairing kind) =>
            new MethodPairingResult(kind, true, null);

        public static MethodPairingResult Refused(string reason) =>
            new MethodPairingResult(default, false, reason);
    }

    /// <summary>
    /// Works out whether a replacement can stand in for a target, and in which of the three
    /// shapes. Everything that would corrupt the stack, reinterpret <c>this</c>, or need a hidden
    /// argument nobody can supply is refused here, before a single slot is written.
    /// </summary>
    internal static class MethodPairingAnalysis
    {
        public static MethodPairingResult Classify(MethodBase target, MethodBase replacement, bool hasReceiver)
        {
            var refusal = RefuseUnsupportedShapes(target, replacement);
            if (refusal != null)
                return MethodPairingResult.Refused(refusal);

            var targetReturn = ReturnTypeOf(target);
            var replacementReturn = ReturnTypeOf(replacement);

            if (targetReturn != replacementReturn)
                return MethodPairingResult.Refused(
                    $"Return types differ: '{Describe(target)}' returns {targetReturn.Name} but " +
                    $"'{Describe(replacement)}' returns {replacementReturn.Name}.");

            var targetEffective = EffectiveParameters(target).ToList();
            var declared = replacement.GetParameters().Select(p => p.ParameterType).ToList();

            // A stand-in whose arguments already arrive in the target's shape.
            if (declared.SequenceEqual(targetEffective))
            {
                if (replacement.IsStatic)
                {
                    // ...except that a static stand-in for an instance method displaces the
                    // hidden return buffer. See ManagedAbi.
                    var needsShim = !target.IsStatic
                                    && ManagedAbi.ReturnBufferFollowsThis
                                    && ManagedAbi.ReturnsViaHiddenBuffer(targetReturn);

                    return MethodPairingResult.Compatible(
                        needsShim ? MethodPairing.AbiShim : MethodPairing.Direct);
                }

                // An instance method here is a proxy object: its own 'this' is the extra
                // argument, and it has to come from somewhere.
                if (!hasReceiver)
                    return MethodPairingResult.Refused(NeedsAReceiver(target, replacement, declared));

                return MethodPairingResult.Compatible(MethodPairing.ReceiverShift);
            }

            // An instance replacement on the target's own type: its 'this' is the target's, so
            // the frames already agree - including where the return buffer sits.
            if (!replacement.IsStatic && EffectiveParameters(replacement).SequenceEqual(targetEffective))
                return MethodPairingResult.Compatible(MethodPairing.Direct);

            return MethodPairingResult.Refused(
                $"Parameter lists differ. '{Describe(target)}' receives " +
                $"({Join(targetEffective)}) but '{Describe(replacement)}' takes " +
                $"({Join(declared)})" +
                (replacement.IsStatic ? string.Empty : " plus its own receiver") +
                ". For an instance target, a static replacement's first parameter stands in for " +
                "the target's receiver; an instance replacement's parameters should match the " +
                "target's without it.");
        }

        private static string NeedsAReceiver(MethodBase target, MethodBase replacement, List<Type> declared)
        {
            return $"'{Describe(replacement)}' is an instance method whose parameters " +
                   $"({Join(declared)}) already match what '{Describe(target)}' receives, so it " +
                   "stands in as a proxy object rather than as a plain function - and a dispatch " +
                   "slot holds a code address only, with no object for it to run on. Supply one:" +
                   Environment.NewLine +
                   $"    MethodDetour.Redirect(target, new {replacement.DeclaringType?.Name}(), replacement)" +
                   Environment.NewLine +
                   "    MethodDetour.Redirect(target, someDelegateOverTheProxyMethod)" +
                   Environment.NewLine +
                   "Or make the replacement static, in which case its first parameter stands in " +
                   "for the target's receiver.";
        }

        /// <summary>
        /// Shapes no redirect can honour, whichever pairing they would otherwise form.
        /// </summary>
        private static string RefuseUnsupportedShapes(MethodBase target, MethodBase replacement)
        {
            if (target.IsAbstract)
                return $"'{Describe(target)}' is abstract and has no implementation to redirect. " +
                       "Redirect the overriding method on a concrete type instead.";

            foreach (var method in new[] { target, replacement })
            {
                var role = ReferenceEquals(method, target) ? "target" : "replacement";

                if (method.DeclaringType == null)
                    return $"The {role} has no declaring type - a dynamic method cannot take part " +
                           "in a redirect, because it has no stable handle to read an entry point from.";

                if (method.IsGenericMethod || method.IsGenericMethodDefinition
                    || method.ContainsGenericParameters || method.DeclaringType.IsGenericType)
                    return $"'{Describe(method)}' is generic (the {role}). Shared generic code takes " +
                           "a hidden instantiation argument between the receiver and the user " +
                           "arguments, which a redirect cannot supply, and its code is shared " +
                           "across instantiations. Refusing.";

                if ((method.CallingConvention & CallingConventions.VarArgs) != 0)
                    return $"'{Describe(method)}' is varargs (the {role}), which passes a hidden " +
                           "cookie argument a redirect cannot reproduce.";

                if (method.DeclaringType.IsValueType)
                    return $"'{Describe(method)}' is declared on a value type (the {role}). A " +
                           "struct's receiver is a managed pointer, and its entry point is often " +
                           "an unboxing stub, so the two sides of a redirect would not agree. " +
                           "Refusing.";
            }

            return null;
        }

        /// <summary>
        /// The arguments a method actually receives. An instance method receives its declaring
        /// type ahead of its declared parameters - by reference when that type is a struct.
        /// </summary>
        public static IEnumerable<Type> EffectiveParameters(MethodBase method)
        {
            var parameters = new List<Type>();

            if (!method.IsStatic)
            {
                var declaringType = method.DeclaringType;
                parameters.Add(declaringType != null && declaringType.IsValueType
                    ? declaringType.MakeByRefType()
                    : declaringType);
            }

            parameters.AddRange(method.GetParameters().Select(p => p.ParameterType));

            return parameters;
        }

        public static Type ReturnTypeOf(MethodBase method)
        {
            return method is MethodInfo info ? info.ReturnType : typeof(void);
        }

        public static string Describe(MethodBase method)
        {
            return $"{method.DeclaringType?.Name}.{method.Name}";
        }

        private static string Join(IEnumerable<Type> types)
        {
            return string.Join(", ", types.Select(t => t.Name));
        }
    }
}
