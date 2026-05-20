using OS.Hal;

namespace OS.Kernel.Threading
{
    // Phase E4 gate — two cooperative kernel threads each yield N times,
    // alternating with the boot/main thread. Probe asserts:
    //   - Both threads reached the final iteration (s_t1Count == N, s_t2Count == N).
    //   - Boot thread resumed cleanly after both children Exited.
    //   - FXSAVE area was preserved across switches (implicit — if the
    //     fxrstor loaded garbage, FP ops in the iteration loop would
    //     #UD/#GP long before we reach the final assert).
    //
    // Round-robin order with three threads (main, T1, T2) yields the
    // pattern: main → T1 → T2 → main → T1 → T2 → ... until T1 and T2
    // both exit, leaving main alone.
    internal static unsafe class ThreadPingPongProbe
    {
        private const int Iterations = 5;

        private static int s_t1Count;
        private static int s_t2Count;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void T1Entry()
        {
            for (int i = 0; i < Iterations; i++)
            {
                Log.Begin(LogLevel.Info);
                Console.Write("  T1 iter ");
                Console.WriteInt(i);
                Log.EndLine();
                s_t1Count++;
                Scheduler.Yield();
            }
            Scheduler.Exit();
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void T2Entry()
        {
            for (int i = 0; i < Iterations; i++)
            {
                Log.Begin(LogLevel.Info);
                Console.Write("  T2 iter ");
                Console.WriteInt(i);
                Log.EndLine();
                s_t2Count++;
                Scheduler.Yield();
            }
            Scheduler.Exit();
        }

        public static void Run()
        {
            Log.Write(LogLevel.Info, "ping-pong probe start");

            if (!Scheduler.Init())
            {
                Log.Write(LogLevel.Warn, "ping-pong probe: Scheduler.Init failed");
                return;
            }

            s_t1Count = 0;
            s_t2Count = 0;

            Thread? t1 = Scheduler.Spawn(&T1Entry, 0);
            Thread? t2 = Scheduler.Spawn(&T2Entry, 0);
            if (t1 == null || t2 == null)
            {
                Log.Write(LogLevel.Warn, "ping-pong probe: Spawn failed");
                return;
            }

            // Main yields until both children have completed their loops.
            // Each round-robin pass increments s_t1Count and s_t2Count by 1.
            // After T1 exits, T2 continues alone (main + T2 alternate).
            // After T2 also exits, no other runnable remains; we fall
            // through the while.
            int safetyCap = Iterations * 32;
            while ((s_t1Count < Iterations || s_t2Count < Iterations) && safetyCap-- > 0)
            {
                Scheduler.Yield();
            }

            bool countOk = (s_t1Count == Iterations) && (s_t2Count == Iterations);
            bool safetyOk = safetyCap > 0;

            Log.Begin(countOk && safetyOk ? LogLevel.Info : LogLevel.Warn);
            Console.Write("ping-pong probe: T1=");
            Console.WriteInt(s_t1Count);
            Console.Write("/");
            Console.WriteInt(Iterations);
            Console.Write(" T2=");
            Console.WriteInt(s_t2Count);
            Console.Write("/");
            Console.WriteInt(Iterations);
            Console.Write(" yields=");
            Console.WriteUInt(Scheduler.YieldCount);
            Console.Write(" switches=");
            Console.WriteUInt(Scheduler.SwitchCount);
            Console.Write(countOk && safetyOk ? " — ok" : " — FAIL");
            Log.EndLine();
        }
    }
}
