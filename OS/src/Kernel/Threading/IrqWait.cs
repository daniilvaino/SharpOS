using OS.Hal;

namespace OS.Kernel.Threading
{
    // A driver thread asleep until its device interrupts (plan §11, stage 2).
    //
    // The interrupt handler cannot wake the thread itself: the scheduler's
    // queues are guarded by Preemption.Suppress, which defers a switch but
    // does not block an interrupt, so a handler arriving inside
    // EnqueueRunnable would edit the queue under it. The handler only sets
    // its source's bit in s_pending (Post); the scheduler takes the whole
    // word with one xchg on its next pass (Deliver, from DequeueRunnable) and
    // wakes the sleeper by the ordinary path. The pass after an interrupt
    // comes at once when the CPU was halted — HaltUntilInterrupt returns into
    // Yield's loop — and at the next yield or preemption tick otherwise.
    //
    // One sleeper per source: a driver serialises its own commands.
    internal static unsafe class IrqWait
    {
        public const int MaxSources = 8;

        private static ulong s_pending;
        private static Thread?[]? s_sleepers;

        /// <summary>Waits that went to sleep; wakes handed over by Deliver.</summary>
        public static ulong Sleeps, Wakes;

        /// <summary>Interrupt handlers only: marks the source for the next scheduler pass.</summary>
        public static void Post(int source) => s_pending |= 1UL << source;

        /// <summary>Made before the first source is wired: handlers never allocate.</summary>
        public static void Init() => s_sleepers ??= new Thread?[MaxSources];

        /// <summary>From the scheduler: wakes the sleepers of every source posted since the last pass.</summary>
        public static void Deliver()
        {
            if (s_pending == 0) return;
            ulong bits;
            fixed (ulong* p = &s_pending) bits = X64Asm.Xchg64(p, 0UL);
            for (int i = 0; bits != 0; i++, bits >>= 1)
            {
                if ((bits & 1) == 0) continue;
                Thread? t = s_sleepers![i];
                if (t == null) continue;
                Wakes++;
                Scheduler.WakeFromWait(t);
            }
        }

        /// <summary>
        /// Sleeps until <paramref name="source"/> interrupts or the HPET reaches
        /// <paramref name="deadline"/>, unless <paramref name="done"/> already
        /// holds. Says nothing about which: the caller checks its own condition
        /// and its deadline again. Without a scheduler or a clock it only yields.
        /// </summary>
        /// <remarks>
        /// Registering, going to sleep and asking <paramref name="done"/> are one
        /// step under suppression. An interrupt in between only posts; the bit
        /// stays for the pass that follows, and by then the thread is asleep and
        /// registered, so that pass wakes it. Asked before registering, a
        /// completion landing in the gap would be missed until the deadline.
        /// </remarks>
        public static void Sleep(int source, delegate*<int, bool> done, ulong deadline)
        {
            Thread? cur = Scheduler.Current;

            // Inside an interrupt handler: neither sleep nor yield, the caller
            // polls as the drivers did before. The interrupted thread may be
            // marked Waiting already (a tick inside Scheduler.Sleep), and a
            // yield from here halts it with the tick unacknowledged — nothing
            // ever wakes it (step210, a sampler line flushed to the boot log
            // from the timer handler).
            if (cur != null && cur.InterruptDepth != 0) return;

            // Inside a section that suppresses preemption: same. The caller
            // relies on nobody else running until it is done (an app's
            // collection stops the world this way), and a sleep here would
            // let the world go on behind its back.
            if (Preemption.Depth != 0) return;

            if (cur == null || deadline == 0 || s_sleepers == null)
            {
                Scheduler.Yield();
                return;
            }

            Preemption.Suppress();
            s_sleepers[source] = cur;
            cur.State = ThreadState.Waiting;
            TimerQueue.Schedule(cur, deadline);
            // After Schedule, which marks the wait a timer's: a driver's wait
            // is not cut short by RequestKill — the command finishes first.
            cur.Wait.Kind = WaitKind.Irq;
            if (done(source))
            {
                cur.State = ThreadState.Running;
                Finish(source, cur);
                Preemption.Allow();
                return;
            }
            Sleeps++;
            Preemption.Allow();

            Scheduler.Yield();

            Preemption.Suppress();
            Finish(source, cur);
            Preemption.Allow();
        }

        private static void Finish(int source, Thread cur)
        {
            s_sleepers![source] = null;
            TimerQueue.Cancel(cur);
            cur.Wait.Kind = WaitKind.None;
        }
    }
}
