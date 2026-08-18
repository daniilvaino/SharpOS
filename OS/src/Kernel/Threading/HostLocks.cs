using OS.Hal;
using OS.Hal.Timer;

namespace OS.Kernel.Threading
{
    // Mutual exclusion for the hosted runtime.
    //
    // Until preemption landed, the runtime's locks could be no-ops and the
    // system still worked: a thread only lost the CPU where it asked to, and
    // no critical section asked. Preemption removed that guarantee from every
    // critical section at once, and the failures came from the busiest one —
    // the code-heap allocator, where two compiling threads walked the same
    // half-built structure and called through a vtable slot that was still
    // zero.
    //
    // Keyed by the address of the caller's CRITICAL_SECTION / SRWLOCK, which
    // the runtime allocates and we never interpret: we only need identity, so
    // the pointer itself is the key and its bytes stay untouched. That keeps
    // the PAL side free of any layout knowledge.
    //
    // Recursive by design. CRITICAL_SECTION is, the runtime relies on it, and
    // a non-recursive lock would deadlock a thread against itself with no
    // diagnosis at all.
    internal sealed class HostLock
    {
        public ulong Key;
        public int OwnerId;        // 0 = free
        public int Recursion;
        public Thread? WaitHead;   // threads blocked on this lock
        public Thread? CondHead;   // threads blocked on the matching condition variable
    }

    internal static unsafe class HostLocks
    {
        // Open addressing, power of two. Entries ARE reclaimed, because the
        // runtime mints short-lived locks — one per compiled unit among
        // others — and a table that only grows filled up within a single
        // three-second run. What makes reclaiming safe: a lock with no owner
        // and no waiters holds no state at all, so dropping its entry is
        // indistinguishable from never having seen that address. It comes
        // back on the next Enter.
        private const int Slots = 4096;
        private static HostLock[] s_table = null!;
        private static int s_count;

        // Counted, not printed. A count that only ever grows says the table is
        // filling with one-shot keys; contention says the locks are earning
        // their keep. Both are questions the sampler can answer cheaply.
        private static ulong s_contended;
        public static ulong Contended => s_contended;
        public static int Count => s_count;

        private static HostLock Find(ulong key)
        {
            if (s_table == null) s_table = new HostLock[Slots];

            ulong h = (key * 0x9E3779B97F4A7C15UL) >> 51;
            int i = (int)(h & (Slots - 1));
            for (int n = 0; n < Slots; n++)
            {
                HostLock? e = s_table[i];
                if (e == null)
                {
                    var fresh = new HostLock();
                    fresh.Key = key;
                    s_table[i] = fresh;
                    s_count++;
                    return fresh;
                }
                if (e.Key == key) return e;
                i = (i + 1) & (Slots - 1);
            }

            // Full: drop every idle entry and try once more. Open addressing
            // makes piecemeal removal unsafe (it would break probe chains), so
            // the table is rebuilt from the live entries instead.
            if (Compact())
                return Find(key);

            // Still full with every entry live. Returning a shared entry would
            // be over-serialised but survivable; handing out an unlocked lock
            // would not be, and neither is worth guessing at.
            Panic.Fail("HostLocks: table full of live locks");
            return s_table[0];
        }

        // Returns true if anything was freed. Callers hold the suppression, so
        // no thread can be mid-Enter on the table while it is rebuilt.
        private static bool Compact()
        {
            var old = s_table;
            var fresh = new HostLock[Slots];
            int kept = 0;

            for (int j = 0; j < Slots; j++)
            {
                HostLock? e = old[j];
                if (e == null) continue;
                if (e.OwnerId == 0 && e.WaitHead == null && e.CondHead == null) continue;

                ulong h = (e.Key * 0x9E3779B97F4A7C15UL) >> 51;
                int i = (int)(h & (Slots - 1));
                while (fresh[i] != null) i = (i + 1) & (Slots - 1);
                fresh[i] = e;
                kept++;
            }

            if (kept == s_count) return false;   // nothing was idle

            s_table = fresh;
            s_count = kept;
            s_compactions++;
            return true;
        }

        private static ulong s_compactions;
        public static ulong Compactions => s_compactions;

        public static void Enter(ulong key)
        {
            Preemption.Suppress();

            HostLock e = Find(key);
            Thread? curr = Scheduler.Current;
            int me = curr == null ? -1 : curr.Id;

            if (curr == null)
            {
                // Before the scheduler exists there is exactly one thread, so
                // the lock is uncontended by construction.
                e.OwnerId = -1;
                e.Recursion++;
                Preemption.Allow();
                return;
            }

            while (e.OwnerId != 0 && e.OwnerId != me)
            {
                s_contended++;
                curr.Wait.Next = e.WaitHead;
                curr.Wait.Kind = WaitKind.Mutex;
                curr.Wait.Signalled = false;
                e.WaitHead = curr;
                curr.State = ThreadState.Waiting;

                Preemption.Allow();
                Scheduler.Yield();
                Preemption.Suppress();
                // Leave hands ownership over directly, so on wake the lock is
                // ours. The loop re-checks anyway: cheap, and it keeps the
                // invariant local instead of spread across two methods.
            }

            e.OwnerId = me;
            e.Recursion++;
            Preemption.Allow();
        }

        public static int TryEnter(ulong key)
        {
            Preemption.Suppress();

            HostLock e = Find(key);
            Thread? curr = Scheduler.Current;
            int me = curr == null ? -1 : curr.Id;

            int got;
            if (e.OwnerId == 0 || e.OwnerId == me)
            {
                e.OwnerId = me;
                e.Recursion++;
                got = 1;
            }
            else got = 0;

            Preemption.Allow();
            return got;
        }

        public static void Leave(ulong key)
        {
            Preemption.Suppress();

            HostLock e = Find(key);
            Thread? curr = Scheduler.Current;
            int me = curr == null ? -1 : curr.Id;

            if (e.OwnerId != me || e.Recursion <= 0)
            {
                // Not ours. Releasing anyway would let a third thread into a
                // section someone else is inside, which is the very failure
                // this file exists to stop.
                Preemption.Allow();
                return;
            }

            e.Recursion--;
            if (e.Recursion == 0)
            {
                e.OwnerId = 0;
                WakeOne(e);
            }

            Preemption.Allow();
        }

        // Called when the runtime initialises a lock object. Addresses get
        // reused: a freed CRITICAL_SECTION and a fresh one can share an
        // address, and an entry remembering the previous owner would deadlock
        // the new lock before its first use.
        public static void Reset(ulong key)
        {
            Preemption.Suppress();
            HostLock e = Find(key);
            if (e.WaitHead == null && e.CondHead == null)
            {
                e.OwnerId = 0;
                e.Recursion = 0;
            }
            // Waiters present means this is not a fresh object but a live one
            // being re-initialised: dropping ownership there would strand them.
            Preemption.Allow();
        }

        // Hand the lock to the first waiter without an unlocked window: a
        // thread woken here finds the lock already assigned to it.
        private static void WakeOne(HostLock e)
        {
            Thread? t = e.WaitHead;
            if (t == null) return;
            e.WaitHead = t.Wait.Next;
            t.Wait.Next = null;
            t.Wait.Kind = WaitKind.None;
            t.Wait.Signalled = true;
            e.OwnerId = t.Id;
            // Ownership only — the count stays zero because the woken Enter
            // falls out of its wait loop and increments it itself. Setting it
            // to one here made that increment a second one, so the lock was
            // held at depth two and the single matching Leave never freed it.
            e.Recursion = 0;
            Scheduler.WakeFromWait(t);
        }

        // ─── condition variables ────────────────────────────────────────
        //
        // The previous stand-in returned "signalled" immediately, so every
        // wait was a spurious wake and every predicate loop became a spin.

        public static int CondWait(ulong cvKey, ulong lockKey, uint milliseconds)
        {
            Preemption.Suppress();

            HostLock cv = Find(cvKey);
            HostLock lk = Find(lockKey);
            Thread? curr = Scheduler.Current;
            if (curr == null) { Preemption.Allow(); return 1; }

            // Release the lock completely — recursion included — and remember
            // how deep we were, so the caller returns to exactly the nesting
            // it had.
            int depth = 0;
            if (lk.OwnerId == curr.Id)
            {
                depth = lk.Recursion;
                lk.Recursion = 0;
                lk.OwnerId = 0;
                WakeOne(lk);
            }

            curr.Wait.Next = cv.CondHead;
            curr.Wait.Kind = WaitKind.Event;
            curr.Wait.Signalled = false;
            cv.CondHead = curr;
            curr.State = ThreadState.Waiting;

            bool timed = milliseconds != 0xFFFFFFFFu;
            if (timed)
            {
                ulong freq = Hpet.FrequencyHz;
                ulong perMs = freq / 1000;
                if (perMs == 0) perMs = 1;
                TimerQueue.Schedule(curr, Hpet.ReadCounter() + (ulong)milliseconds * perMs);
            }

            Preemption.Allow();
            Scheduler.Yield();
            Preemption.Suppress();

            bool signalled = curr.Wait.Signalled;
            if (timed && signalled) TimerQueue.Cancel(curr);
            if (!signalled) Unlink(cv, curr);   // woke on the deadline; nobody else knows

            Preemption.Allow();

            // Reacquire outside the suppressed region: this blocks, and
            // blocking with preemption suppressed would stop the whole system.
            if (depth > 0)
            {
                Enter(lockKey);
                Preemption.Suppress();
                Find(lockKey).Recursion = depth;
                Preemption.Allow();
            }

            return signalled ? 1 : 0;
        }

        public static void CondWake(ulong cvKey, int all)
        {
            Preemption.Suppress();

            HostLock cv = Find(cvKey);
            while (cv.CondHead != null)
            {
                Thread t = cv.CondHead;
                cv.CondHead = t.Wait.Next;
                t.Wait.Next = null;
                t.Wait.Kind = WaitKind.None;
                t.Wait.Signalled = true;
                Scheduler.WakeFromWait(t);
                if (all == 0) break;
            }

            Preemption.Allow();
        }

        private static void Unlink(HostLock e, Thread t)
        {
            Thread? p = e.CondHead;
            if (p == t) { e.CondHead = t.Wait.Next; t.Wait.Next = null; return; }
            while (p != null)
            {
                if (p.Wait.Next == t) { p.Wait.Next = t.Wait.Next; t.Wait.Next = null; return; }
                p = p.Wait.Next;
            }
        }
    }
}
