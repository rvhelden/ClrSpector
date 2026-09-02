using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using ClrSpector;
using ClrSpector.Cdac;

namespace ClrSpectorConsole
{
    [StructLayout(LayoutKind.Sequential)]
    public struct TestStruct
    {
        public readonly byte Test1;
        public readonly byte Test2;
        public readonly byte Test3;
        public readonly byte Test4;
    }

    internal static class Program
    {
        private static void Main()
        {
            PrintRuntime();

            foreach (var type in new[]
                     {
                         typeof(SampleClass),
                         typeof(TestStruct),
                         typeof(string),
                         typeof(int[]),
                         typeof(List<int>),
                         typeof(List<string>)
                     })
            {
                Dump(type);
            }
        }

        private static void PrintRuntime()
        {
            var descriptor = ClrObject.Descriptor;

            Console.WriteLine($"Runtime      : {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})");
            Console.WriteLine($"CoreLib      : {typeof(object).Assembly.Location}");
            Console.WriteLine($"Descriptor   : version {descriptor.Version}, baseline '{descriptor.Baseline}', " +
                              $"{descriptor.TypeNames.Count()} types, {descriptor.Globals.Names.Count()} globals");
            Console.WriteLine($"Architecture : {descriptor.Globals.Text("Architecture")}");
        }

        /// <summary>
        /// Prints the decoded runtime view of a type next to the reflection view of the same
        /// type, so a decoding mistake shows up as a visible disagreement.
        /// </summary>
        private static void Dump(Type type)
        {
            Console.WriteLine();
            Console.WriteLine(new string('-', 78));
            Console.WriteLine(type.FullName);

            var methodTable = ClrObject.From(type).MethodTable;
            var eeClass = methodTable.EEClass;

            Console.WriteLine($"  MethodTable   0x{methodTable.Address.ToInt64():x}");
            Console.WriteLine($"  baseSize      {methodTable.BaseSize}");
            Console.WriteLine($"  virtuals      {methodTable.NumberOfVirtuals}");
            Console.WriteLine($"  interfaces    {methodTable.NumberOfInterfaces}");
            Console.WriteLine($"  category      class={methodTable.IsClass} valueType={methodTable.IsValueType} " +
                              $"interface={methodTable.IsInterface} array={methodTable.IsArray}");

            if (methodTable.HasComponentSize)
                Console.WriteLine($"  componentSize {methodTable.ComponentSize}");

            Console.WriteLine($"  union         {methodTable.UnionKind} (canonical={methodTable.IsCanonicalMethodTable})");
            Console.WriteLine($"  parent        0x{methodTable.ParentMethodTablePointer.ToInt64():x}" +
                              $"{(type.BaseType != null ? $"  [{type.BaseType.Name}]" : "  [none]")}");

            if (eeClass != null)
            {
                Console.WriteLine($"  EEClass       0x{eeClass.Address.ToInt64():x}");
                Console.WriteLine($"    normType    {eeClass.NormType}");
                Console.WriteLine($"    fields      instance={eeClass.NumberOfInstanceFields} " +
                                  $"static={eeClass.NumberOfStaticFields} " +
                                  $"threadStatic={eeClass.NumberOfThreadStaticFields}");
                Console.WriteLine($"    methods     {eeClass.NumberOfMethods}   (EEClass.NumMethods; not the same tally as declared methods)");
            }

            PrintMethods(type, methodTable);
        }

        /// <summary>
        /// Lists each decoded MethodDesc with the name, arguments and generic type arguments
        /// recovered by resolving its reconstructed metadata token.
        /// </summary>
        private static void PrintMethods(Type type, ClrMethodTable methodTable)
        {
            Console.WriteLine($"  methods       {methodTable.Methods.Count} decoded from the MethodDescChunk list:");

            var module = type.IsConstructedGenericType
                ? type.GetGenericTypeDefinition().Module
                : type.Module;

            foreach (var method in methodTable.Methods)
            {
                MethodBase resolved;
                try
                {
                    resolved = module.ResolveMethod((int)method.MetadataToken);
                }
                catch (ArgumentException)
                {
                    Console.WriteLine($"    slot {method.SlotNumber,-5} 0x{method.MetadataToken:x8}  <token did not resolve>");
                    continue;
                }

                var generics = GenericArguments(resolved);
                var arguments = string.Join(", ", resolved.GetParameters()
                    .Select(p => $"{FriendlyName(p.ParameterType)} {p.Name}"));

                Console.WriteLine($"    slot {method.SlotNumber,-5} 0x{method.MetadataToken:x8}  " +
                                  $"{FriendlyName(resolved.DeclaringType)}.{resolved.Name}{generics}({arguments})" +
                                  $"   [{method.Classification}]");
            }
        }

        private static string GenericArguments(MethodBase method)
        {
            if (!method.IsGenericMethod && !method.IsGenericMethodDefinition)
                return string.Empty;

            return "<" + string.Join(", ", method.GetGenericArguments().Select(FriendlyName)) + ">";
        }

        private static string FriendlyName(Type type)
        {
            if (type == null)
                return "?";

            if (type.IsArray)
                return FriendlyName(type.GetElementType()) + "[]";

            if (!type.IsGenericType)
                return type.Name;

            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
                name = name.Substring(0, tick);

            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyName)) + ">";
        }
    }
}
