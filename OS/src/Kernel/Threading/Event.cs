namespace OS.Kernel.Threading
{
    // Phase E5 — scheduler-aware Event (manual or auto reset).
    // Threads call Wait() to block until Set() fires; the wait list is
    // a single-linked queue threaded through Thread.WaitNext.
    //
    // Manual-reset: Set() wakes ALL waiters and IsSet stays true until
    // Reset() is called.
    // Auto-reset: Set() wakes EXACTLY ONE waiter and immediately clears
    // IsSet. If no waiter is queued, IsSet stays true until the next
    // Wait() consumes it.
    //
    // Single-CPU cooperative — no locks: any sequence of Set/Reset/Wait
    // is by construction serialised across all threads on this CPU.
    internal unsafe class Event
    {
        public readonly bool IsManualReset;
        public bool IsSet;

        private Thread? _waitHead;

        public Event(bool manualReset, bool initialState = false)
        {
            IsManualReset = manualReset;
            IsSet = initialState;
        }

        // Block the current thread until the event is signalled. Returns
        // immediately if already set (consuming the signal on auto-reset).
        public void Wait()
        {
            if (IsSet)
            {
                if (!IsManualReset) IsSet = false;
                return;
            }

            Thread? curr = Scheduler.Current;
            if (curr == null) return;   // nothing to block

            // Link onto wait list (LIFO; ordering doesn't matter for
            // manual-reset since all wake at once; for auto-reset we
            // wake the latest first — acceptable for E5).
            curr.WaitNext = _waitHead;
            _waitHead = curr;
            curr.State = ThreadState.Waiting;

            Scheduler.Yield();
            // When we return, Set woke us. WaitNext was nulled at wake time.
        }

        // Signal the event. Manual-reset: wake ALL waiters, IsSet stays
        // true. Auto-reset: wake one waiter (if any), IsSet stays false;
        // if no waiter, IsSet becomes true (latched for the next Wait).
        public void Set()
        {
            if (IsManualReset)
            {
                IsSet = true;
                while (_waitHead != null)
                {
                    Thread t = _waitHead;
                    _waitHead = t.WaitNext;
                    t.WaitNext = null;
                    Scheduler.WakeFromWait(t);
                }
                return;
            }

            // Auto-reset.
            if (_waitHead != null)
            {
                Thread t = _waitHead;
                _waitHead = t.WaitNext;
                t.WaitNext = null;
                Scheduler.WakeFromWait(t);
                // IsSet stays false — signal consumed by the woken waiter.
            }
            else
            {
                IsSet = true;
            }
        }

        public void Reset()
        {
            IsSet = false;
        }
    }
}
