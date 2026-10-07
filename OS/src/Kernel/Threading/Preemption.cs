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

        // A system-wide counter is only a valid critical section while the code
        // inside never gives up the CPU. If it does, one thread's Suppress is
        // paired with another thread's Allow and the state goes wrong in both
        // directions. This counts the violation instead of assuming it away.
        private static ulong s_yieldsWhileSuppressed;
        public static ulong YieldsWhileSuppressed => s_yieldsWhileSuppressed;
        public static uint Depth => s_disableDepth;
        public static void NoteYield() { if (s_disableDepth != 0) s_yieldsWhileSuppressed++; }
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
            if (s_disableDepth == 0 && s_pending != 0) TakeDeferred();
        }

        // A tick that came while switching was suppressed (step196): it is
        // taken when the suppression ends, not lost. Without it a thread that
        // suppresses for most of every quantum — a collector running back to
        // back — kept the CPU: every tick landed inside a critical section.
        private static byte s_pending;
        private static ulong s_deferred;

        /// <summary>Ticks taken late, at the end of a suppression.</summary>
        public static ulong Deferred => s_deferred;

        /// <summary>Where the flag lives, for apps: their critical sections end with a look at it.</summary>
        public static byte* PendingAddress
            => (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_pending);

        /// <summary>
        /// The switch a tick asked for, if one is pending and it can be taken
        /// here: not inside an interrupt handler, not mid-switch, interrupts on.
        /// Otherwise it stays pending for the next chance.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void TakeDeferred()
        {
            if (s_pending == 0 || !s_enabled || s_disableDepth != 0 || Scheduler.SwitchInProgress) return;
            Thread? curr = Scheduler.Current;
            if (curr == null || curr.InterruptDepth != 0 || curr.InPreemptiveSwitch) return;
            if (!X64Asm.InterruptsEnabled()) return;
            s_pending = 0;
            s_deferred++;
            curr.ParkedAtDeferredTick = true;
            Scheduler.Yield();
            curr.ParkedAtDeferredTick = false;
        }

        /// <summary>
        /// Puts the depth back to a value taken earlier. For an app ended
        /// mid-flight: it may have died inside a service that suppressed, and
        /// the matching Allow will never run.
        /// </summary>
        public static void RestoreDepth(uint depth) => s_disableDepth = depth;

        /// <summary>
        /// Whether the running code may wait for another thread: not from
        /// inside an interrupt handler (Thread.InterruptDepth). Installed as
        /// the class-constructor runner's s_canWait.
        /// </summary>
        public static bool CanWait() => (Scheduler.Current?.InterruptDepth ?? 0) == 0;

        /// <summary>
        /// Where the depth lives, for apps: their allocator and collector
        /// suppress by incrementing it directly (AppServiceTable
        /// .PreemptionDepthAddress) rather than through a service call per
        /// allocation. A static in .bss never moves.
        /// </summary>
        public static uint* DepthAddress
            => (uint*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_disableDepth);

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
            if (!s_enabled || Scheduler.SwitchInProgress)
            {
                s_declined++;
                return;
            }
            if (s_disableDepth != 0)
            {
                s_declined++;
                s_pending = 1;
                return;
            }

            Thread? curr = Scheduler.Current;
            if (curr == null) return;

            // A thread of an ending process caught in its app's code: it
            // leaves rather than goes on (step194).
            if (curr.KillRequested && TurnToLeave(curr, frame)) return;

            // The narrow mode: only code of the app now running is switched.
            if (OS.Kernel.Diagnostics.Probes.PreemptAppCodeOnly &&
                !OS.Kernel.Exec.JumpStub.IsAppCode(((OS.Hal.Idt.InterruptFrame*)frame)->Rip))
            {
                s_declined++;
                return;
            }

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

            // Ended while it was switched out: same, on the way back.
            if (curr.KillRequested) TurnToLeave(curr, frame);
        }

        // Points the interrupted app code's return at the exit path: the
        // interrupt returns into LeaveFromPreemption instead of the app. Only
        // app code is turned this way — a thread inside the kernel leaves on
        // the way out of its service, holding nothing.
        private static bool TurnToLeave(Thread curr, void* frame)
        {
            var f = (OS.Hal.Idt.InterruptFrame*)frame;
            if (curr.App == null || !OS.Kernel.Exec.JumpStub.IsAppCode(f->Rip))
                return false;
            delegate* unmanaged<void> leave = &LeaveFromPreemption;
            f->Rip = (ulong)leave;
            f->Rsp = (f->Rsp & ~0xFUL) - 8;      // as after a call: entry alignment
            return true;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void LeaveFromPreemption()
        {
            Thread self = Scheduler.Current;
            OS.Kernel.Exec.JumpStub.LeaveApp(self, self.App.KillCode);
        }
    }
}
