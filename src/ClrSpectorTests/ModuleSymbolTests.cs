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
    /// Reading a module's portable PDB, which is the only place a local variable's name exists.
    /// </summary>
    /// <remarks>
    /// This test assembly is built the default way - a portable PDB in a file beside it - so it
    /// is its own subject: the names asserted here are the ones written in
    /// <see cref="StructuringSample"/> a few lines further up the file it lives in.
    /// </remarks>
    public class ModuleSymbolTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static;

        private static IntPtr TestAssemblyImage =>
            ClrObject.From<StructuringSample>().MethodTable.Metadata.ImageBase;

        [Test]
        public async Task FindsThisAssemblysOwnPdb()
        {
            var symbols = ClrModuleSymbols.AtImageBase(TestAssemblyImage);

            await Assert.That(symbols).IsNotNull();

            // Built with the default DebugType, so the PDB is a file rather than embedded, and
            // its metadata is a portable PDB's rather than a module's.
            await Assert.That(symbols.IsEmbedded).IsFalse();
            await Assert.That(symbols.Source).EndsWith(".pdb");
            await Assert.That(symbols.Image.IsPortablePdb).IsTrue();
            await Assert.That(symbols.Image.RowCount(MetadataTable.LocalVariable)).IsGreaterThan(0);
        }

        /// <summary>
        /// The slot numbers a PDB names have to be the ones the IL loads and stores by, or the
        /// names would be attached to the wrong variables.
        /// </summary>
        [Test]
        public async Task NamesTheSlotsAMethodActuallyUses()
        {
            var method = typeof(StructuringSample).GetMethod(nameof(StructuringSample.Restock), All);
            var names = ClrModuleSymbols.AtImageBase(TestAssemblyImage).LocalNames((uint)method.MetadataToken);

            await Assert.That(names.Count).IsGreaterThanOrEqualTo(2);
            await Assert.That(names.Values).Contains("missing");
            await Assert.That(names.Values).Contains("i");
        }

        /// <summary>
        /// The names reach the locals from both sources of IL - and the MethodDesc path is the
        /// one that matters, since nothing about it goes through reflection.
        /// </summary>
        [Test]
        public async Task LocalsAreNamedFromEitherSourceOfIl()
        {
            var method = typeof(StructuringSample).GetMethod(nameof(StructuringSample.Restock), All);

            var fromReflection = ClrMethodIl.Of(method);
            var fromMemory = ClrMethodIl.Of(
                ClrObject.From<StructuringSample>().MethodTable.FindMethod(nameof(StructuringSample.Restock)));

            foreach (var il in new[] { fromReflection, fromMemory })
            {
                var named = il.LocalVariables.Select(local => local.Name).ToList();

                await Assert.That(named).Contains("missing");
                await Assert.That(named).Contains("i");

                // The type came from the signature and the name from the PDB; both describe the
                // same slot, which is the point of keeping them on one object.
                var missing = il.LocalVariables.First(local => local.Name == "missing");

                await Assert.That(missing.TypeName).Contains("Int32").Or.Contains("int");
                await Assert.That(missing.DisplayName).IsEqualTo("missing");
            }
        }

        /// <summary>
        /// The projection uses them, which is what closes the last gap between it and the source
        /// it was compiled from.
        /// </summary>
        [Test]
        [Arguments(ClrCSharpForm.Faithful)]
        [Arguments(ClrCSharpForm.Structured)]
        public async Task TheProjectionCallsLocalsWhatTheSourceCalledThem(ClrCSharpForm form)
        {
            var dump = ClrMethodCSharp.Of(
                typeof(StructuringSample).GetMethod(nameof(StructuringSample.Restock), All), form).Dump();

            await Assert.That(dump).Contains("missing");
            await Assert.That(dump).Contains("wanted");
            await Assert.That(dump).DoesNotContain("loc0");

            if (form == ClrCSharpForm.Structured)
            {
                // var missing = 0; for (var i = 0; i < wanted; i++) missing += ...
                await Assert.That(dump).Contains("int missing = 0;");
                await Assert.That(dump).Contains("for (int i = 0; i < wanted; i++)");
                await Assert.That(dump).Contains("missing += i < this.Quantity ? 0 : 1;");
            }
        }

        /// <summary>The IL listing names them too, in its <c>.locals</c> block.</summary>
        [Test]
        public async Task TheIlListingNamesTheLocalsAsWell()
        {
            var dump = ClrMethodIl.Of(
                ClrObject.From<StructuringSample>().MethodTable
                    .FindMethod(nameof(StructuringSample.Restock))).Dump();

            await Assert.That(dump).Contains("[0] int missing");
        }

        /// <summary>
        /// A module whose PDB is not to be found keeps working, which is most of them: the
        /// framework ships without its PDBs, so its locals stay slot-numbered.
        /// </summary>
        [Test]
        public async Task AModuleWithNoPdbToFindStillProjects()
        {
            var symbols = ClrModuleSymbols.AtImageBase(
                ClrObject.From<string>().MethodTable.Metadata.ImageBase);

            // CoreLib names a PDB that is not shipped beside it, so this is the miss path.
            if (symbols != null)
                return;

            var withLocals = typeof(Uri).GetMethods(All | BindingFlags.DeclaredOnly)
                .Select(ClrMethodIl.Of)
                .First(il => il != null && il.LocalVariables.Count > 0);

            await Assert.That(withLocals.LocalVariables.All(local => local.Name == null)).IsTrue();
            await Assert.That(withLocals.LocalVariables[0].DisplayName).IsEqualTo("loc0");
            await Assert.That(withLocals.ToCSharp().Dump()).Contains("loc0");
        }

        /// <summary>
        /// Nothing may be assumed about a PDB being there: every lookup has to answer for an
        /// image that has none, or one that is not an image at all.
        /// </summary>
        [Test]
        public async Task LooksForNothingWhenThereIsNoImage()
        {
            await Assert.That(ClrModuleSymbols.AtImageBase(IntPtr.Zero)).IsNull();

            // Asked twice, because the answer is cached and the cache must not turn a miss into
            // an exception the second time.
            await Assert.That(ClrModuleSymbols.AtImageBase(IntPtr.Zero)).IsNull();
        }

        /// <summary>
        /// The reader has to see the PDB's own tables, which are numbered above the type
        /// system's and measured with the module's row counts restated in the <c>#Pdb</c> stream.
        /// </summary>
        [Test]
        public async Task ReadsThePdbsOwnTables()
        {
            var image = ClrModuleSymbols.AtImageBase(TestAssemblyImage).Image;

            await Assert.That(image.PdbId).IsNotNull();
            await Assert.That(image.PdbId.Length).IsEqualTo(20);
            await Assert.That(image.RowCount(MetadataTable.LocalScope)).IsGreaterThan(0);
            await Assert.That(image.RowCount(MetadataTable.MethodDebugInformation)).IsGreaterThan(0);

            // A PDB holds none of the module's own rows, only indexes into them - so a table it
            // does not have must not measure as though it did.
            await Assert.That(image.RowCount(MetadataTable.TypeDef)).IsEqualTo(0);
            await Assert.That(image.RowCount(MetadataTable.MethodDef)).IsEqualTo(0);
        }
    }
}
