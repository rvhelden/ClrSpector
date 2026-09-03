using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ClrSpector.Detours
{
    /// <summary>
    /// The one dynamic module this library emits into, and the way it gets an entry point out
    /// again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Emitting into a <see cref="TypeBuilder"/> rather than a
    /// <see cref="System.Reflection.Emit.DynamicMethod"/> is deliberate on both counts that
    /// matter here. A DynamicMethod is always static, and a static stand-in for an instance
    /// method displaces the hidden return buffer (see <see cref="ManagedAbi"/>); and its code is
    /// collectible, so a dispatch slot could outlive the code it points at. A method built on a
    /// TypeBuilder can be an instance method, and lands in a non-collectible assembly.
    /// </para>
    /// <para>
    /// The assembly is never unloaded, so everything emitted here lives for the life of the
    /// process. Callers cache what they emit rather than emitting per use.
    /// </para>
    /// </remarks>
    internal static class EmittedCode
    {
        private static readonly Lazy<ModuleBuilder> module = new Lazy<ModuleBuilder>(
            CreateModule, LazyThreadSafetyMode.ExecutionAndPublication);

        private static int emitted;

        public static ModuleBuilder Module => module.Value;

        /// <summary>A type name no other emission will collide with.</summary>
        public static string UniqueName(string prefix, MethodBase forMethod)
        {
            return $"{prefix}_{Interlocked.Increment(ref emitted)}_{forMethod.Name}";
        }

        /// <summary>
        /// Jits a generated method and returns the address to put in a dispatch slot.
        /// </summary>
        /// <remarks>
        /// The address is the method's stable entry point - its precode - which does not move
        /// even when tiered compilation replaces the code behind it.
        /// </remarks>
        public static IntPtr EntryPointOf(MethodInfo method)
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            return method.MethodHandle.GetFunctionPointer();
        }

        /// <summary>
        /// Replaces reference types with <see cref="object"/>. Value types, byrefs and pointers
        /// are left alone, since their exact identity decides how they are passed.
        /// </summary>
        /// <remarks>
        /// A reference is a reference at the call boundary, so this changes nothing about how the
        /// call is made - and it keeps the target's own types out of the generated metadata, so
        /// emitting against an internal type needs no access to be granted.
        /// </remarks>
        public static Type Erase(Type type)
        {
            if (type == null || type == typeof(void))
                return type;

            return type.IsValueType || type.IsByRef || type.IsPointer ? type : typeof(object);
        }

        public static Type[] Erase(Type[] types)
        {
            var erased = new Type[types.Length];
            for (var i = 0; i < types.Length; i++)
                erased[i] = Erase(types[i]);

            return erased;
        }

        private static ModuleBuilder CreateModule()
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
                throw new MethodDetourException(
                    "This runtime cannot emit code, so anything that needs a generated method - " +
                    "an adapter thunk or a replacement method body - is not possible here.");

            return AssemblyBuilder
                .DefineDynamicAssembly(new AssemblyName("ClrSpector.Emitted"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("ClrSpector.Emitted");
        }
    }
}
