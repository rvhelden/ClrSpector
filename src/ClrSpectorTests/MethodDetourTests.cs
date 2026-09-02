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
        [MethodImpl(MethodImplOptions.NoInlining)]
        public decimal GetPrice(string sku) => 100m;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Describe(int quantity) => $"real-{quantity}";
    }

    /// <summary>
    /// Stand-ins. Instance replacements are declared static with a leading parameter for the
    /// instance, so the redirected call never reinterprets 'this' as the wrong type.
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
}
