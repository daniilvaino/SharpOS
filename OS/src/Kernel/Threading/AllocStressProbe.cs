using OS.Hal;
using OS.Hal.Timer;
using OS.Kernel.Memory;

namespace OS.Kernel.Threading
{
    // Phase E6 acceptance gate -- 4 cooperative worker threads each
    // perform Iterations cycles of (Alloc + write own ThreadId pattern +
    // Yield + verify pattern intact + Free). The Yield between write
    // and verify lets siblings run between the two halves of every
    // critical section so the test stresses the KernelHeap lock under
    // realistic interleaving.
    //
    // Per docs/threading-architecture.md sec15 the acceptance figure is
    // 4 × 10000. On cooperative single-CPU each KernelHeap.Alloc is
    // logically atomic (no Yield inside the locked region), so the
    // "no corruption" guarantee here is trivial against today's
    // execution model -- the meaningful coverage is (a) the lock paths
    // execute at all, (b) thread interleavings against a shared
    // allocator do not surface a missing guard, (c) the stress provides
    // a forward-compat baseline once preemption / SMP land.
    internal static unsafe class AllocStressProbe
    {
        private const int WorkerCount = 4;
        private const int Iterations = 10000;
        private const uint BlockSize = 16;

        private static long s_workersDone;
        private static long s_corruptionDetected;
        private static long s_allocFailed;
        private static long s_totalAllocs;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WorkerEntry()
        {
            Thread? me = Scheduler.Current;
            if (me == null) { Scheduler.Exit(); return; }
            ulong tag = ((ulong)me.Id << 32) | 0xA110_C8E5UL;

            for (int i = 0; i < Iterations; i++)
            {
                void* raw = KernelHeap.Alloc(BlockSize);
                if (raw == null) { s_allocFailed++; break; }
                s_totalAllocs++;

                ulong* p = (ulong*)raw;
                p[0] = tag;
                p[1] = tag ^ (ulong)i;

                // Yield between write and verify so siblings can hammer
                // the allocator in between. Single-CPU cooperative so
                // they can't actually overwrite OUR block (a correct
                // allocator gives each thread distinct blocks), but the
                // yield surfaces any cross-block bookkeeping bugs.
                Scheduler.Yield();

                if (p[0] != tag || p[1] != (tag ^ (ulong)i))
                    s_corruptionDetected++;

                KernelHeap.Free(raw);
            }

            s_workersDone++;
            Scheduler.Exit();
        }

        public static void Run()
        {
            Log.Write(LogLevel.Info, "alloc stress probe start");

            if (!Scheduler.Init())
            {
                Log.Write(LogLevel.Warn, "alloc stress probe: Scheduler.Init failed");
                return;
            }
            if (!Hpet.IsInitialized)
            {
                Log.Write(LogLevel.Warn, "alloc stress probe: HPET not initialised - skipped");
                return;
            }

            s_workersDone = 0;
            s_corruptionDetected = 0;
            s_allocFailed = 0;
            s_totalAllocs = 0;

            int spawned = 0;
            for (int i = 0; i < WorkerCount; i++)
            {
                if (Scheduler.Spawn(&WorkerEntry, 0) != null) spawned++;
            }
            if (spawned != WorkerCount)
            {
                Log.Begin(LogLevel.Warn);
                Console.Write("alloc stress probe: only spawned ");
                Console.WriteInt(spawned);
                Console.Write("/");
                Console.WriteInt(WorkerCount);
                Log.EndLine();
                return;
            }

            // 30-second HPET budget. Each iteration triggers two yields
            // (Sleep-free, fast-path), four workers ~ 80k yields total.
            // At ~1us per fast yield + alloc/free this is ~80ms of real
            // work; 30s budget is huge slack.
            ulong freq = Hpet.FrequencyHz;
            ulong deadline = Hpet.ReadCounter() + 30UL * freq;
            while (s_workersDone < WorkerCount && Hpet.ReadCounter() < deadline)
                Scheduler.Yield();
            bool timedOut = (s_workersDone < WorkerCount);

            bool ok = !timedOut &&
                      (s_corruptionDetected == 0) &&
                      (s_allocFailed == 0) &&
                      (s_totalAllocs == (long)WorkerCount * Iterations);

            Log.Begin(ok ? LogLevel.Info : LogLevel.Warn);
            Console.Write("alloc stress probe: workers=");
            Console.WriteInt((int)s_workersDone);
            Console.Write("/");
            Console.WriteInt(WorkerCount);
            Console.Write(" allocs=");
            Console.WriteInt((int)s_totalAllocs);
            Console.Write("/");
            Console.WriteInt(WorkerCount * Iterations);
            Console.Write(" corruption=");
            Console.WriteInt((int)s_corruptionDetected);
            Console.Write(" allocFail=");
            Console.WriteInt((int)s_allocFailed);
            Console.Write(ok ? " -- ok" : " -- FAIL");
            Log.EndLine();
        }
    }
}
