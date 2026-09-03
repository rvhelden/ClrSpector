using System;
using System.ComponentModel;
using System.Threading;

namespace ClrSpector.Detours
{
    /// <summary>
    /// Holds the proxy objects that generated thunks call into. Public only because the emitted
    /// IL calls across an assembly boundary; nothing here is meant to be called by hand.
    /// </summary>
    /// <remarks>
    /// A thunk bakes in a slot index, not the proxy itself, so disposing a redirect can release
    /// the proxy immediately instead of leaking it for the life of the process. A slot belongs
    /// permanently to one target/replacement pairing, so a call still in flight through a thunk
    /// after its redirect was disposed finds either that pairing's proxy or nothing - never some
    /// unrelated object of another type.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class DetourThunkSupport
    {
        private static readonly object Gate = new object();

        private static Slot[] slots = new Slot[8];

        private static int count;

        /// <summary>Reads the proxy a thunk should run on. Called from generated IL.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static object Get(int slot)
        {
            var current = Volatile.Read(ref slots);

            return slot >= 0 && slot < current.Length ? current[slot]?.Receiver : null;
        }

        /// <summary>
        /// Reports a call that arrived through a thunk whose redirect is no longer active.
        /// Called from generated IL, and never returns - the <see cref="object"/> return type
        /// only keeps the emitted stack shape honest on both branches.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static object ThrowStale(int slot)
        {
            var pairing = Volatile.Read(ref slots)[slot]?.Description ?? $"slot {slot}";

            throw new MethodDetourException(
                $"A call reached the detour thunk for {pairing} after the redirect was disposed. " +
                "The dispatch slot should have been restored first, so this means either a call " +
                "was already in flight on another thread, or something re-patched the slot.");
        }

        /// <summary>Claims the slot belonging to one target/replacement pairing.</summary>
        internal static int Reserve(string description)
        {
            lock (Gate)
            {
                if (count == slots.Length)
                {
                    var grown = new Slot[slots.Length * 2];
                    Array.Copy(slots, grown, slots.Length);
                    Volatile.Write(ref slots, grown);
                }

                slots[count] = new Slot { Description = description };

                return count++;
            }
        }

        /// <summary>Points a slot at the proxy for the duration of one redirect.</summary>
        internal static void Bind(int slot, object owner, object receiver)
        {
            lock (Gate)
            {
                var existing = slots[slot];

                if (existing.Owner != null && !ReferenceEquals(existing.Owner, owner))
                    throw new MethodDetourException(
                        $"{existing.Description} is already redirected by another live detour. " +
                        "Dispose that one first - two redirects of the same method would undo " +
                        "each other.");

                existing.Owner = owner;
                existing.Receiver = receiver;
            }
        }

        /// <summary>
        /// Releases a slot so the proxy becomes collectable. Does nothing when the slot has
        /// since been claimed by someone else, which keeps a double dispose harmless.
        /// </summary>
        internal static void Release(int slot, object owner)
        {
            if (slot < 0)
                return;

            lock (Gate)
            {
                var existing = slots[slot];

                if (!ReferenceEquals(existing.Owner, owner))
                    return;

                existing.Owner = null;
                existing.Receiver = null;
            }
        }

        private sealed class Slot
        {
            public string Description { get; set; }

            public object Owner { get; set; }

            public object Receiver { get; set; }
        }
    }
}
