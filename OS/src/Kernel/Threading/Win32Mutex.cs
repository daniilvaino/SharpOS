namespace OS.Kernel.Threading
{
    // Phase E9.b -- Win32-semantics mutex. Distinct from OS.Kernel.Threading.Mutex
    // (Phase E6, non-reentrant) because Win32 CreateMutex returns a
    // reentrant lock with abandoned-mutex bookkeeping. Tracks owner
    // Thread + recursion count.
    //
    // Semantics:
    //   Wait():
    //     - If unowned, claim it (owner = current, count = 1) and return.
    //     - If owned by current thread, count++ and return (recursion).
    //     - Otherwise block on wait list; on wake, ownership is handed
    //       to us (count = 1).
    //   Release():
    //     - If we don't own it, no-op (or could throw; for now no-op
    //       since the PAL ReleaseMutex API surfaces the error).
    //     - Decrement count. If count reaches 0, hand to next waiter
    //       (count = 1, new owner) or clear ownership.
    //   IsAbandoned():
    //     - True if owner thread is in Exited state and count > 0.
    //       PAL's WaitForSingleObject returns WAIT_ABANDONED when
    //       the wait completes on an abandoned mutex.
    //
    // Single-CPU cooperative; no atomics needed for the inner state
    // (any Wait/Release sequence is serialised across all threads on
    // this CPU). SMP-readiness left for E13+.
    internal unsafe class Win32Mutex
    {
        public Thread? Owner;
        public uint RecursionCount;

        private Thread? _waitHead;

        // Returns 0 = acquired, 1 = recursive re-acquire,
        // 2 = abandoned-then-acquired (previous owner exited),
        // 3 = blocked and woken. The PAL layer maps the abandoned
        // bit to WAIT_ABANDONED for its caller.
        public uint Wait()
        {
            Thread? curr = Scheduler.Current;
            if (curr == null) return 0;   // no scheduler context (pre-E4)

            // Detect abandoned mutex: owner thread has exited but
            // RecursionCount stayed > 0. Reset, claim ourselves.
            if (Owner != null && Owner.State == ThreadState.Exited)
            {
                Owner = curr;
                RecursionCount = 1;
                return 2;       // signal abandoned to caller
            }

            // Recursive re-acquire by the current owner.
            if (Owner == curr)
            {
                RecursionCount++;
                return 1;
            }

            // Fresh acquire when unowned.
            if (Owner == null)
            {
                Owner = curr;
                RecursionCount = 1;
                return 0;
            }

            // Contended -- block.
            curr.Wait.Next = _waitHead;
            curr.Wait.Kind = WaitKind.Mutex;
            _waitHead = curr;
            curr.State = ThreadState.Waiting;
            Scheduler.Yield();
            // On wake, Release() handed us ownership directly
            // (Owner = curr, RecursionCount = 1).
            return 3;
        }

        // Returns true if the caller actually owned the mutex (and the
        // release was valid). False if the caller didn't own it (PAL
        // ReleaseMutex surfaces this as ERROR_NOT_OWNER).
        public bool Release()
        {
            Thread? curr = Scheduler.Current;
            if (curr == null || Owner != curr) return false;

            if (RecursionCount > 1)
            {
                RecursionCount--;
                return true;
            }

            // Final release. Hand to next waiter or clear ownership.
            if (_waitHead != null)
            {
                Thread t = _waitHead;
                _waitHead = t.Wait.Next;
                t.Wait.Next = null;
                t.Wait.Kind = WaitKind.None;
                Owner = t;
                RecursionCount = 1;     // new owner starts at depth 1
                Scheduler.WakeFromWait(t);
            }
            else
            {
                Owner = null;
                RecursionCount = 0;
            }
            return true;
        }
    }
}
