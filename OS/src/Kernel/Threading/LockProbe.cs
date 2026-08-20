using OS.Hal;
using System.Threading;
using System.Threading.Tasks;

namespace OS.Kernel.Threading
{
    // Do locks actually lock?
    //
    // Three things have to hold, and each one fails silently on its own:
    //
    //   1. compare-and-swap is a real instruction, not three managed
    //      statements a timer tick can split;
    //   2. threads have distinct ids — with one id shared by all, a reentrant
    //      lock lets everyone straight in and mutual exclusion quietly becomes
    //      none at all;
    //   3. two threads hammering a counter under `lock` lose nothing.
    //
    // The third is the one that matters, and it is also the one that passes for
    // the wrong reason if preemption happens to be off: no switch inside the
    // critical section means no chance to interleave. So the counter test runs
    // with preemption enabled, and says so.
    internal static class LockProbe
    {
        private const int Iterations = 20000;

        // No initialiser: a static field with one gives this type a class
        // constructor, whose check does not work here (limits §1).
        private static object s_gate = null!;
        private static int s_guarded;
        private static int s_interlocked;
        private static int s_firstId;
        private static int s_secondId;

        public static void Run()
        {
            Console.Write("[lock] ");

            if (!ThreadBackend.IsAvailable)
            {
                Console.WriteLine("SKIP no thread backend installed");
                return;
            }

            bool ok = AtomicsAreReal() && ThreadIdsDiffer() && LockExcludes();
            Console.WriteLine(ok ? " PASS" : " FAIL");
        }

        private static bool AtomicsAreReal()
        {
            bool ok = Interlocked.IsAtomic;
            Console.Write("cas=");
            Console.Write(ok ? "real " : "MANAGED-FALLBACK ");
            return ok;
        }

        // Cheap, and it catches the difference between "ids are per thread" and
        // "the thread-static is not honoured and everyone shares one".
        private static bool ThreadIdsDiffer()
        {
            s_firstId = ManagedThreadIds.Current;
            s_secondId = 0;

            Task t = Task.Run(() => { s_secondId = ManagedThreadIds.Current; });
            t.Wait();

            bool ok = s_secondId != 0 && s_secondId != s_firstId;
            Console.Write("ids=");
            Console.WriteUInt((uint)s_firstId);
            Console.Write("/");
            Console.WriteUInt((uint)s_secondId);
            Console.Write(ok ? " " : " FAIL ");
            return ok;
        }

        private static bool LockExcludes()
        {
            s_gate = new object();
            s_guarded = 0;
            s_interlocked = 0;

            bool preemptionWasOff = !Preemption.IsEnabled;
            if (preemptionWasOff) Preemption.Enable();

            Task a = Task.Run(Hammer);
            Task b = Task.Run(Hammer);
            a.Wait();
            b.Wait();

            if (preemptionWasOff) Preemption.Disable();

            int expected = Iterations * 2;
            bool lockOk = s_guarded == expected;
            bool casOk = s_interlocked == expected;

            Console.Write("lock=");
            Console.WriteUInt((uint)s_guarded);
            Console.Write("/");
            Console.WriteUInt((uint)expected);
            if (!lockOk) Console.Write(" FAIL");

            Console.Write(" incr=");
            Console.WriteUInt((uint)s_interlocked);
            if (!casOk) Console.Write(" FAIL");

            return lockOk && casOk;
        }

        private static void Hammer()
        {
            for (int i = 0; i < Iterations; i++)
            {
                // Read and write as separate statements on purpose: this is the
                // update that goes missing when the lock does not hold.
                lock (s_gate)
                {
                    int current = s_guarded;
                    s_guarded = current + 1;
                }

                Interlocked.Increment(ref s_interlocked);
            }
        }
    }
}
