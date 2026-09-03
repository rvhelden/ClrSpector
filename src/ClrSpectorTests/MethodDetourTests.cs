using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ClrSpector.Detours;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    /// <summary>The concrete class under test - no interface, nothing virtual.</summary>
    public class PriceService
    {
        public decimal FixedPrice = 100m;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public decimal GetPrice(string sku) => 100m;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Describe(int quantity) => $"real-{quantity}";
    }

    /// <summary>
    /// Stand-ins. A stand-in for an instance method is declared static with a leading parameter
    /// for the instance, so the redirected call never reinterprets 'this' as the wrong type.
    /// </summary>
    public static class PriceServiceProxy
    {
        public static int GetPriceCalls;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static decimal GetPrice(PriceService instance, string sku)
        {
            GetPriceCalls++;
            return 42m;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Describe(int quantity) => $"proxy-{quantity}";
    }

    /// <summary>
    /// Swapping a method's dispatch pointer so a concrete method can be stood in for under
    /// test, without the production type needing an interface.
    /// </summary>
    /// <remarks>
    /// A redirect mutates one process-wide dispatch slot, so these tests must not run
    /// concurrently with each other - two tests redirecting the same method would each undo
    /// the other's swap. Any test suite using <see cref="MethodDetour"/> on a shared method
    /// needs the same treatment.
    /// </remarks>
    [NotInParallel]
    public class MethodDetourTests
    {
        [Test]
        public async Task RedirectsInstanceMethodAndRestoresOnDispose()
        {
            var service = new PriceService();

            await Assert.That(service.GetPrice("abc")).IsEqualTo(100m);

            using (MethodDetour.Redirect(
                       typeof(PriceService), nameof(PriceService.GetPrice),
                       typeof(PriceServiceProxy), nameof(PriceServiceProxy.GetPrice)))
            {
                await Assert.That(service.GetPrice("abc")).IsEqualTo(42m);
            }

            // The using block must put the original dispatch target back.
            await Assert.That(service.GetPrice("abc")).IsEqualTo(100m);
        }

        [Test]
        public async Task RedirectsStaticMethodAndRestoresOnDispose()
        {
            await Assert.That(PriceService.Describe(2)).IsEqualTo("real-2");

            using (MethodDetour.Redirect(
                       typeof(PriceService), nameof(PriceService.Describe),
                       typeof(PriceServiceProxy), nameof(PriceServiceProxy.Describe)))
            {
                await Assert.That(PriceService.Describe(2)).IsEqualTo("proxy-2");
            }

            await Assert.That(PriceService.Describe(2)).IsEqualTo("real-2");
        }

        [Test]
        public async Task ReplacementObservesTheCallAndItsArguments()
        {
            var service = new PriceService();
            PriceServiceProxy.GetPriceCalls = 0;

            using (MethodDetour.Redirect(
                       typeof(PriceService), nameof(PriceService.GetPrice),
                       typeof(PriceServiceProxy), nameof(PriceServiceProxy.GetPrice)))
            {
                service.GetPrice("a");
                service.GetPrice("b");
            }

            await Assert.That(PriceServiceProxy.GetPriceCalls).IsEqualTo(2);
        }

        /// <summary>
        /// Every caller reaches the method through the same dispatch slot, so a redirect is not
        /// specific to one call shape.
        /// </summary>
        [Test]
        public async Task RedirectAppliesToDirectCallsDelegatesAndReflection()
        {
            var service = new PriceService();
            var method = typeof(PriceService).GetMethod(nameof(PriceService.GetPrice));
            var viaDelegate = (Func<PriceService, string, decimal>)Delegate.CreateDelegate(
                typeof(Func<PriceService, string, decimal>), method);

            using (MethodDetour.Redirect(
                       method,
                       typeof(PriceServiceProxy).GetMethod(nameof(PriceServiceProxy.GetPrice))))
            {
                await Assert.That(service.GetPrice("x")).IsEqualTo(42m);
                await Assert.That(viaDelegate(service, "x")).IsEqualTo(42m);
                await Assert.That((decimal)method.Invoke(service, new object[] { "x" })).IsEqualTo(42m);
            }

            await Assert.That(service.GetPrice("x")).IsEqualTo(100m);
            await Assert.That(viaDelegate(service, "x")).IsEqualTo(100m);
            await Assert.That((decimal)method.Invoke(service, new object[] { "x" })).IsEqualTo(100m);
        }

        [Test]
        public async Task DisposeIsIdempotent()
        {
            var service = new PriceService();
            var detour = MethodDetour.Redirect(
                typeof(PriceService), nameof(PriceService.GetPrice),
                typeof(PriceServiceProxy), nameof(PriceServiceProxy.GetPrice));

            await Assert.That(detour.IsActive).IsTrue();

            detour.Dispose();
            detour.Dispose();

            await Assert.That(detour.IsActive).IsFalse();
            await Assert.That(service.GetPrice("x")).IsEqualTo(100m);
        }

        /// <summary>
        /// A mismatched replacement would corrupt the stack, so it is refused up front rather
        /// than producing undefined behaviour at the call.
        /// </summary>
        [Test]
        public async Task RefusesIncompatibleSignatures()
        {
            await Assert.That(() => MethodDetour.Redirect(
                    typeof(PriceService), nameof(PriceService.GetPrice),
                    typeof(PriceServiceProxy), nameof(PriceServiceProxy.Describe)))
                .Throws<MethodDetourException>();
        }

        /// <summary>
        /// A failed swap must leave the target callable.
        /// </summary>
        [Test]
        public async Task TargetStillWorksAfterARefusedRedirect()
        {
            var service = new PriceService();

            try
            {
                MethodDetour.Redirect(
                    typeof(PriceService), nameof(PriceService.GetPrice),
                    typeof(PriceServiceProxy), nameof(PriceServiceProxy.Describe));
            }
            catch (MethodDetourException)
            {
            }

            await Assert.That(service.GetPrice("x")).IsEqualTo(100m);
        }
    }

    /// <summary>A concrete type with virtual members, and no interface in sight.</summary>
    public class Repository
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual string Load(int id) => $"real-{id}";
    }

    public class CachingRepository : Repository
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override string Load(int id) => $"cached-{id}";
    }

    public abstract class ReportBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public abstract string Render();
    }

    public sealed class Report : ReportBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override string Render() => "real-report";
    }

    /// <summary>A method that also implements an interface member.</summary>
    public class Exporter : IExporter
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Export() => "real-export";
    }

    public interface IExporter
    {
        string Export();
    }

    public static class RepositoryProxy
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Load(Repository self, int id) => $"proxy-{id}";

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string LoadCaching(CachingRepository self, int id) => $"proxy-cached-{id}";

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Render(Report self) => "proxy-report";

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Export(Exporter self) => "proxy-export";
    }

    /// <summary>
    /// Virtual methods dispatch through the MethodTable vtable rather than the precode, so they
    /// need a second patch. These cover that path.
    /// </summary>
    [NotInParallel]
    public class VirtualMethodDetourTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                                             | BindingFlags.Instance | BindingFlags.Static;

        [Test]
        public async Task RedirectsVirtualMethodAndRestoresOnDispose()
        {
            var repository = new Repository();

            await Assert.That(repository.Load(1)).IsEqualTo("real-1");

            using (MethodDetour.Redirect(typeof(Repository), nameof(Repository.Load),
                       typeof(RepositoryProxy), nameof(RepositoryProxy.Load)))
            {
                await Assert.That(repository.Load(1)).IsEqualTo("proxy-1");
            }

            await Assert.That(repository.Load(1)).IsEqualTo("real-1");
        }

        /// <summary>A virtual redirect must patch both dispatch paths, not just the precode.</summary>
        [Test]
        public async Task VirtualRedirectPatchesPrecodeAndVtable()
        {
            using var detour = MethodDetour.Redirect(
                typeof(Repository), nameof(Repository.Load),
                typeof(RepositoryProxy), nameof(RepositoryProxy.Load));

            await Assert.That(detour.PatchedTargets)
                .IsEqualTo(DetourTargets.Precode | DetourTargets.Vtable);
            await Assert.That(detour.VtableSlot).IsNotEqualTo(IntPtr.Zero);
            await Assert.That(detour.Precode.HasDispatchSlot).IsTrue();
        }

        [Test]
        public async Task NonVirtualRedirectPatchesOnlyThePrecode()
        {
            using var detour = MethodDetour.Redirect(
                typeof(PriceService), nameof(PriceService.GetPrice),
                typeof(PriceServiceProxy), nameof(PriceServiceProxy.GetPrice));

            await Assert.That(detour.PatchedTargets).IsEqualTo(DetourTargets.Precode);
            await Assert.That(detour.VtableSlot).IsEqualTo(IntPtr.Zero);
        }

        /// <summary>An override redirected through a base-typed reference.</summary>
        [Test]
        public async Task RedirectsAnOverrideCalledThroughTheBaseType()
        {
            Repository repository = new CachingRepository();

            await Assert.That(repository.Load(2)).IsEqualTo("cached-2");

            using (MethodDetour.Redirect(
                       typeof(CachingRepository).GetMethod(nameof(Repository.Load), All),
                       typeof(RepositoryProxy).GetMethod(nameof(RepositoryProxy.LoadCaching), All)))
            {
                await Assert.That(repository.Load(2)).IsEqualTo("proxy-cached-2");
            }

            await Assert.That(repository.Load(2)).IsEqualTo("cached-2");
        }

        /// <summary>Redirecting a base type's slot must not disturb an overriding subclass.</summary>
        [Test]
        public async Task RedirectingTheBaseLeavesAnOverridingSubclassAlone()
        {
            Repository overriding = new CachingRepository();

            using (MethodDetour.Redirect(typeof(Repository), nameof(Repository.Load),
                       typeof(RepositoryProxy), nameof(RepositoryProxy.Load)))
            {
                await Assert.That(new Repository().Load(3)).IsEqualTo("proxy-3");
                await Assert.That(overriding.Load(3)).IsEqualTo("cached-3");
            }
        }

        [Test]
        public async Task RedirectsASealedOverrideCalledThroughTheAbstractBase()
        {
            ReportBase report = new Report();

            await Assert.That(report.Render()).IsEqualTo("real-report");

            using (MethodDetour.Redirect(typeof(Report), nameof(Report.Render),
                       typeof(RepositoryProxy), nameof(RepositoryProxy.Render)))
            {
                await Assert.That(report.Render()).IsEqualTo("proxy-report");
            }

            await Assert.That(report.Render()).IsEqualTo("real-report");
        }

        /// <summary>An abstract declaration has no implementation to redirect.</summary>
        [Test]
        public async Task RefusesAnAbstractMethod()
        {
            await Assert.That(() => MethodDetour.Redirect(
                    typeof(ReportBase).GetMethod(nameof(ReportBase.Render), All),
                    typeof(RepositoryProxy).GetMethod(nameof(RepositoryProxy.Render), All)))
                .Throws<MethodDetourException>();
        }

        /// <summary>
        /// Interface dispatch caches the resolved target, and that cache is NOT undone on
        /// dispose - the redirect leaks permanently, process-wide, reaching instances created
        /// afterwards. Refusing by default is the only safe behaviour for a test tool.
        /// </summary>
        [Test]
        public async Task RefusesAMethodThatImplementsAnInterfaceMethod()
        {
            await Assert.That(() => MethodDetour.Redirect(
                    typeof(Exporter), nameof(Exporter.Export),
                    typeof(RepositoryProxy), nameof(RepositoryProxy.Export)))
                .Throws<MethodDetourException>();

            // and the target is untouched by the refusal
            await Assert.That(new Exporter().Export()).IsEqualTo("real-export");
        }

        /// <summary>
        /// The refusal can be overridden knowingly. This test deliberately never calls through
        /// an interface reference, since doing so would leak the redirect for the whole run.
        /// </summary>
        [Test]
        public async Task InterfaceDispatchGuardCanBeOverriddenExplicitly()
        {
            var exporter = new Exporter();

            using (MethodDetour.Redirect(
                       typeof(Exporter).GetMethod(nameof(Exporter.Export), All),
                       typeof(RepositoryProxy).GetMethod(nameof(RepositoryProxy.Export), All),
                       allowInterfaceDispatch: true))
            {
                await Assert.That(exporter.Export()).IsEqualTo("proxy-export");
            }

            await Assert.That(exporter.Export()).IsEqualTo("real-export");
        }
    }
}