using System;
using System.Runtime;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// Holds off collection for the duration of a heap walk, and reports afterwards whether it
    /// actually managed to.
    /// </summary>
    /// <remarks>
    /// Walking a live heap from a managed thread inside that heap's own process is racy in a way
    /// that walking a suspended target is not. Other threads keep allocating, and a collection
    /// moves objects, which invalidates every address a walk in progress is holding.
    ///
    /// <c>GC.TryStartNoGCRegion</c> is the only supported lever: it makes the runtime commit
    /// enough memory up front that no collection is needed, so nothing moves. That narrows the
    /// window; it does not close it. The budget can be exhausted by a large enough allocation
    /// burst, at which point a collection happens anyway - so the collection counts are compared
    /// on the way out and <see cref="CollectionOccurred"/> says whether the walk's results can
    /// be trusted. A walk of a very large heap may not fit in a budget at all.
    ///
    /// This is the honest limit of in-process heap walking. It is not equivalent to a debugger's
    /// view of a stopped process.
    ///
    /// <b>Enter the scope before snapshotting the heap.</b> Establishing a no-GC region performs
    /// a blocking collection first, to free the budget it needs, and that collection moves
    /// objects and rebuilds the region lists. A <see cref="ClrGcHeap"/> read beforehand then
    /// holds stale segment bounds, and the walk quietly under-reports rather than failing:
    /// <code>
    /// using var scope = GcWalkScope.Enter();
    /// var heap = ClrGcHeap.Refresh();
    /// foreach (var instance in heap.EnumerateObjects()) { }
    /// scope.ThrowIfInvalidated();
    /// </code>
    /// </remarks>
    public sealed class GcWalkScope : IDisposable
    {
        /// <summary>Enough headroom for a sizeable walk without demanding a huge commit.</summary>
        private const long DefaultBudgetBytes = 64 * 1024 * 1024;

        private readonly int[] countsOnEntry;
        private readonly bool regionStarted;
        private bool disposed;

        private GcWalkScope(bool regionStarted, int[] countsOnEntry)
        {
            this.regionStarted = regionStarted;
            this.countsOnEntry = countsOnEntry;
        }

        /// <summary>True when the no-GC region was actually established.</summary>
        /// <remarks>
        /// False means the runtime declined - typically the budget could not be committed - and
        /// the walk is running unprotected. It is still a walk, but a collection may invalidate
        /// it, which <see cref="CollectionOccurred"/> will then report.
        /// </remarks>
        public bool IsProtected => this.regionStarted;

        /// <summary>True once a collection has happened since the scope was entered.</summary>
        public bool CollectionOccurred
        {
            get
            {
                for (var generation = 0; generation < this.countsOnEntry.Length; generation++)
                {
                    if (GC.CollectionCount(generation) != this.countsOnEntry[generation])
                        return true;
                }

                return false;
            }
        }

        /// <summary>Enters a scope, trying to suspend collection for the default budget.</summary>
        public static GcWalkScope Enter() => Enter(DefaultBudgetBytes);

        /// <summary>Enters a scope, trying to suspend collection for <paramref name="budgetBytes"/>.</summary>
        public static GcWalkScope Enter(long budgetBytes)
        {
            if (budgetBytes <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(budgetBytes), budgetBytes, "The no-GC budget must be positive.");

            var started = false;
            try
            {
                started = GC.TryStartNoGCRegion(budgetBytes);
            }
            catch (ArgumentOutOfRangeException)
            {
                // The budget exceeds what an ephemeral segment can hold; walk unprotected.
            }
            catch (InvalidOperationException)
            {
                // A no-GC region is already in progress, so collection is already held off.
            }

            var counts = new int[GC.MaxGeneration + 1];
            for (var generation = 0; generation < counts.Length; generation++)
                counts[generation] = GC.CollectionCount(generation);

            return new GcWalkScope(started, counts);
        }

        /// <summary>
        /// Throws when a collection has invalidated the walk.
        /// </summary>
        /// <remarks>
        /// Call this after consuming a walk's results. A walk that a collection ran through has
        /// read addresses that no longer mean what they meant, and silently returning those is
        /// worse than failing.
        /// </remarks>
        public void ThrowIfInvalidated()
        {
            if (this.CollectionOccurred)
                throw new ClrSpectorUnsupportedRuntimeException(
                    "A garbage collection ran during the heap walk, so objects may have moved " +
                    "and the addresses read are no longer reliable. Retry with a larger no-GC " +
                    "budget, or walk a smaller part of the heap.");
        }

        public void Dispose()
        {
            if (this.disposed)
                return;

            this.disposed = true;

            if (!this.regionStarted)
                return;

            try
            {
                // The region ends by itself once its budget is exhausted, in which case the
                // latency mode has already been reset and ending it again would throw.
                if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                    GC.EndNoGCRegion();
            }
            catch (InvalidOperationException)
            {
                // Raced with the budget running out; nothing left to end.
            }
        }
    }
}