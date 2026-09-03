using System;
using System.Linq;
using System.Threading.Tasks;
using ClrSpector;

namespace ClrSpectorTests
{
    /// <summary>
    /// Checks that an assembly's identity is recovered from metadata rather than reflection.
    /// </summary>
    /// <remarks>
    /// The runtime's Assembly structure stores no name - it holds a Module, and the name is a row
    /// in that module's metadata. Reflection is the oracle here precisely because it is the thing
    /// being avoided: if the two agree, the metadata route read the right row.
    /// </remarks>
    public class ClrAssemblyNameTests
    {
        [Test]
        [Arguments(typeof(object))]
        [Arguments(typeof(Uri))]
        [Arguments(typeof(Enumerable))]
        [Arguments(typeof(ClrAssemblyNameTests))]
        public async Task AssemblyNameAndVersionMatchReflection(Type type)
        {
            var assembly = ClrAssembly.Of(type);
            var expected = type.Assembly.GetName();

            await Assert.That(assembly).IsNotNull();
            await Assert.That(assembly.Name).IsEqualTo(expected.Name);
            await Assert.That(assembly.Version).IsEqualTo(expected.Version);

            // A neutral culture is recorded as an empty string, which reads better as null.
            await Assert.That(assembly.Culture).IsNull();
        }

        /// <summary>
        /// Every assembly on this runtime has exactly one module: multi-module assemblies are a
        /// .NET Framework feature .NET Core never carried forward.
        /// </summary>
        [Test]
        public async Task AnAssemblyHasOneModuleAndItIsTheManifestModule()
        {
            var assembly = ClrAssembly.Of(typeof(object));

            await Assert.That(assembly.Modules.Count).IsEqualTo(1);
            await Assert.That(assembly.ManifestModule).IsNotNull();
            await Assert.That(assembly.Modules[0].Address).IsEqualTo(assembly.ManifestModule.Address);
            await Assert.That(assembly.Modules[0].SimpleName).IsEqualTo("System.Private.CoreLib");
        }

        [Test]
        [Arguments(typeof(object))]
        [Arguments(typeof(Uri))]
        [Arguments(typeof(ClrAssemblyNameTests))]
        public async Task ModuleNamesMatchReflection(Type type)
        {
            var module = ClrModule.Of(type);
            var expected = type.Module.ScopeName;

            // Reflection reports the file name; the runtime's Module holds the simple name.
            await Assert.That(module.SimpleName)
                .IsEqualTo(System.IO.Path.GetFileNameWithoutExtension(expected));
        }

        /// <summary>
        /// The metadata each module hands back must be its own, and must describe a plausible
        /// number of types - a wrong table measurement would show up as nonsense here.
        /// </summary>
        [Test]
        public async Task EachModulesMetadataDescribesThatModule()
        {
            var coreLib = ClrModuleMetadata.Of(ClrModule.Of(typeof(object))).Image;
            var tests = ClrModuleMetadata.Of(ClrModule.Of(typeof(ClrAssemblyNameTests))).Image;

            await Assert.That(coreLib.RowCount(MetadataTable.TypeDef)).IsGreaterThan(1000);
            await Assert.That(coreLib.RowCount(MetadataTable.MethodDef)).IsGreaterThan(10000);

            await Assert.That(tests.RowCount(MetadataTable.TypeDef)).IsGreaterThan(0);
            await Assert.That(tests.RowCount(MetadataTable.TypeDef))
                .IsLessThan(coreLib.RowCount(MetadataTable.TypeDef));

            // CoreLib is large enough to force four-byte heap indexes, which is the case that a
            // small assembly never exercises - so the two must not be the same image.
            await Assert.That(coreLib.RowCount(MetadataTable.MethodDef))
                .IsNotEqualTo(tests.RowCount(MetadataTable.MethodDef));
        }
    }
}