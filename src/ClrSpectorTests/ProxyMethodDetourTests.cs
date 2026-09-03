using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ClrSpector.Detours;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    /// <summary>A proxy object: a stand-in with state of its own.</summary>
    public class StatefulPriceProxy
    {
        public readonly List<string> Seen = new List<string>();

        public decimal Bonus = 42m;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual decimal GetPrice(PriceService instance, string sku)
        {
            this.Seen.Add(sku);

            return this.Bonus + instance.FixedPrice;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Describe(int quantity) => $"{this.Bonus}-proxy-{quantity}";
    }

    public class DerivedPriceProxy : StatefulPriceProxy
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override decimal GetPrice(PriceService instance, string sku) => 999m;
    }

    public class RepositoryStandIn
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Load(Repository repository, int id) => $"stand-in-{id}";
    }

    /// <summary>Bigger than a register, so it comes back through a hidden buffer.</summary>
    public struct Money
    {
        public decimal Amount;

        public long Currency;
    }

    public class Basket
    {
        public int Recorded;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Money Total(int quantity) => new Money { Amount = quantity, Currency = 1 };

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Record(int quantity) => this.Recorded += quantity;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool TryGet(string key, out decimal value)
        {
            value = 1m;

            return false;
        }
    }

    public class BasketProxy
    {
        public int Recorded;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Money Total(Basket basket, int quantity) => new Money { Amount = 7m, Currency = 99 };

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Record(Basket basket, int quantity) => this.Recorded += quantity * 10;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool TryGet(Basket basket, string key, out decimal value)
        {
            value = 5m;

            return true;
        }
    }

    /// <summary>Shapes a redirect has to refuse.</summary>
    public struct Point
    {
        public int X;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Sum(int y) => this.X + y;
    }

    public class Cache<T>
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Load(int id) => $"cache-{id}";
    }

    public class Widget
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public T Pick<T>(T candidate) => candidate;
    }

    public static class RefusedProxies
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Sum(ref Point self, int y) => 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Load(object self, int id) => "proxy";
    }

    /// <summary>
    /// A dispatch slot holds a code address and nothing else, so a proxy <i>object</i> cannot
    /// simply be patched in: its own receiver would displace every argument. These cover the
    /// generated thunk that supplies the receiver instead - and the one that repairs a static
    /// stand-in whose hidden return buffer does not line up.
    /// </summary>
    [NotInParallel]
    public class ProxyMethodDetourTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static;

        [Test]
        public async Task ProxyObjectStandsInForAnInstanceMethod()
        {
            var service = new PriceService();
            var proxy = new StatefulPriceProxy();

            using (MethodDetour.Redirect(
                       typeof(PriceService), nameof(PriceService.GetPrice),
                       proxy, nameof(StatefulPriceProxy.GetPrice)))
            {
                // 42 from the proxy's own field, 100 from the real service: both receivers
                // arrived, and in the right order.
                await Assert.That(service.GetPrice("abc")).IsEqualTo(142m);
            }

            await Assert.That(proxy.Seen.Count).IsEqualTo(1);
            await Assert.That(proxy.Seen[0]).IsEqualTo("abc");
            await Assert.That(service.GetPrice("abc")).IsEqualTo(100m);
        }

        /// <summary>The proxy is not baked into the thunk, so a second one is really a second one.</summary>
        [Test]
        public async Task EachProxyKeepsItsOwnState()
        {
            var service = new PriceService();
            var first = new StatefulPriceProxy { Bonus = 1m };
            var second = new StatefulPriceProxy { Bonus = 2m };

            using (MethodDetour.Redirect(typeof(PriceService), nameof(PriceService.GetPrice),
                                         first, nameof(StatefulPriceProxy.GetPrice)))
            {
                await Assert.That(service.GetPrice("a")).IsEqualTo(101m);
            }

            using (MethodDetour.Redirect(typeof(PriceService), nameof(PriceService.GetPrice),
                                         second, nameof(StatefulPriceProxy.GetPrice)))
            {
                await Assert.That(service.GetPrice("b")).IsEqualTo(102m);
            }

            await Assert.That(first.Seen.Count).IsEqualTo(1);
            await Assert.That(second.Seen.Count).IsEqualTo(1);
        }

        /// <summary>A delegate carries its receiver, so a closure works as a stand-in.</summary>
        [Test]
        public async Task ClosureStandsInForAnInstanceMethod()
        {
            var service = new PriceService();
            var captured = 7m;

            Func<PriceService, string, decimal> standIn = (instance, sku) => captured + instance.FixedPrice;

            using (MethodDetour.Redirect(
                       typeof(PriceService).GetMethod(nameof(PriceService.GetPrice), All), standIn))
            {
                await Assert.That(service.GetPrice("abc")).IsEqualTo(107m);

                // The closure itself is the receiver, so it is read live rather than snapshotted.
                captured = 8m;
                await Assert.That(service.GetPrice("abc")).IsEqualTo(108m);
            }

            await Assert.That(service.GetPrice("abc")).IsEqualTo(100m);
        }

        /// <summary>A virtual stand-in resolves against the proxy actually supplied.</summary>
        [Test]
        public async Task ProxyMethodResolvesAgainstTheProxysOwnType()
        {
            var service = new PriceService();

            using (MethodDetour.Redirect(
                       typeof(PriceService), nameof(PriceService.GetPrice),
                       new DerivedPriceProxy(), nameof(StatefulPriceProxy.GetPrice)))
            {
                await Assert.That(service.GetPrice("abc")).IsEqualTo(999m);
            }
        }

        [Test]
        public async Task ProxyStandsInForAStaticMethod()
        {
            var proxy = new StatefulPriceProxy { Bonus = 3m };

            using (MethodDetour.Redirect(typeof(PriceService), nameof(PriceService.Describe),
                                         proxy, nameof(StatefulPriceProxy.Describe)))
            {
                await Assert.That(PriceService.Describe(2)).IsEqualTo("3-proxy-2");
            }

            await Assert.That(PriceService.Describe(2)).IsEqualTo("real-2");
        }

        [Test]
        public async Task VirtualTargetIsReachedThroughTheThunkOnBothPaths()
        {
            var repository = new Repository();

            using var detour = MethodDetour.Redirect(
                typeof(Repository).GetMethod(nameof(Repository.Load), All),
                (Func<Repository, int, string>)new RepositoryStandIn().Load);

            await Assert.That(repository.Load(4)).IsEqualTo("stand-in-4");
            await Assert.That(detour.PatchedTargets).IsEqualTo(DetourTargets.Precode | DetourTargets.Vtable);
            await Assert.That(detour.UsesThunk).IsTrue();
        }

        /// <summary>
        /// A struct return travels through a hidden buffer whose pointer is passed after the
        /// receiver - the case that has to survive the thunk intact.
        /// </summary>
        [Test]
        public async Task StructReturnSurvivesTheThunk()
        {
            var basket = new Basket();
            var proxy = new BasketProxy();

            using (MethodDetour.Redirect(typeof(Basket), nameof(Basket.Total),
                                         proxy, nameof(BasketProxy.Total)))
            {
                var total = basket.Total(3);

                await Assert.That(total.Amount).IsEqualTo(7m);
                await Assert.That(total.Currency).IsEqualTo(99L);
            }

            await Assert.That(basket.Total(3).Currency).IsEqualTo(1L);
        }

        [Test]
        public async Task VoidReturnSurvivesTheThunk()
        {
            var basket = new Basket();
            var proxy = new BasketProxy();

            using (MethodDetour.Redirect(typeof(Basket), nameof(Basket.Record),
                                         proxy, nameof(BasketProxy.Record)))
            {
                basket.Record(2);
            }

            await Assert.That(proxy.Recorded).IsEqualTo(20);
            await Assert.That(basket.Recorded).IsEqualTo(0);
        }

        [Test]
        public async Task OutParameterSurvivesTheThunk()
        {
            var basket = new Basket();
            var proxy = new BasketProxy();

            using (MethodDetour.Redirect(typeof(Basket), nameof(Basket.TryGet),
                                         proxy, nameof(BasketProxy.TryGet)))
            {
                var found = basket.TryGet("k", out var value);

                await Assert.That(found).IsTrue();
                await Assert.That(value).IsEqualTo(5m);
            }
        }

        /// <summary>
        /// A static stand-in for an instance method returning a register-sized value needs no
        /// adapter at all.
        /// </summary>
        [Test]
        public async Task AStaticStandInIsPatchedStraightIn()
        {
            using var detour = MethodDetour.Redirect(
                typeof(Repository), nameof(Repository.Load),
                typeof(RepositoryProxy), nameof(RepositoryProxy.Load));

            await Assert.That(detour.Pairing).IsEqualTo(MethodPairing.Direct);
            await Assert.That(detour.UsesThunk).IsFalse();
            await Assert.That(detour.ThunkEntryPoint).IsEqualTo(IntPtr.Zero);
        }

        /// <summary>
        /// ...but one whose return value travels in a hidden buffer does, because that buffer is
        /// passed after the receiver and so does not line up. Patched in directly, the returned
        /// value lands in the target object instead - verified, along with the dead GC that
        /// follows.
        /// </summary>
        [Test]
        public async Task AStaticStandInForABufferedReturnIsAdapted()
        {
            var service = new PriceService();

            using (var detour = MethodDetour.Redirect(
                       typeof(PriceService), nameof(PriceService.GetPrice),
                       typeof(PriceServiceProxy), nameof(PriceServiceProxy.GetPrice)))
            {
                await Assert.That(detour.Pairing).IsEqualTo(MethodPairing.AbiShim);
                await Assert.That(detour.UsesThunk).IsTrue();

                await Assert.That(service.GetPrice("abc")).IsEqualTo(42m);
                await Assert.That(service.FixedPrice).IsEqualTo(100m);
            }
        }

        [Test]
        public async Task DisposeIsIdempotentOnTheThunkPath()
        {
            var service = new PriceService();
            var detour = MethodDetour.Redirect(typeof(PriceService), nameof(PriceService.GetPrice),
                                               new StatefulPriceProxy(), nameof(StatefulPriceProxy.GetPrice));

            detour.Dispose();
            detour.Dispose();

            await Assert.That(detour.IsActive).IsFalse();
            await Assert.That(service.GetPrice("x")).IsEqualTo(100m);
        }

        /// <summary>The proxy must not outlive the redirect that bound it.</summary>
        [Test]
        public async Task DisposeReleasesTheProxy()
        {
            var weak = RedirectAndDispose();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            await Assert.That(weak.IsAlive).IsFalse();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference RedirectAndDispose()
        {
            var proxy = new StatefulPriceProxy();

            using (MethodDetour.Redirect(typeof(PriceService), nameof(PriceService.GetPrice),
                                         proxy, nameof(StatefulPriceProxy.GetPrice)))
            {
                new PriceService().GetPrice("x");
            }

            return new WeakReference(proxy);
        }

        /// <summary>
        /// An instance stand-in with no object to run on is the shape this whole path exists for,
        /// so the refusal has to say how to supply one.
        /// </summary>
        [Test]
        public async Task RefusesAProxyMethodWithNoReceiverAndSaysWhy()
        {
            var service = new PriceService();

            var exception = await Assert.That(() => MethodDetour.Redirect(
                    typeof(PriceService), nameof(PriceService.GetPrice),
                    typeof(StatefulPriceProxy), nameof(StatefulPriceProxy.GetPrice)))
                .Throws<MethodDetourException>();

            await Assert.That(exception.Message).Contains("proxy object");
            await Assert.That(service.GetPrice("x")).IsEqualTo(100m);
        }

        [Test]
        public async Task RefusesAValueTypeTarget()
        {
            await Assert.That(() => MethodDetour.Redirect(
                    typeof(Point).GetMethod(nameof(Point.Sum), All),
                    typeof(RefusedProxies).GetMethod(nameof(RefusedProxies.Sum), All)))
                .Throws<MethodDetourException>();
        }

        [Test]
        public async Task RefusesAMethodOnAGenericType()
        {
            await Assert.That(() => MethodDetour.Redirect(
                    typeof(Cache<string>).GetMethod(nameof(Cache<string>.Load), All),
                    typeof(RefusedProxies).GetMethod(nameof(RefusedProxies.Load), All)))
                .Throws<MethodDetourException>();
        }

        [Test]
        public async Task RefusesAGenericMethod()
        {
            await Assert.That(() => MethodDetour.Redirect(
                    typeof(Widget).GetMethod(nameof(Widget.Pick), All).MakeGenericMethod(typeof(int)),
                    typeof(RefusedProxies).GetMethod(nameof(RefusedProxies.Load), All)))
                .Throws<MethodDetourException>();
        }

        [Test]
        public async Task RefusesAMulticastDelegate()
        {
            var combined = (Func<PriceService, string, decimal>)Delegate.Combine(
                (Func<PriceService, string, decimal>)new StatefulPriceProxy().GetPrice,
                (Func<PriceService, string, decimal>)new StatefulPriceProxy().GetPrice);

            await Assert.That(() => MethodDetour.Redirect(
                    typeof(PriceService).GetMethod(nameof(PriceService.GetPrice), All), combined))
                .Throws<MethodDetourException>();
        }

        [Test]
        public async Task RefusesATypeWhereAProxyObjectIsExpected()
        {
            await Assert.That(() => MethodDetour.Redirect(
                    typeof(PriceService).GetMethod(nameof(PriceService.GetPrice), All),
                    (object)typeof(StatefulPriceProxy),
                    typeof(StatefulPriceProxy).GetMethod(nameof(StatefulPriceProxy.GetPrice), All)))
                .Throws<MethodDetourException>();
        }
    }
}
