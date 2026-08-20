// App-side thread backend for Tasks.
//
// Linked into freestanding apps only — the kernel gets
// Threading.Tasks.KernelScheduler.cs instead. The Task code above both is
// shared and unchanged; this is the one file that knows where threads come
// from, which here means the service table (ABI V3).

using System;
using System.Threading;
using SharpOS.AppSdk;

namespace SharpOS.AppSdk
{
    internal static unsafe class TaskBackendInstaller
    {
        // The same queue as the kernel side, for the same reason: the service
        // call takes a bare function pointer, so the delegate cannot travel
        // with the thread and has to be left where the thread will find it.
        // Which thread picks up which entry does not matter — every entry is
        // independent work — so a queue removes the ordering question instead
        // of assuming an answer to it.
        private const int Capacity = 32;
        private static ThreadBackend.ThreadEntry?[] s_queue = null!;
        private static int s_head;
        private static int s_tail;

        /// <summary>
        /// Returns false when the kernel is older than V3: the app then knows
        /// tasks will not work, instead of finding out when one silently never
        /// runs.
        /// </summary>
        public static bool Install()
        {
            if (!AppThreads.IsAvailable) return false;

            if (s_queue == null) s_queue = new ThreadBackend.ThreadEntry?[Capacity];
            ThreadBackend.Install(Spawn, AppThreads.Sleep);

            // Thread identity, which Monitor needs. Only from V4 up; below that
            // the service returns zero and std keeps its single-thread answer,
            // which ManagedThreadIds.IsPerThread reports honestly.
            if (AppThreads.CurrentThreadId() != 0)
                ManagedThreadIds.Install(&CurrentThreadId);

            return true;
        }

        private static int CurrentThreadId() => AppThreads.CurrentThreadId();

        private static bool Spawn(ThreadBackend.ThreadEntry entry)
        {
            int next = (s_tail + 1) % Capacity;
            if (next == s_head) return false;      // queue full

            s_queue[s_tail] = entry;
            s_tail = next;

            if (AppThreads.Spawn(&ThreadThunk)) return true;

            // Nobody will collect it now, so take it back rather than leaving
            // the queue one longer after every failure.
            s_tail = (s_tail - 1 + Capacity) % Capacity;
            s_queue[s_tail] = null;
            return false;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void ThreadThunk()
        {
            ThreadBackend.ThreadEntry? entry = null;
            if (s_head != s_tail)
            {
                entry = s_queue[s_head];
                s_queue[s_head] = null;
                s_head = (s_head + 1) % Capacity;
            }

            if (entry != null) entry();
        }
    }
}
