using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ClrSpector.Detours
{
    /// <summary>
    /// A generated adapter that sits in a target's dispatch slot and re-forms the call for a
    /// replacement whose frame does not match the target's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The thunk is emitted as IL and left to the JIT rather than written as machine code, so the
    /// argument shuffle is produced by the same compiler that produced both call frames: hidden
    /// return buffers, floating-point registers, spilling to the stack, x64 versus arm64 - none
    /// of it has to be reimplemented here, and none of it can be got subtly wrong.
    /// </para>
    /// <para>
    /// It is emitted as an <b>instance</b> method whenever the target is one, so the thunk's
    /// receiver occupies the same argument slot as the target's and everything after it -
    /// crucially the hidden return buffer, see <see cref="ManagedAbi"/> - keeps its place. That
    /// is also why <see cref="System.Reflection.Emit.DynamicMethod"/> cannot serve: it is always
    /// static.
    /// </para>
    /// <para>
    /// Reference-typed parameters are declared as <see cref="object"/> in the generated
    /// signature. A reference is a reference at this level, so it changes nothing about how the
    /// call is made, and it keeps the target's and the replacement's own types out of the
    /// generated metadata - so a proxy on an internal type needs no access to be granted.
    /// </para>
    /// </remarks>
    internal sealed class MethodThunk
    {
        private static readonly ConcurrentDictionary<(MethodBase, MethodBase, MethodPairing), MethodThunk> Cache =
            new ConcurrentDictionary<(MethodBase, MethodBase, MethodPairing), MethodThunk>();

        private static readonly Lazy<ModuleBuilder> Module = new Lazy<ModuleBuilder>(
            CreateModule, LazyThreadSafetyMode.ExecutionAndPublication);

        private static int emitted;

        private MethodThunk(MethodInfo method, IntPtr entryPoint, int receiverSlot)
        {
            this.Method = method;
            this.EntryPoint = entryPoint;
            this.ReceiverSlot = receiverSlot;
        }

        /// <summary>The generated method, for diagnostics.</summary>
        public MethodInfo Method { get; }

        /// <summary>The address to write into the target's dispatch slot.</summary>
        public IntPtr EntryPoint { get; }

        /// <summary>
        /// The <see cref="DetourThunkSupport"/> slot this thunk reads its proxy from, or -1 when
        /// it does not need one.
        /// </summary>
        public int ReceiverSlot { get; }

        /// <summary>
        /// The thunk for one target/replacement pairing, emitted once and reused. The proxy
        /// itself is not baked in - it is bound to <see cref="ReceiverSlot"/> per redirect.
        /// </summary>
        public static MethodThunk For(MethodBase target, MethodBase replacement, MethodPairing kind)
        {
            return Cache.GetOrAdd((target, replacement, kind), key => Emit(key.Item1, key.Item2, key.Item3));
        }

        private static ModuleBuilder CreateModule()
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
                throw new MethodDetourException(
                    "This runtime cannot emit code, so a redirect that needs an adapter thunk " +
                    "is not possible here. Use a static replacement whose first parameter stands " +
                    "in for the target's receiver.");

            return AssemblyBuilder
                .DefineDynamicAssembly(new AssemblyName("ClrSpector.Thunks"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("ClrSpector.Thunks");
        }

        private static MethodThunk Emit(MethodBase target, MethodBase replacement, MethodPairing kind)
        {
            var needsReceiver = kind == MethodPairing.ReceiverShift;

            var slot = needsReceiver
                ? DetourThunkSupport.Reserve(
                    $"'{MethodPairingAnalysis.Describe(target)}' redirected to " +
                    $"'{MethodPairingAnalysis.Describe(replacement)}'")
                : -1;

            var type = Module.Value.DefineType(
                $"Thunk_{Interlocked.Increment(ref emitted)}_{target.Name}",
                TypeAttributes.Public | TypeAttributes.Class);

            var returnType = Erase(MethodPairingAnalysis.ReturnTypeOf(target));
            var parameters = Erase(Types(target.GetParameters()));

            // An instance target gets an instance thunk, so the receiver stays in argument slot
            // zero and nothing behind it moves.
            var isInstance = !target.IsStatic;

            var method = type.DefineMethod(
                "Invoke",
                isInstance ? MethodAttributes.Public : MethodAttributes.Public | MethodAttributes.Static,
                isInstance ? CallingConventions.HasThis : CallingConventions.Standard,
                returnType,
                parameters);

            method.SetImplementationFlags(
                MethodImplAttributes.NoInlining | MethodImplAttributes.AggressiveOptimization);

            EmitBody(method.GetILGenerator(), target, replacement, kind, slot);

            var generated = type.CreateType().GetMethod("Invoke");

            RuntimeHelpers.PrepareMethod(generated.MethodHandle);

            return new MethodThunk(generated, generated.MethodHandle.GetFunctionPointer(), slot);
        }

        private static void EmitBody(
            ILGenerator il, MethodBase target, MethodBase replacement, MethodPairing kind, int slot)
        {
            var callee = EntryPointOf(replacement);
            var returnType = Erase(MethodPairingAnalysis.ReturnTypeOf(target));

            if (kind == MethodPairing.ReceiverShift)
            {
                // The proxy object comes first: it is the receiver of the call we are about to
                // make, exactly where a normal instance call would put it.
                var bound = il.DefineLabel();

                il.Emit(OpCodes.Ldc_I4, slot);
                il.Emit(OpCodes.Call, typeof(DetourThunkSupport).GetMethod(nameof(DetourThunkSupport.Get)));
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Brtrue_S, bound);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldc_I4, slot);
                il.Emit(OpCodes.Call, typeof(DetourThunkSupport).GetMethod(nameof(DetourThunkSupport.ThrowStale)));
                il.MarkLabel(bound);
            }

            // The target's own arguments, receiver included, passed straight through.
            var argumentCount = target.GetParameters().Length + (target.IsStatic ? 0 : 1);
            for (var i = 0; i < argumentCount; i++)
                il.Emit(OpCodes.Ldarg, i);

            il.Emit(OpCodes.Ldc_I8, callee.ToInt64());
            il.Emit(OpCodes.Conv_I);

            il.EmitCalli(
                OpCodes.Calli,
                kind == MethodPairing.ReceiverShift ? CallingConventions.HasThis : CallingConventions.Standard,
                returnType,
                Erase(Types(replacement.GetParameters())),
                null);

            il.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// The address to call. A method's entry point is its precode, which is stable for the
        /// life of the process even as tiered compilation replaces the code behind it.
        /// </summary>
        private static IntPtr EntryPointOf(MethodBase method)
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            return method.MethodHandle.GetFunctionPointer();
        }

        private static Type[] Types(ParameterInfo[] parameters)
        {
            var types = new Type[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                types[i] = parameters[i].ParameterType;

            return types;
        }

        /// <summary>
        /// Replaces reference types with <see cref="object"/>. Value types, byrefs and pointers
        /// are left alone, since their exact identity decides how they are passed.
        /// </summary>
        private static Type Erase(Type type)
        {
            if (type == null || type == typeof(void))
                return type;

            return type.IsValueType || type.IsByRef || type.IsPointer ? type : typeof(object);
        }

        private static Type[] Erase(Type[] types)
        {
            var erased = new Type[types.Length];
            for (var i = 0; i < types.Length; i++)
                erased[i] = Erase(types[i]);

            return erased;
        }
    }
}
