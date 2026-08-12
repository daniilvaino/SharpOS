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

        // Non-blocking attempt: consume a permit if available, else return false.
        public bool TryAcquire()
        {
            if (Count > 0)
            {
                Count--;
                return true;
            }
            return false;
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

            curr.Wait.Next = _waitHead;
            curr.Wait.Kind = WaitKind.Semaphore;
            _waitHead = curr;
            curr.State = ThreadState.Waiting;

            Scheduler.Yield();
            // Releaser already decremented Count to keep balance — see
            // Release() below.
        }

        /// <summary>
        /// Block until a permit is available or the deadline passes. Returns
        /// true if a permit was taken.
        /// </summary>
        /// <remarks>
        /// The point of this over a poll loop is that the thread LEAVES the
        /// runnable queue. Polling with a timeout kept every waiter in the
        /// round-robin, so a machine with eight idle waiters gave the one
        /// thread doing work an eighth of the CPU — and the waiters spent
        /// their slices reading the clock. Measured: 41% of all samples inside
        /// Hpet.ReadCounter, called from here.
        ///
        /// Woken by whichever comes first: Release (which hands over a permit)
        /// or the timer queue at the deadline. Both make the thread runnable
        /// again, so on return we have to work out which happened.
        /// </remarks>
        public bool WaitUntil(ulong deadlineTicks)
        {
            // Checking the count and joining the wait list is ONE operation.
            //
            // Split by a thread switch, a releaser can run in between: it sees
            // an empty wait list, hands its permit to nobody, and this thread
            // then goes to sleep on a semaphore that is already signalled.
            // Under cooperative scheduling the gap did not exist; preemption
            // opened it, and the result was every thread blocked with nobody
            // left to wake them — the machine idling forever with one thread
            // halting and waking on the tick.
            Preemption.Suppress();

            if (Count > 0)
            {
                Count--;
                Preemption.Allow();
                return true;
            }

            Thread? curr = Scheduler.Current;
            if (curr == null) { Preemption.Allow(); return false; }

            curr.Wait.Next = _waitHead;
            curr.Wait.Kind = WaitKind.Semaphore;
            _waitHead = curr;
            curr.State = ThreadState.Waiting;
            curr.Wait.Signalled = false;

            TimerQueue.Schedule(curr, deadlineTicks);

            Preemption.Allow();
            Scheduler.Yield();

            // Release marks the waiter it handed a permit to. Anything else is
            // the deadline, and then we have to take ourselves off the wait
            // list — nobody else knows we stopped waiting.
            if (curr.Wait.Signalled)
            {
                TimerQueue.Cancel(curr);
                return true;
            }

            Unlink(curr);
            return false;
        }

        private void Unlink(Thread t)
        {
            Thread? prev = null;
            Thread? c = _waitHead;
            while (c != null && c != t) { prev = c; c = c.Wait.Next; }
            if (c == null) return;
            if (prev == null) _waitHead = c.Wait.Next;
            else prev.Wait.Next = c.Wait.Next;
            c.Wait.Next = null;
        }

        // Add `n` permits. Wakes up to `n` waiters (one permit each).
        // Surplus permits stay in Count for future waiters.
        public void Release(int n = 1)
        {
            if (n <= 0) return;

            // The other half of the same critical section: handing permits to
            // waiters walks the list and mutates counts, and a switch in the
            // middle leaves both inconsistent.
            Preemption.Suppress();
            try { ReleaseCore(n); }
            finally { Preemption.Allow(); }
        }

        private void ReleaseCore(int n)
        {

            while (n > 0 && _waitHead != null)
            {
                Thread t = _waitHead;
                _waitHead = t.Wait.Next;
                t.Wait.Next = null;
                t.Wait.Kind = WaitKind.None;
                // The waiter consumes one permit on wake — we don't
                // bump Count for this slot.
                t.Wait.Signalled = true;
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
