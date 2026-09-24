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
    //
    // Written to the DIAGNOSTICS channel, never to ordinary output. The first
    // version used Console, which paints, and on the rig that showed up as a
    // launcher redrawing itself to death: every ten seconds nine lines of
    // this landed on top of the interface, and Terminal.Gui repainted the
    // whole screen to erase them. A full repaint costs 685 us per output call
    // on that machine. The measurement was the slowdown it was measuring.
    //
    // OutputChannel.Perf exists for exactly this (step175) and every other
    // diagnostic already used it; this one was the exception.
    internal static unsafe class ThreadDump
    {
        public static void Print(string reason)
        {
            Put("[threads] ");
            Put(reason);
            Put(" current=");
            OS.Kernel.Threading.Thread? cur = OS.Kernel.Threading.Scheduler.Current;
            PutUInt(cur == null ? 0u : (uint)cur.Id);
            Put("\r\n");

            OS.Kernel.Threading.Thread? t = OS.Kernel.Threading.Scheduler.AllThreads;
            int printed = 0;
            while (t != null && printed < 64)
            {
                Put("[threads]   id=");
                PutUInt((uint)t.Id);
                Put(" state=");
                PutUInt((uint)t.State);
                if (t == cur) Put(" <-current");

                Put(" wait=");
                PutUInt((uint)t.Wait.Kind);

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
                        Put(" resume=0x");
                        PutHex(*(ulong*)(savedRsp + 64), 16);
                        Put(" rsp=0x");
                        PutHex(savedRsp + 72, 16);
                        if (t == cur) Put(" (stale)");
                    }
                }

                Put("\r\n");
                printed++;
                t = t.AllNext;
            }
        }

        // Straight to the channel, a character at a time: this runs from the
        // sampler on a timer tick, and a formatter that allocates would ask
        // the heap from inside an interrupt.
        private static void Put(string text)
        {
            for (int i = 0; i < text.Length; i++)
                OS.Hal.Platform.WriteChar(text[i], OS.Hal.OutputChannel.Perf);
        }

        private static void PutUInt(uint value)
        {
            char* digits = stackalloc char[10];
            int count = 0;
            do
            {
                digits[count++] = (char)('0' + (int)(value % 10u));
                value /= 10u;
            }
            while (value != 0);

            while (count > 0)
                OS.Hal.Platform.WriteChar(digits[--count], OS.Hal.OutputChannel.Perf);
        }

        private static void PutHex(ulong value, int digits)
        {
            const string table = "0123456789ABCDEF";
            for (int shift = (digits - 1) * 4; shift >= 0; shift -= 4)
                OS.Hal.Platform.WriteChar(table[(int)((value >> shift) & 0xF)],
                                          OS.Hal.OutputChannel.Perf);
        }
    }
}
