using OS.Hal;

namespace OS.Kernel.Threading
{
    // Preemption: the timer tick takes the CPU away from a thread that never
    // asked to give it up.
    //
    // Deliberately opt-in and off by default. Cooperative scheduling is not a
    // setting in this kernel, it is a load-bearing assumption: KernelHeap runs
    // without a lock, ProcessTable and Event skip synchronisation, every driver
    // polls its device in a loop, and the shellcode patchers rewrite code
    // nobody else is executing. All of that is correct only while a thread
    // cannot be interrupted by another thread. Turning preemption on globally
    // would not "enable a feature", it would invalidate those assumptions
    // everywhere at once.
    //
    // So it is enabled around code that has been shown to tolerate it, and
    // disabled everywhere else. Depth is a counter, not a flag, because the
    // regions nest.
    //
    // Mechanism: the tick handler simply calls Scheduler.Yield(). That reuses
    // the entire cooperative machinery — same context block, same switch stub,
    // same wait queues — instead of introducing a second representation of a
    // suspended thread. It works because the interrupt frame lives on the
    // interrupted thread's own stack: parking mid-handler leaves it there, and
    // resuming returns through it by IRETQ with every register intact.
    internal static unsafe class Preemption
    {
        private static uint s_disableDepth;
        private static bool s_enabled;
        private static ulong s_switches;
        private static ulong s_declined;

        /// <summary>Preemptive switches actually performed.</summary>
        public static ulong Switches => s_switches;

        /// <summary>Ticks that could have switched but were inside a disabled region.</summary>
        public static ulong Declined => s_declined;

        public static bool IsEnabled => s_enabled;

        public static void Enable() => s_enabled = true;

        public static void Disable() => s_enabled = false;

        /// <summary>
        /// Enter a region that must not be interrupted by a thread switch.
        /// System-wide and conservative: it suppresses preemption for whoever
        /// is running, which is what a critical section in the heap or a
        /// driver needs. NOT the mechanism for "this thread is mid-switch" —
        /// that is a per-thread flag, see Thread.InPreemptiveSwitch.
        /// </summary>
        public static void Suppress() => s_disableDepth++;

        public static void Allow()
        {
            if (s_disableDepth > 0) s_disableDepth--;
        }

        /// <summary>
        /// Called from the timer interrupt, after the interrupt has been
        /// acknowledged. Returns having possibly run other threads.
        /// </summary>
        /// <param name="frame">
        /// The interrupt frame, recorded on the thread so the garbage collector
        /// can walk past it. Without that the frames BELOW the interrupt — the
        /// code that was actually running — are invisible: the walker unwinds
        /// managed frames until it reaches the entry shellcode, which carries
        /// no unwind data, and stops there.
        /// </param>
        public static void OnTick(void* frame)
        {
            if (!s_enabled || s_disableDepth != 0)
            {
                s_declined++;
                return;
            }

            Thread? curr = Scheduler.Current;
            if (curr == null) return;

            // Nested ticks must not switch this thread again: we are about to
            // run with interrupts enabled inside an interrupt handler, and a
            // second switch from there would nest the scheduler inside itself.
            //
            // The flag lives on the thread, not in a global counter. A global
            // one stays raised for as long as this thread is parked — which is
            // most of the time — and silently declines every tick for every
            // other thread. Observed: switches=1, declined=28.
            if (curr.InPreemptiveSwitch)
            {
                s_declined++;
                return;
            }

            curr.InPreemptiveSwitch = true;
            curr.PreemptedFrame = frame;

            // Interrupts on before switching, off after.
            //
            // The handler was entered through an interrupt gate, so IF is
            // clear. Switching with it clear would hand the next thread a CPU
            // with interrupts disabled — it would never be preempted itself,
            // and the tick would die after exactly one switch. Enabling here
            // costs a nesting window, which the suppression above bounds.
            X64Asm.Sti();
            s_switches++;
            Scheduler.Yield();
            X64Asm.Cli();

            curr.PreemptedFrame = null;
            curr.InPreemptiveSwitch = false;
        }
    }
}
