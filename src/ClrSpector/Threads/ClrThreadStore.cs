using System;
using System.Collections.Generic;
using ClrSpector.Cdac;

namespace ClrSpector
{
    /// <summary>
    /// The runtime's state bits for a managed thread.
    /// </summary>
    [Flags]
    public enum ClrThreadState : uint
    {
        None = 0,

        /// <summary>Trapped at a suspension point while the runtime suspends for the GC.</summary>
        SuspensionTrapped = 0x2,

        GcSuspendRedirected = 0x4,
        DebugSuspendPending = 0x8,

        /// <summary>The runtime redirected the thread's return address to take control of it.</summary>
        Hijacked = 0x80,

        Background = 0x200,
        Unstarted = 0x400,
        CoInitialized = 0x2000,
        InSingleThreadedApartment = 0x4000,
        InMultiThreadedApartment = 0x8000,
        Stopped = 0x10000,
        DebugSyncSuspended = 0x80000,
        DebugWillSync = 0x100000,
        ThreadPoolWorker = 0x1000000,

        /// <summary>Inside a Wait, Sleep or Join.</summary>
        WaitSleepJoin = 0x2000000,

        Detached = 0x80000000
    }

    /// <summary>
    /// One managed thread as the runtime tracks it - which is more than
    /// <see cref="System.Threading.Thread"/> exposes: its OS thread, its stack bounds, whether it
    /// is currently in cooperative mode, and what it last threw.
    /// </summary>
    public sealed class ClrThread
    {
        /// <summary>The runtime's Thread object.</summary>
        public IntPtr Address { get; internal set; }

        /// <summary>The managed thread id, the same one <see cref="System.Threading.Thread.ManagedThreadId"/> reports.</summary>
        public uint ManagedThreadId { get; internal set; }

        /// <summary>The operating system thread id.</summary>
        public ulong OsThreadId { get; internal set; }

        public ClrThreadState State { get; internal set; }

        /// <summary>
        /// True when the thread is running managed code, so the GC must suspend it before
        /// collecting rather than simply ignoring it.
        /// </summary>
        public bool IsInCooperativeMode { get; internal set; }

        /// <summary>The top of the thread's stack - the highest address, since stacks grow down.</summary>
        public IntPtr StackBase { get; internal set; }

        /// <summary>The limit the stack may grow to.</summary>
        public IntPtr StackLimit { get; internal set; }

        /// <summary>The managed Thread object for this thread, as a heap address.</summary>
        public IntPtr ExposedObject { get; internal set; }

        /// <summary>The most recent exception thrown on this thread, as a heap address.</summary>
        public IntPtr LastThrownObject { get; internal set; }

        /// <summary>The runtime's current frame chain head for this thread.</summary>
        public IntPtr Frame { get; internal set; }

        public bool IsBackground => (this.State & ClrThreadState.Background) != 0;

        public bool IsUnstarted => (this.State & ClrThreadState.Unstarted) != 0;

        public bool IsThreadPoolWorker => (this.State & ClrThreadState.ThreadPoolWorker) != 0;

        public bool IsWaiting => (this.State & ClrThreadState.WaitSleepJoin) != 0;

        /// <summary>How many bytes of stack the thread has been given.</summary>
        public long StackSize => this.StackBase.ToInt64() - this.StackLimit.ToInt64();

        public override string ToString()
        {
            return $"thread @0x{this.Address.ToInt64():x} managedId={this.ManagedThreadId} " +
                   $"osId={this.OsThreadId} state={this.State} coop={this.IsInCooperativeMode} " +
                   $"stack={this.StackSize / 1024}KB";
        }
    }

    /// <summary>
    /// Every managed thread in the process, as the runtime's own ThreadStore lists them.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Diagnostics.Process.Threads"/> lists OS threads and knows nothing about
    /// which are managed; the managed side offers no enumeration at all. This is the runtime's
    /// own list, which is both complete and managed-only, and it carries the runtime's view of
    /// each thread rather than the operating system's.
    /// </remarks>
    public sealed unsafe class ClrThreadStore
    {
        private ClrThreadStore()
        {
        }

        /// <summary>The runtime's ThreadStore.</summary>
        public IntPtr Address { get; private set; }

        public int ThreadCount { get; private set; }

        public int BackgroundCount { get; private set; }

        public int PendingCount { get; private set; }

        public int DeadCount { get; private set; }

        public int UnstartedCount { get; private set; }

        /// <summary>The threads, in the order the runtime links them.</summary>
        public IReadOnlyList<ClrThread> Threads { get; private set; }

        /// <summary>Reads the thread store of the running process.</summary>
        /// <remarks>
        /// Not cached: threads start and finish constantly, so a snapshot is only ever true of
        /// the moment it was taken.
        /// </remarks>
        public static ClrThreadStore Read()
        {
            var descriptor = ContractDescriptor.Current;
            var storeLayout = descriptor.GetDataType("ThreadStore");
            var threadLayout = descriptor.GetDataType("Thread");

            // The global names a pointer variable, so it takes one dereference to reach the store.
            var address = descriptor.Globals.Dereference("ThreadStore");

            var reader = new MemoryReader(address);

            var store = new ClrThreadStore
            {
                Address = address,
                ThreadCount = reader.ReadInt(storeLayout["ThreadCount"]),
                BackgroundCount = reader.ReadInt(storeLayout["BackgroundCount"]),
                PendingCount = reader.ReadInt(storeLayout["PendingCount"]),
                DeadCount = reader.ReadInt(storeLayout["DeadCount"]),
                UnstartedCount = reader.ReadInt(storeLayout["UnstartedCount"])
            };

            var threads = new List<ClrThread>();

            // The list links Thread to Thread directly, so the link is the next thread.
            var thread = reader.ReadIntPtr(storeLayout["FirstThreadLink"]);

            // The count bounds the walk, so a corrupted link cannot spin forever.
            var limit = Math.Max(store.ThreadCount, 0) + store.DeadCount + 64;

            while (thread != IntPtr.Zero && threads.Count < limit)
            {
                threads.Add(ReadThread(thread, threadLayout));

                thread = new MemoryReader(thread).ReadIntPtr(threadLayout["LinkNext"]);
            }

            store.Threads = threads;

            return store;
        }

        private static ClrThread ReadThread(IntPtr address, DataType layout)
        {
            var reader = new MemoryReader(address);

            return new ClrThread
            {
                Address = address,
                ManagedThreadId = reader.ReadUInt(layout["Id"]),
                OsThreadId = (ulong)reader.ReadIntPtr(layout["OSId"]).ToInt64(),
                State = (ClrThreadState)reader.ReadUInt(layout["State"]),
                IsInCooperativeMode = reader.ReadUInt(layout["PreemptiveGCDisabled"]) != 0,
                StackBase = reader.ReadIntPtr(layout["CachedStackBase"]),
                StackLimit = reader.ReadIntPtr(layout["CachedStackLimit"]),
                ExposedObject = reader.ReadIntPtr(layout["ExposedObject"]),
                LastThrownObject = reader.ReadIntPtr(layout["LastThrownObject"]),
                Frame = reader.ReadIntPtr(layout["Frame"])
            };
        }

        public override string ToString()
        {
            return $"ThreadStore @0x{this.Address.ToInt64():x} threads={this.ThreadCount} " +
                   $"background={this.BackgroundCount} pending={this.PendingCount} " +
                   $"dead={this.DeadCount} unstarted={this.UnstartedCount} " +
                   $"walked={this.Threads?.Count ?? 0}";
        }
    }
}
