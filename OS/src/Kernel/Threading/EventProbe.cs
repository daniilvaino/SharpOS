using OS.Hal;
using OS.Hal.Timer;

namespace OS.Kernel.Threading
{
    // Phase E5 gate — Event.Wait/Set round trip.
    // Main spawns a waiter that calls Wait() on a manual-reset event,
    // then sleeps for ~30 ms, then Set()s the event. The probe captures
    // (set time) and (wake time) and asserts the wake latency is inside
    // a generous bound — same yield-loop reasoning as SleepProbe.
    // Probes both correctness (waiter unblocks) and latency (Set ->
    // resumed Wait < tolerance).
    internal static unsafe class EventProbe
    {
        // Manual-reset event allocated in Run() and stashed here so the
        // [UnmanagedCallersOnly] worker can reach it without a captured
        // closure (no managed delegate state in the trampoline).
        private static Event? s_event;
        private static long s_setTicks;
        private static long s_wakeTicks;
        private static long s_workerDone;

        private const uint SleepBeforeSetMs = 30;
        private const long LatencyUpperMs = 30;   // generous bound for spin-yield wake

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WaiterEntry()
        {
            Event? e = s_event;
            if (e == null) { Scheduler.Exit(); return; }
            e.Wait();
            s_wakeTicks = (long)Hpet.ReadCounter();
            s_workerDone = 1;
            Scheduler.Exit();
        }

        public static void Run()
        {
            Log.Write(LogLevel.Info, "event probe start");

            if (!Scheduler.Init())
            {
                Log.Write(LogLevel.Warn, "event probe: Scheduler.Init failed");
                return;
            }
            if (!Hpet.IsInitialized)
            {
                Log.Write(LogLevel.Warn, "event probe: HPET not initialised - skipped");
                return;
            }

            s_event = new Event(manualReset: true, initialState: false);
            s_setTicks = 0;
            s_wakeTicks = 0;
            s_workerDone = 0;

            Thread? worker = Scheduler.Spawn(&WaiterEntry, 0);
            if (worker == null)
            {
                Log.Write(LogLevel.Warn, "event probe: Spawn failed");
                return;
            }

            // Let the waiter run + reach Wait() (it transitions to Waiting
            // and yields back to us).
            Scheduler.Yield();

            // Sleep ~30 ms, then fire Set.
            Scheduler.Sleep(SleepBeforeSetMs);
            s_setTicks = (long)Hpet.ReadCounter();
            s_event.Set();

            // Yield until the waiter logged its wake time and Exit'd
            // (HPET-based 1-second timeout for the same reasoning as
            // SleepProbe — yield-count budgets sneak below real time).
            ulong freq = Hpet.FrequencyHz;
            ulong waitDeadline = Hpet.ReadCounter() + 1UL * freq;
            while (s_workerDone == 0 && Hpet.ReadCounter() < waitDeadline)
                Scheduler.Yield();

            ulong ticksPerMs = freq == 0 ? 1 : freq / 1000;
            long latencyMs = 0;
            if (s_wakeTicks > 0 && s_setTicks > 0)
                latencyMs = (long)(((ulong)s_wakeTicks - (ulong)s_setTicks) / ticksPerMs);

            bool ok = (s_workerDone != 0) &&
                      (latencyMs >= 0) && (latencyMs <= LatencyUpperMs);

            Log.Begin(ok ? LogLevel.Info : LogLevel.Warn);
            Console.Write("event probe: latency=");
            Console.WriteInt((int)latencyMs);
            Console.Write(" ms (set->wake)");
            Console.Write(ok ? " -- ok" : " -- FAIL");
            Log.EndLine();
        }
    }
}
