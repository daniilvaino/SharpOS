using OS.Hal;
using OS.Hal.Timer;

namespace OS.Kernel.Threading
{
    // Does the timer take the CPU away from a thread that never gives it up?
    //
    // That is the whole definition of preemption, so the probe is built around
    // it: the worker contains no Yield, no Sleep, no wait — nothing but a
    // counting loop. If it ever stops running, something took the CPU from it
    // by force. Under cooperative scheduling this probe hangs the machine,
    // which is exactly why preemption is enabled around it and switched off
    // again afterwards.
    //
    // Both halves are checked, because each alone is misleading:
    //   - the worker made progress  → it was scheduled at all;
    //   - the main thread made progress WHILE the worker was spinning
    //     → the switch went both ways rather than stranding us.
    internal static unsafe class PreemptionProbe
    {
        private static volatile uint s_workerLoops;
        private static volatile bool s_workerDone;
        private static volatile bool s_stop;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WorkerEntry()
        {
            // No yield anywhere in here. The only way out is preemption
            // putting us aside long enough for the main thread to set s_stop.
            while (!s_stop)
            {
                s_workerLoops++;
            }

            s_workerDone = true;
            Scheduler.Exit();
        }

        public static void Run()
        {
            if (!OS.Hal.Apic.LocalApic.IsEnabled)
            {
                Console.WriteLine("[preempt] SKIP no timer interrupt");
                return;
            }

            s_workerLoops = 0;
            s_workerDone = false;
            s_stop = false;

            delegate* unmanaged<void> entry = &WorkerEntry;
            Thread? worker = Scheduler.Spawn(entry, 16 * 4096);
            if (worker == null)
            {
                Console.WriteLine("[preempt] SKIP could not spawn worker");
                return;
            }

            ulong switchesBefore = Preemption.Switches;
            ulong declinedBefore = Preemption.Declined;
            Preemption.Enable();

            // Hand the CPU over once, voluntarily. From here on the worker
            // never gives it back on its own.
            Scheduler.Yield();

            // Spin for a bounded slice of real time. Getting through this loop
            // at all means the CPU came back to us from a thread that never
            // yielded — measured against the HPET, which keeps running
            // regardless of who holds the CPU.
            ulong window = Hpet.IsInitialized ? Hpet.FrequencyHz / 5 : 0;   // 200 ms
            ulong start = Hpet.IsInitialized ? Hpet.ReadCounter() : 0;
            uint mainLoops = 0;

            if (window != 0)
            {
                while (Hpet.ReadCounter() - start < window)
                {
                    mainLoops++;
                }
            }

            s_stop = true;

            // Let the worker observe the flag and exit. Bounded: if preemption
            // is broken the worker may never run again, and a probe must not
            // be the thing that hangs the boot.
            for (int i = 0; i < 1000 && !s_workerDone; i++)
                Scheduler.Yield();

            Preemption.Disable();

            ulong switches = Preemption.Switches - switchesBefore;

            Console.Write("[preempt] switches=");
            Console.WriteULong(switches);
            Console.Write(" workerLoops=");
            Console.WriteUInt(s_workerLoops);
            Console.Write(" mainLoops=");
            Console.WriteUInt(mainLoops);
            // Delta, not the running total: ticks that arrived before
            // preemption was switched on are declined by design, and printing
            // the absolute count next to a windowed one invites reading a
            // deliberate choice as a loss.
            Console.Write(" declined=");
            Console.WriteULong(Preemption.Declined - declinedBefore);

            // Not "at least one switch": preemption that fires once and then
            // stops looks identical to working preemption from a single
            // counter, and that is exactly the defect this probe first missed
            // (switches=1, declined=28 — a global suppression counter that a
            // parked thread never lowered).
            //
            // Over a 200 ms window at 100 Hz the tick fires about 20 times and
            // roughly half of those should find the other thread running. Ten
            // is a floor that a healthy system clears easily and a
            // fire-once-and-die cannot.
            const ulong MinimumSwitches = 10;

            bool bothRan = s_workerLoops > 0 && mainLoops > 0;
            bool keptSwitching = switches >= MinimumSwitches;
            Console.WriteLine(keptSwitching && bothRan ? " PASS" : " FAIL");
        }
    }
}
