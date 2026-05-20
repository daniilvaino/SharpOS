namespace OS.Kernel.Threading
{
    // Phase E5 — counted semaphore. Wait() blocks while Count==0 and
    // decrements when a Release() makes Count>0. Release(n) increments
    // and wakes up to n waiters. Same single-CPU cooperative model as
    // Event (§7), no locks needed.
    //
    // Initial count seeds the semaphore; max is informational (we don't
    // enforce — caller's contract). Wait list is single-linked through
    // Thread.WaitNext, same field Event uses (a thread can be on AT
    // MOST ONE wait list at a time).
    internal unsafe class Semaphore
    {
        public int Count;
        public readonly int Max;

        private Thread? _waitHead;

        public Semaphore(int initialCount, int max = int.MaxValue)
        {
            if (initialCount < 0) initialCount = 0;
            if (max < 1) max = 1;
            if (initialCount > max) initialCount = max;
            Count = initialCount;
            Max = max;
        }

        // Block until at least 1 permit is available, then consume it.
        public void Wait()
        {
            if (Count > 0)
            {
                Count--;
                return;
            }

            Thread? curr = Scheduler.Current;
            if (curr == null) return;

            curr.WaitNext = _waitHead;
            _waitHead = curr;
            curr.State = ThreadState.Waiting;

            Scheduler.Yield();
            // Releaser already decremented Count to keep balance — see
            // Release() below.
        }

        // Add `n` permits. Wakes up to `n` waiters (one permit each).
        // Surplus permits stay in Count for future waiters.
        public void Release(int n = 1)
        {
            if (n <= 0) return;

            while (n > 0 && _waitHead != null)
            {
                Thread t = _waitHead;
                _waitHead = t.WaitNext;
                t.WaitNext = null;
                // The waiter consumes one permit on wake — we don't
                // bump Count for this slot.
                Scheduler.WakeFromWait(t);
                n--;
            }

            if (n > 0)
            {
                Count += n;
                if (Count > Max) Count = Max;
            }
        }
    }
}
