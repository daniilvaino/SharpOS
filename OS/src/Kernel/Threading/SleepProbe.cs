using OS.Hal;
using OS.Hal.Timer;

namespace OS.Kernel.Threading
{
    // Phase E5 gate — Scheduler.Sleep(ms) accuracy.
    // Spawns one worker that performs 3 × Sleep(50ms), records the
    // HPET delta around each sleep, and the probe asserts every delta
    // falls inside [45 ms, 80 ms]. Lower bound enforces "we did sleep";
    // upper bound is loose to absorb yield-loop latency until an
    // IRQ-driven HPET wake lands in a later phase (no real preemption
    // yet, so the deadline is checked only on yield visits — typically
    // sub-millisecond, but tolerant of a slower spin if a future change
    // adds work between yields).
    internal static unsafe class SleepProbe
    {
        private const int Iterations = 3;
        private const uint SleepMs = 50;
        private const long LowerMs = 45;
        private const long UpperMs = 80;

        private static long s_doneFlag;
        private static long s_passCount;
        private static long s_failCount;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WorkerEntry()
        {
            ulong freq = Hpet.FrequencyHz;
            ulong ticksPerMs = freq == 0 ? 1 : freq / 1000;

            for (int i = 0; i < Iterations; i++)
            {
                ulong t0 = Hpet.ReadCounter();
                Scheduler.Sleep(SleepMs);
                ulong t1 = Hpet.ReadCounter();
                long elapsedMs = (long)((t1 - t0) / ticksPerMs);

                Log.Begin(LogLevel.Info);
                Console.Write("  sleep iter ");
                Console.WriteInt(i);
                Console.Write(" elapsed=");
                Console.WriteInt((int)elapsedMs);
                Console.Write(" ms");
                Log.EndLine();

                if (elapsedMs >= LowerMs && elapsedMs <= UpperMs)
                    s_passCount++;
                else
                    s_failCount++;
            }

            s_doneFlag = 1;
            Scheduler.Exit();
        }

        public static void Run()
        {
            Log.Write(LogLevel.Info, "sleep probe start");

            if (!Scheduler.Init())
            {
                Log.Write(LogLevel.Warn, "sleep probe: Scheduler.Init failed");
                return;
            }
            if (!Hpet.IsInitialized)
            {
                Log.Write(LogLevel.Warn, "sleep probe: HPET not initialised - skipped");
                return;
            }

            s_doneFlag = 0;
            s_passCount = 0;
            s_failCount = 0;

            Thread? t = Scheduler.Spawn(&WorkerEntry, 0);
            if (t == null)
            {
                Log.Write(LogLevel.Warn, "sleep probe: Spawn failed");
                return;
            }

            // Yield until the worker has finished its 3 iterations OR
            // 1 second of HPET time passes. Yield-counter safety is the
            // wrong dial here — main's tight Yield loop with no Runnable
            // can do ~50k yields per 50ms sleep on this hardware, so a
            // fixed yield cap depends on probe timing in a way that
            // sneaks below 3 × 50ms. HPET-based timeout is platform-
            // proportional.
            ulong freq = Hpet.FrequencyHz;
            ulong timeoutDeadline = Hpet.ReadCounter() + 1UL * freq;   // 1 second budget
            while (s_doneFlag == 0 && Hpet.ReadCounter() < timeoutDeadline)
                Scheduler.Yield();
            bool timedOut = (s_doneFlag == 0);

            bool ok = (s_passCount == Iterations) && (s_failCount == 0) && !timedOut;
            Log.Begin(ok ? LogLevel.Info : LogLevel.Warn);
            Console.Write("sleep probe: pass=");
            Console.WriteInt((int)s_passCount);
            Console.Write("/");
            Console.WriteInt(Iterations);
            Console.Write(" fail=");
            Console.WriteInt((int)s_failCount);
            Console.Write(ok ? " -- ok" : " -- FAIL");
            Log.EndLine();
        }
    }
}
