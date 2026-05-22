using OS.Hal;

namespace OS.Kernel.Threading
{
    // Phase E6 -- scheduler-aware mutual exclusion.
    //
    // Implementation: a single ulong flag `_locked` (0 = free, 1 = held)
    // plus a wait list of blocked threads. Fast path is a `lock cmpxchg`
    // via X64Asm.CmpXchg64 -- atomic against IRQ-driven preemption (when
    // it lands in Phase E6+) and against future SMP. Slow path links the
    // caller onto _waitHead and Yield()'s; the releaser hands ownership
    // directly to the first waiter (no race window where the lock looks
    // free and a third thread sneaks in).
    //
    // Cooperative single-CPU caveat: today there is no preemption, so a
    // critical section that never yields cannot actually be contended.
    // The Mutex is still useful because (a) any critical section that
    // touches scheduler primitives (Sleep/Wait/Event) DOES yield, and
    // (b) once preemption / SMP land the lock semantics carry over.
    //
    // Reentrancy: NOT reentrant. A thread that calls Acquire on a Mutex
    // it already holds will deadlock against itself (wait list never
    // wakes). Callers are responsible for non-reentrant use until E10+.
    internal unsafe class Mutex
    {
        private ulong _locked;
        private Thread? _waitHead;

        // Non-blocking attempt. Returns true if we got it.
        public bool TryAcquire()
        {
            fixed (ulong* p = &_locked)
            {
                ulong old = X64Asm.CmpXchg64(p, value: 1UL, comparand: 0UL);
                return old == 0UL;
            }
        }

        // Block until we own the lock. On contention, link onto wait list
        // and yield -- the releaser will WakeFromWait us and our Acquire
        // returns with the lock held (no re-CAS needed).
        public void Acquire()
        {
            if (TryAcquire()) return;

            Thread? curr = Scheduler.Current;
            if (curr == null)
            {
                // No scheduler context (e.g., pre-Phase-E4 boot path).
                // Spin-CAS as a degenerate single-thread fallback.
                while (!TryAcquire()) { /* nothing else to do */ }
                return;
            }

            curr.Wait.Next = _waitHead;
            curr.Wait.Kind = WaitKind.Mutex;
            _waitHead = curr;
            curr.State = ThreadState.Waiting;

            Scheduler.Yield();
            // When we wake the releaser has already handed us ownership;
            // _locked stays 1 (transferred), no further CAS needed.
        }

        // Release the lock. If a waiter is queued, hand ownership to it
        // directly (no observable "unlocked" window). Otherwise clear
        // _locked.
        public void Release()
        {
            if (_waitHead != null)
            {
                Thread t = _waitHead;
                _waitHead = t.Wait.Next;
                t.Wait.Next = null;
                t.Wait.Kind = WaitKind.None;
                // Ownership transferred -- _locked stays 1.
                Scheduler.WakeFromWait(t);
                return;
            }

            fixed (ulong* p = &_locked)
            {
                X64Asm.Xchg64(p, 0UL);   // atomic clear
            }
        }
    }
}
