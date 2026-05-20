namespace OS.Kernel.Threading
{
    // ProcessLifecycle — logical state per docs/threading-architecture.md
    // sec10 (Active / Exiting / Zombie). The pre-E7 OS.Kernel.Process.
    // ProcessState enum (None/Ready/Running/Exited/Failed) is ELF-launcher
    // bookkeeping; keep both untangled until the launcher refactor.
    internal enum ProcessLifecycle : byte
    {
        Active  = 0,    // at least one thread Running/Runnable/Waiting
        Exiting = 1,    // marked for shutdown; threads cleaning up
        Zombie  = 2,    // all threads gone, exit code captured, awaiting reap
    }

    // Phase E7 -- logical process container. NO MMU isolation (single
    // address space cooperative model); the Process serves as:
    //   1. PID + diagnostic name.
    //   2. Owner of one or more kernel.Thread instances (singly-linked
    //      via Thread.NextInProcess so a process can iterate its
    //      threads on exit).
    //   3. Exit-code rendezvous + state machine.
    //   4. Future home for the handle table (events / files / etc.).
    //
    // Real concurrent execution at the SAME virtual address (e.g., two
    // ELF apps both linked at 0x400000) requires per-process CR3 -- not
    // landed in E7. For now Process is the abstraction; ELFs at the
    // same VA still serialise at the launcher level.
    internal unsafe class Process
    {
        public uint Id;
        public string? Name;
        public ProcessLifecycle State;
        public int ExitCode;

        // Singly-linked list of threads belonging to this process,
        // threaded via Thread.NextInProcess. FirstThread is the primary
        // thread (the one created by Process.Launch); subsequent threads
        // are pushed at the head.
        public Thread? FirstThread;
        public uint ThreadCount;

        // ProcessTable registry link.
        public Process? Next;

        // Phase E7 acceptance helpers.

        // Launch a new Process with one primary thread. The thread will
        // be Runnable; caller (typically a probe or the launcher) can
        // either Yield to let it run, or WaitForExit() to block until
        // the process is Zombie.
        public static Process? Launch(
            string name,
            delegate* unmanaged<void> entry,
            uint stackBytes = 0)
        {
            if (entry == null) return null;

            Process p = ProcessTable.Allocate(name);

            Thread? t = Scheduler.Spawn(entry, stackBytes, p);
            if (t == null)
            {
                ProcessTable.Unregister(p);
                return null;
            }

            return p;
        }

        // Called by a process's own thread when it's done. Mark the
        // current thread exited (via Scheduler.Exit -- never returns)
        // AFTER updating Process state. The state transition + exit-
        // code capture happen here so concurrent WaitForExit observers
        // see the value before the thread vanishes.
        public static void Exit(int code)
        {
            Thread? curr = Scheduler.Current;
            Process? p = curr?.OwnerProcess;
            if (p != null)
            {
                p.ExitCode = code;
                p.State = ProcessLifecycle.Zombie;
            }
            Scheduler.Exit();
            // Unreachable.
        }

        // Block the current thread until `this` reports Zombie. Polls
        // by yielding -- no Event yet because creating one in pre-E7
        // boot has bootstrap subtleties; we have Sleep/Yield so a
        // yield-loop is fine for the probe.
        public bool WaitForExit(uint timeoutMs = 5000)
        {
            ulong freq = OS.Hal.Timer.Hpet.FrequencyHz;
            if (freq == 0)
            {
                // No HPET -- spin without deadline (boot-time degenerate).
                while (State != ProcessLifecycle.Zombie)
                    Scheduler.Yield();
                return true;
            }
            ulong ticksPerMs = freq / 1000;
            if (ticksPerMs == 0) ticksPerMs = 1;
            ulong deadline = OS.Hal.Timer.Hpet.ReadCounter() + (ulong)timeoutMs * ticksPerMs;

            while (State != ProcessLifecycle.Zombie)
            {
                if (OS.Hal.Timer.Hpet.ReadCounter() >= deadline)
                    return false;
                Scheduler.Yield();
            }
            return true;
        }
    }
}
