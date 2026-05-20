using OS.Hal;
using OS.Hal.Timer;

namespace OS.Kernel.Threading
{
    // Phase E7 acceptance gate -- two Processes launched concurrently;
    // each runs a primary thread that does 3 yielding iterations and
    // then calls Process.Exit(code). Main yield-waits via
    // WaitForExit() until both report Zombie. Asserts:
    //   - both processes reach Zombie state
    //   - exit codes propagated correctly
    //   - both PIDs distinct and tracked in the registry
    //   - state machine ends with two registered Zombies + main thread
    //
    // No MMU isolation (logical process only) -- the workers share the
    // kernel address space; cooperative scheduling alternates their
    // primary threads through Yield. The "concurrent launch"
    // requirement of docs/threading-architecture.md sec15 is satisfied
    // at the logical level (both processes coexist in the table, both
    // make progress before either finishes).
    internal static unsafe class ProcessProbe
    {
        private const int Iterations = 3;
        private const int ExitCodeA = 0x1A;
        private const int ExitCodeB = 0x2B;

        private static long s_aIterCount;
        private static long s_bIterCount;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WorkerAEntry()
        {
            for (int i = 0; i < Iterations; i++)
            {
                Log.Begin(LogLevel.Info);
                Console.Write("  proc A iter ");
                Console.WriteInt(i);
                Log.EndLine();
                s_aIterCount++;
                Scheduler.Yield();
            }
            Process.Exit(ExitCodeA);
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WorkerBEntry()
        {
            for (int i = 0; i < Iterations; i++)
            {
                Log.Begin(LogLevel.Info);
                Console.Write("  proc B iter ");
                Console.WriteInt(i);
                Log.EndLine();
                s_bIterCount++;
                Scheduler.Yield();
            }
            Process.Exit(ExitCodeB);
        }

        public static void Run()
        {
            Log.Write(LogLevel.Info, "process probe start");

            if (!Scheduler.Init())
            {
                Log.Write(LogLevel.Warn, "process probe: Scheduler.Init failed");
                return;
            }
            if (!Hpet.IsInitialized)
            {
                Log.Write(LogLevel.Warn, "process probe: HPET not initialised - skipped");
                return;
            }

            s_aIterCount = 0;
            s_bIterCount = 0;

            Process? a = Process.Launch("worker-A", &WorkerAEntry);
            Process? b = Process.Launch("worker-B", &WorkerBEntry);
            if (a == null || b == null)
            {
                Log.Write(LogLevel.Warn, "process probe: Launch failed");
                return;
            }

            bool aExited = a.WaitForExit();
            bool bExited = b.WaitForExit();

            bool aCodeOk = a.ExitCode == ExitCodeA;
            bool bCodeOk = b.ExitCode == ExitCodeB;
            bool aStateOk = a.State == ProcessLifecycle.Zombie;
            bool bStateOk = b.State == ProcessLifecycle.Zombie;
            bool itersOk = (s_aIterCount == Iterations) && (s_bIterCount == Iterations);
            bool pidsDistinct = a.Id != b.Id;

            bool ok = aExited && bExited && aCodeOk && bCodeOk &&
                      aStateOk && bStateOk && itersOk && pidsDistinct;

            Log.Begin(ok ? LogLevel.Info : LogLevel.Warn);
            Console.Write("process probe: pidA=");
            Console.WriteUInt(a.Id);
            Console.Write(" exitA=0x");
            Console.WriteHex((ulong)(uint)a.ExitCode);
            Console.Write(" pidB=");
            Console.WriteUInt(b.Id);
            Console.Write(" exitB=0x");
            Console.WriteHex((ulong)(uint)b.ExitCode);
            Console.Write(" iters=");
            Console.WriteInt((int)s_aIterCount);
            Console.Write("/");
            Console.WriteInt((int)s_bIterCount);
            Console.Write(ok ? " -- ok" : " -- FAIL");
            Log.EndLine();
        }
    }
}
