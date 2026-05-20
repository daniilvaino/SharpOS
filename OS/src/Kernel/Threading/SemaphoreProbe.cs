using OS.Hal;
using OS.Hal.Timer;

namespace OS.Kernel.Threading
{
    // Phase E5 gate — Semaphore Wait + Release(n) wakes the right
    // number of waiters. Spawns 3 waiters on a 0-count Semaphore,
    // main Release(2), expects 2 of 3 to wake. Then Release(1),
    // expects the last to wake. Final state: Count = 0, no waiters
    // pending.
    internal static unsafe class SemaphoreProbe
    {
        private static Semaphore? s_sem;
        private static long s_wakeCount;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WaiterEntry()
        {
            Semaphore? s = s_sem;
            if (s == null) { Scheduler.Exit(); return; }
            s.Wait();
            // Single-CPU cooperative — only one woken waiter runs at
            // a time, so plain increment is race-free.
            s_wakeCount++;
            Scheduler.Exit();
        }

        public static void Run()
        {
            Log.Write(LogLevel.Info, "semaphore probe start");

            if (!Scheduler.Init())
            {
                Log.Write(LogLevel.Warn, "semaphore probe: Scheduler.Init failed");
                return;
            }
            if (!Hpet.IsInitialized)
            {
                Log.Write(LogLevel.Warn, "semaphore probe: HPET not initialised - skipped");
                return;
            }

            s_sem = new Semaphore(initialCount: 0);
            s_wakeCount = 0;

            Thread? a = Scheduler.Spawn(&WaiterEntry, 0);
            Thread? b = Scheduler.Spawn(&WaiterEntry, 0);
            Thread? c = Scheduler.Spawn(&WaiterEntry, 0);
            if (a == null || b == null || c == null)
            {
                Log.Write(LogLevel.Warn, "semaphore probe: Spawn failed");
                return;
            }

            // Let all three waiters reach Wait() and block.
            Scheduler.Yield();
            Scheduler.Yield();
            Scheduler.Yield();

            // Release 2 — exactly 2 should wake and increment s_wakeCount.
            s_sem.Release(2);

            // Wait up to 200 ms for the two waiters to log + Exit.
            ulong freq = Hpet.FrequencyHz;
            ulong deadline1 = Hpet.ReadCounter() + freq / 5UL;   // 200 ms
            while (s_wakeCount < 2 && Hpet.ReadCounter() < deadline1)
                Scheduler.Yield();
            long countAfterFirstRelease = s_wakeCount;

            // Release 1 — the last waiter wakes.
            s_sem.Release(1);
            ulong deadline2 = Hpet.ReadCounter() + freq / 5UL;   // 200 ms
            while (s_wakeCount < 3 && Hpet.ReadCounter() < deadline2)
                Scheduler.Yield();
            long countAfterSecondRelease = s_wakeCount;

            bool ok = (countAfterFirstRelease == 2) &&
                      (countAfterSecondRelease == 3) &&
                      (s_sem.Count == 0);

            Log.Begin(ok ? LogLevel.Info : LogLevel.Warn);
            Console.Write("semaphore probe: release(2)->wake=");
            Console.WriteInt((int)countAfterFirstRelease);
            Console.Write(" release(1)->wake=");
            Console.WriteInt((int)countAfterSecondRelease);
            Console.Write(" residualCount=");
            Console.WriteInt(s_sem.Count);
            Console.Write(ok ? " -- ok" : " -- FAIL");
            Log.EndLine();
        }
    }
}
