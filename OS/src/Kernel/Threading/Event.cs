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
            // Testing the flag and joining the wait list must be one step. A
            // switch in between lets the signaller run: it sets the flag and
            // wakes a wait list this thread has not joined yet, and the thread
            // then sleeps on an event that has already fired. Nothing wakes it
            // again. That is what left the main thread in Join forever after
            // all four workers had exited and signalled — the same shape as the
            // semaphore race, in the primitive next door.
            Preemption.Suppress();

            if (IsSet)
            {
                if (!IsManualReset) IsSet = false;
                Preemption.Allow();
                return;
            }

            Thread? curr = Scheduler.Current;
            if (curr == null) { Preemption.Allow(); return; }   // nothing to block

            // Link onto wait list (LIFO; ordering doesn't matter for
            // manual-reset since all wake at once; for auto-reset we
            // wake the latest first — acceptable for E5).
            curr.Wait.Next = _waitHead;
            curr.Wait.Kind = WaitKind.Event;
            _waitHead = curr;
            curr.State = ThreadState.Waiting;

            Preemption.Allow();
            Scheduler.Yield();
            // When we return, Set woke us. Wait.Next was nulled at wake time.
        }

        // Signal the event. Manual-reset: wake ALL waiters, IsSet stays
        // true. Auto-reset: wake one waiter (if any), IsSet stays false;
        // if no waiter, IsSet becomes true (latched for the next Wait).
        public void Set()
        {
            // Same reason as Wait: publishing the flag and draining the wait
            // list is one step, or a waiter can slip in between the two.
            Preemption.Suppress();
            try { SetCore(); }
            finally { Preemption.Allow(); }
        }

        private void SetCore()
        {
            if (IsManualReset)
            {
                IsSet = true;
                while (_waitHead != null)
                {
                    Thread t = _waitHead;
                    _waitHead = t.Wait.Next;
                    t.Wait.Next = null;
                    t.Wait.Kind = WaitKind.None;
                    Scheduler.WakeFromWait(t);
                }
                return;
            }

            // Auto-reset.
            if (_waitHead != null)
            {
                Thread t = _waitHead;
                _waitHead = t.Wait.Next;
                t.Wait.Next = null;
                t.Wait.Kind = WaitKind.None;
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
