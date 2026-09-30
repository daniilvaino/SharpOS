// Kernel-side thread backend for Tasks.
//
// Linked into OS.csproj only — it names the kernel scheduler directly. An app
// gets a different file once thread creation reaches the service table; the Task
// code itself is shared and does not change.
//
// Installed rather than referenced, so std keeps no compile-time dependency on
// the kernel and the layering does not invert.

using System;
using System.Threading;

namespace OS.Kernel.Threading
{
    internal static unsafe class TaskBackendInstaller
    {
        private const uint DefaultStackBytes = 64 * 1024;

        // A queue, not "the slot I just filled".
        //
        // The kernel spawns through a plain function pointer, so the delegate
        // cannot travel with the thread and has to be left somewhere for it to
        // collect. The obvious version — write to a slot, have the new thread
        // read that same slot — assumes the new thread starts before the next
        // spawn happens. Under preemption that is exactly the assumption that
        // fails, and two threads would run the same entry while another never
        // ran at all.
        //
        // A queue removes the assumption instead of documenting it: every entry
        // is independent work, so it does not matter which thread takes which,
        // only that each is taken once.
        private const int Capacity = 64;
        private static ThreadBackend.ThreadEntry?[] s_queue = null!;
        private static int s_head;   // next to take
        private static int s_tail;   // next to fill

        public static void Install()
        {
            if (s_queue == null) s_queue = new ThreadBackend.ThreadEntry?[Capacity];
            ThreadBackend.Install(Spawn, Sleep);

            // Blocking waits. Init first: without the buckets WaitOnAddress
            // returns at once, and every wait in std would become a spin.
            if (AddressWait.Init())
                ThreadBackend.InstallWaits(&AddressWait.WaitOnAddress, &AddressWait.WakeByAddressAll);

            // Thread identity, which Monitor needs and thread-statics cannot
            // give us here: the scheduler owns the threads, so it is the one
            // that can name the running one.
            ManagedThreadIds.Install(&CurrentThreadId);

            // Monitor's side tables, built here rather than on first lock:
            // this runs before there is a second thread, and building them
            // under contention is what made `lock` fault on a null array.
            Monitor.EnsureTables();
        }

        private static int CurrentThreadId()
        {
            Thread? current = Scheduler.Current;
            return current == null ? 1 : current.Id;
        }

        private static bool Spawn(ThreadBackend.ThreadEntry entry)
        {
            Preemption.Suppress();
            int next = (s_tail + 1) % Capacity;
            bool full = next == s_head;
            if (!full)
            {
                s_queue[s_tail] = entry;
                s_tail = next;
            }
            Preemption.Allow();

            if (full) return false;

            if (Scheduler.Spawn(&ThreadThunk, DefaultStackBytes) != null) return true;

            // The thread never started, so nobody will take the entry: put it
            // back rather than leaving a queue that grows by one every failure.
            Preemption.Suppress();
            s_tail = (s_tail - 1 + Capacity) % Capacity;
            s_queue[s_tail] = null;
            Preemption.Allow();
            return false;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void ThreadThunk()
        {
            Preemption.Suppress();
            ThreadBackend.ThreadEntry? entry = null;
            if (s_head != s_tail)
            {
                entry = s_queue[s_head];
                s_queue[s_head] = null;
                s_head = (s_head + 1) % Capacity;
            }
            Preemption.Allow();

            if (entry != null) entry();
            Scheduler.Exit();
        }

        private static void Sleep(uint milliseconds) => Scheduler.Sleep(milliseconds);
    }
}
