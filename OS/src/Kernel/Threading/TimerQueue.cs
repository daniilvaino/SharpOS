namespace OS.Kernel.Threading
{
    // Phase E5 — deadline queue for Thread.Sleep / timed waits. Singly-
    // linked, sorted by DeadlineTicks ascending. Insertion is O(N) which
    // is fine while the system has only a handful of waiting threads;
    // revisit with a heap when ThreadPool / Task land in E10.
    //
    // The HPET counter (Hpet.ReadCounter) is the monotonic time base.
    // Ticks are converted to/from milliseconds via Hpet.FrequencyHz at
    // the call site (Scheduler.Sleep / Event.WaitFor).
    //
    // Threading model — single CPU + cooperative. Callers run with
    // interrupts conceptually on (no IRQ-driven HPET wake yet; Yield
    // drains the queue on every visit). No locks needed; the queue is
    // touched only inside Scheduler.Yield / Sleep / Wake helpers, which
    // by construction run one-at-a-time on the single CPU.
    internal static unsafe class TimerQueue
    {
        private static Thread? s_head;

        // Insert `t` sorted by `deadlineTicks` ascending. Caller has
        // already marked `t.State = Waiting` and set `t.DeadlineTicks`.
        public static void Schedule(Thread t, ulong deadlineTicks)
        {
            t.TimerNext = null;
            t.DeadlineTicks = deadlineTicks;

            if (s_head == null || deadlineTicks < s_head.DeadlineTicks)
            {
                t.TimerNext = s_head;
                s_head = t;
                return;
            }

            Thread? prev = s_head;
            while (prev.TimerNext != null && prev.TimerNext.DeadlineTicks <= deadlineTicks)
                prev = prev.TimerNext;

            t.TimerNext = prev.TimerNext;
            prev.TimerNext = t;
        }

        // Pop every entry whose deadline has elapsed. Each popped thread
        // is handed to `Scheduler.WakeFromWait` so it gets transitioned
        // Waiting -> Runnable and enqueued. Returns count moved.
        // (Direct dependency rather than callback to avoid pulling in a
        // managed delegate type — std/no-runtime has no Action<T>.)
        public static uint DrainExpired(ulong nowTicks)
        {
            uint moved = 0;
            while (s_head != null && s_head.DeadlineTicks <= nowTicks)
            {
                Thread t = s_head;
                s_head = t.TimerNext;
                t.TimerNext = null;
                t.DeadlineTicks = 0;
                Scheduler.WakeFromWait(t);
                moved++;
            }
            return moved;
        }

        // Cancel a thread's pending deadline (e.g., Event.Set fires while
        // the waiter also had a timeout). Returns true if found+removed.
        public static bool Cancel(Thread t)
        {
            if (s_head == null) return false;
            if (s_head == t)
            {
                s_head = t.TimerNext;
                t.TimerNext = null;
                t.DeadlineTicks = 0;
                return true;
            }
            Thread? prev = s_head;
            while (prev.TimerNext != null)
            {
                if (prev.TimerNext == t)
                {
                    prev.TimerNext = t.TimerNext;
                    t.TimerNext = null;
                    t.DeadlineTicks = 0;
                    return true;
                }
                prev = prev.TimerNext;
            }
            return false;
        }

        // Diagnostic — earliest deadline or 0 if queue empty.
        public static ulong NextDeadline()
        {
            return s_head == null ? 0UL : s_head.DeadlineTicks;
        }
    }
}
