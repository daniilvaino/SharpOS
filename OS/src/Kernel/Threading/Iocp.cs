using OS.Hal.Timer;

namespace OS.Kernel.Threading
{
    // Phase E11 -- IO completion port-like primitive for CoreCLR's
    // LowLevelLifoSemaphore (used by PortableThreadPool worker wake-up,
    // TimerQueue.Portable's worker dispatch). Windows IOCP semantics
    // boil down to "counting semaphore with LIFO wake order"; our
    // existing Semaphore already inserts/removes at the head (LIFO),
    // so we just wrap it and add timeoutMs support via Hpet poll.
    //
    // Not a real file/device IOCP -- no file association, no overlapped
    // completion. Just enough for the LIFO sem use case that the
    // ThreadPool / Timer need.
    //
    // Wait(timeoutMs) returns true on permit acquired, false on timeout.
    //   timeoutMs == 0          -> non-blocking probe, returns
    //                              false immediately if no permit
    //   timeoutMs == -1 (infinite) -> proper Semaphore.Wait (blocks until Post)
    //   timeoutMs > 0           -> Hpet-deadline yield-poll loop. Inefficient
    //                              under contention but correct; revisit
    //                              if benchmark needs it.
    //
    // Post(n) increments by n, wakes up to n waiters from the front of the
    // wait list (head=most-recently-blocked, by Semaphore's LIFO insert).
    internal unsafe class Iocp
    {
        private readonly Semaphore _sem;

        public Iocp(int maxConcurrent)
        {
            int cap = maxConcurrent < 1 ? 1 : maxConcurrent;
            _sem = new Semaphore(initialCount: 0, max: cap);
        }

        public bool Wait(int timeoutMs)
        {
            if (_sem.TryAcquire()) return true;
            if (timeoutMs == 0) return false;
            if (timeoutMs < 0)
            {
                _sem.Wait();
                return true;
            }

            ulong freq = Hpet.FrequencyHz;
            if (freq == 0)
            {
                // No HPET -- can't measure timeout. Block infinitely
                // (degenerate fallback; should not happen post-boot).
                _sem.Wait();
                return true;
            }
            ulong ticksPerMs = freq / 1000UL;
            if (ticksPerMs == 0UL) ticksPerMs = 1UL;
            ulong deadline = Hpet.ReadCounter() + (ulong)timeoutMs * ticksPerMs;

            while (true)
            {
                if (_sem.TryAcquire()) return true;
                if (Hpet.ReadCounter() >= deadline) return false;
                Scheduler.Yield();
            }
        }

        public void Post(int n)
        {
            if (n <= 0) return;
            _sem.Release(n);
        }
    }
}
