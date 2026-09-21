namespace OS.Kernel.Diagnostics
{
    // Where every thread is parked, printed on demand.
    //
    // Written after three hangs on the rig came back with nothing to look at.
    // The sampling profiler cannot answer this one by construction: it records
    // where the CPU was, and a hung machine that is WAITING has its CPU in the
    // idle halt — a profile of that is a profile of nothing. Worse, the
    // sampler suppresses its own report when the window was spent idle, so a
    // waiting hang is exactly the case it stays quiet for.
    //
    // What is missing is not where the CPU is but where the THREADS are, and
    // that is recorded already: a parked thread's resume address sits in its
    // saved context block, at the layout CoopSwitch produces —
    //   SavedRsp + 0..56  callee-saved GPRs, r15 first
    //   SavedRsp + 64     return address, where the thread resumes
    //   SavedRsp + 72     the stack pointer it resumes with
    // (see KernelGcPreciseWalk.RunFromParkedThread, which reads the same
    // layout for the collector).
    //
    // Addresses, not names: there is no symbol table in the image. Feed them
    // to tools/symbolize.ps1 against the image the ib says.
    internal static unsafe class ThreadDump
    {
        public static void Print(string reason)
        {
            OS.Hal.Console.Write("[threads] ");
            OS.Hal.Console.Write(reason);
            OS.Hal.Console.Write(" current=");
            OS.Kernel.Threading.Thread? cur = OS.Kernel.Threading.Scheduler.Current;
            OS.Hal.Console.WriteUIntRaw(cur == null ? 0u : (uint)cur.Id);
            OS.Hal.Console.WriteLine("");

            OS.Kernel.Threading.Thread? t = OS.Kernel.Threading.Scheduler.AllThreads;
            int printed = 0;
            while (t != null && printed < 64)
            {
                OS.Hal.Console.Write("[threads]   id=");
                OS.Hal.Console.WriteUIntRaw((uint)t.Id);
                OS.Hal.Console.Write(" state=");
                OS.Hal.Console.WriteUIntRaw((uint)t.State);
                if (t == cur) OS.Hal.Console.Write(" <-current");

                OS.Hal.Console.Write(" wait=");
                OS.Hal.Console.WriteUIntRaw((uint)t.Wait.Kind);

                // The current thread's saved context is where it was LAST
                // switched out — stale in general, but exactly right for the
                // case this exists to catch: a thread the scheduler parked and
                // never got back to. Printed with a mark rather than withheld,
                // because withholding it hid the only thread that mattered.
                byte* ctx = t.ContextBlock;
                if (ctx != null)
                {
                    ulong savedRsp = *(ulong*)ctx;
                    if (savedRsp != 0)
                    {
                        OS.Hal.Console.Write(" resume=0x");
                        OS.Hal.Console.WriteHexRaw(*(ulong*)(savedRsp + 64), 16);
                        OS.Hal.Console.Write(" rsp=0x");
                        OS.Hal.Console.WriteHexRaw(savedRsp + 72, 16);
                        if (t == cur) OS.Hal.Console.Write(" (stale)");
                    }
                }

                OS.Hal.Console.WriteLine("");
                printed++;
                t = t.AllNext;
            }
        }
    }
}
