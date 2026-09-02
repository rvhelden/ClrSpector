using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ClrSpector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    /// <summary>
    /// Prints every method ClrSpector decodes from a type's runtime structures, with its
    /// arguments and generic type arguments, and checks the listing against reflection.
    /// </summary>
    /// <remarks>
    /// Names and signatures are not stored on a MethodDesc; only a metadata token is
    /// recoverable. Resolving that token through the declaring module is what turns a decoded
    /// MethodDesc back into a readable signature - and because the token is reassembled from
    /// two separate runtime fields, a listing that matches reflection is strong evidence the
    /// decode is correct rather than merely plausible.
    /// </remarks>
    public class MethodListingTests
    {
        [Test]
        public async Task PrintsMethodsOfSampleClass()
        {
            await PrintAndVerify(typeof(SampleClass));
        }

        [Test]
        public async Task PrintsMethodsOfGenericSampleClass()
        {
            await PrintAndVerify(typeof(GenericSampleClass<string>));
        }

        [Test]
        public async Task PrintsMethodsOfClosedGenericFrameworkType()
        {
            await PrintAndVerify(typeof(List<int>));
        }

        /// <summary>
        /// Writes to the test's own output so the listing shows up in test results, falling
        /// back to the console when there is no ambient test.
        /// </summary>
        private static void Write(string line)
        {
            var writer = TestContext.Current?.OutputWriter;
            if (writer != null)
                writer.WriteLine(line);
            else
                Console.WriteLine(line);
        }

        private static async Task PrintAndVerify(Type type)
        {
            var methodTable = ClrObject.From(type).MethodTable;
            var module = ResolvingModule(type);

            Write(string.Empty);
            Write($"{type.FullName}");
            Write($"  MethodTable 0x{methodTable.Address.ToInt64():x}  " +
                              $"canonical={methodTable.IsCanonicalMethodTable}  " +
                              $"virtuals={methodTable.NumberOfVirtuals}  " +
                              $"interfaces={methodTable.NumberOfInterfaces}");
            Write($"  {methodTable.Methods.Count} method(s) decoded from the MethodDescChunk list:");

            var decoded = new List<string>();

            foreach (var method in methodTable.Methods)
            {
                var resolved = Resolve(module, method.MetadataToken);

                if (resolved == null)
                {
                    Write($"    slot {method.SlotNumber,-4} token 0x{method.MetadataToken:x8}  <unresolved>");
                    continue;
                }

                var genericArguments = GenericArgumentsOf(resolved);
                var arguments = string.Join(", ", resolved.GetParameters()
                    .Select(p => $"{FriendlyName(p.ParameterType)} {p.Name}"));

                Write(
                    $"    slot {method.SlotNumber,-4} token 0x{method.MetadataToken:x8}  " +
                    $"{resolved.Name}{genericArguments}({arguments})" +
                    $"    [{method.Classification}]");

                decoded.Add(resolved.Name);
            }

            // Reflection lists the same members for the type that owns them.
            var owner = methodTable.IsCanonicalMethodTable ? type : type.GetGenericTypeDefinition();
            var expected = DeclaredMethodNames(owner);

            Write($"  reflection declares {expected.Count}: {string.Join(", ", expected.OrderBy(n => n))}");

            await Assert.That(decoded.OrderBy(n => n).ToList())
                .IsEquivalentTo(expected.OrderBy(n => n).ToList());
        }

        /// <summary>
        /// Method tokens are scoped to the module of the type that declares them, which for a
        /// constructed generic type is its generic type definition.
        /// </summary>
        private static Module ResolvingModule(Type type)
        {
            return type.IsConstructedGenericType
                ? type.GetGenericTypeDefinition().Module
                : type.Module;
        }

        private static MethodBase Resolve(Module module, uint token)
        {
            try
            {
                return module.ResolveMethod((int)token);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static List<string> DeclaredMethodNames(Type type)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static
                                     | BindingFlags.DeclaredOnly;

            return type.GetMethods(all).Cast<MethodBase>()
                .Concat(type.GetConstructors(all))
                .Select(m => m.Name)
                .ToList();
        }

        private static string GenericArgumentsOf(MethodBase method)
        {
            if (!method.IsGenericMethodDefinition && !method.IsGenericMethod)
                return string.Empty;

            return "<" + string.Join(", ", method.GetGenericArguments().Select(FriendlyName)) + ">";
        }

        private static string FriendlyName(Type type)
        {
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
