namespace OS.Kernel.Threading
{
    // Phase E9.c -- Win32 WaitOnAddress / WakeByAddressSingle /
    // WakeByAddressAll. Address-keyed wait queue used by modern .NET
    // fast-path sync primitives (ManualResetEventSlim, SemaphoreSlim,
    // SpinWait, the "low-level lock" inside Monitor, ConcurrentQueue's
    // slow path, etc.).
    //
    // Semantics:
    //   WaitOnAddress(addr, cmpAddr, size, timeoutMs):
    //     - If memcmp(addr, cmpAddr, size) != 0 -> return immediately.
    //     - Otherwise link the current thread into the bucket for `addr`
    //       and yield. On wake (WakeByAddress*), return.
    //     - timeoutMs == INFINITE (-1) -> wait indefinitely; finite -> also
    //       on the timer queue, false when the deadline wins; 0 -> probe.
    //   WakeByAddressSingle(addr):
    //     - Wake the first thread parked on this exact `addr`.
    //   WakeByAddressAll(addr):
    //     - Wake every thread parked on this `addr`.
    //
    // Single CPU: preemption is the only concurrency, so the bucket edits
    // run with it suppressed. SMP would need per-bucket spinlocks.
    //
    // Storage: 64-bucket hash table keyed by (addr >> 3) & 63 (drop low 3
    // bits since most addresses are 8-aligned; spreads better than raw
    // mod). Each bucket is the head of a Thread.WaitNext-linked list;
    // Thread.WaitAddress identifies the bucket entry.
    internal static unsafe class AddressWait
    {
        public const int BucketCount = 64;

        // Lazy-init: explicit Init() avoids the ClassConstructorRunner
        // trap that a field-initializer would trigger in the kernel
        // NoStdLib environment (CLAUDE.md §ClassConstructorRunner). Boot
        // calls Init() before the first WaitOnAddress can fire.
        private static Thread?[]? s_buckets;

        public static bool Init()
        {
            if (s_buckets != null) return true;
            s_buckets = new Thread?[BucketCount];
            return true;
        }

        private static int BucketOf(void* addr)
        {
            ulong v = (ulong)addr >> 3;
            return (int)(v & (BucketCount - 1));
        }

        // Byte-wise compare. Win32 documents 1/2/4/8 as the allowed sizes
        // (the API uses size_t but only those four are honoured by
        // RtlWaitOnAddress); we accept any size by doing a memcmp.
        private static bool MemEq(void* a, void* b, uint n)
        {
            byte* pa = (byte*)a, pb = (byte*)b;
            for (uint i = 0; i < n; i++)
                if (pa[i] != pb[i]) return false;
            return true;
        }

        // Returns true on a wake (or when the value already differs), false
        // on timeout. timeoutMs == 0xFFFFFFFF means infinite, 0 is a probe.
        //
        // Comparing the value and joining the bucket are ONE operation, the
        // same as in Event.Wait and Semaphore.WaitUntil: split by a thread
        // switch, a waker can change the value and wake an empty bucket in
        // between, and this thread then sleeps on a value that has already
        // changed, with nobody left to wake it.
        //
        // A finite wait parks on the bucket AND on the timer queue, and on
        // return works out which woke it. It used to re-read the value in a
        // Yield loop instead, which kept the waiter in the round-robin and
        // spent its slices reading the clock (see Semaphore.WaitUntil).
        public static bool WaitOnAddress(void* addr, void* cmpAddr, uint size, uint timeoutMs)
        {
            if (addr == null || cmpAddr == null || size == 0) return true;
            if (s_buckets == null) return true;

            if (timeoutMs == 0)
                return !MemEq(addr, cmpAddr, size);

            Thread? curr = Scheduler.Current;
            if (curr == null) return true;

            ulong freq = OS.Hal.Timer.Hpet.FrequencyHz;
            bool timed = timeoutMs != 0xFFFFFFFFu && freq != 0;   // no HPET: wait without a deadline

            Preemption.Suppress();

            if (!MemEq(addr, cmpAddr, size))
            {
                Preemption.Allow();
                return true;
            }

            int b = BucketOf(addr);
            curr.Wait.Address = addr;
            curr.Wait.Kind = WaitKind.Address;
            curr.Wait.Signalled = false;
            curr.Wait.Next = s_buckets[b];
            s_buckets[b] = curr;
            curr.State = ThreadState.Waiting;

            if (timed)
            {
                ulong ticksPerMs = freq / 1000UL;
                if (ticksPerMs == 0UL) ticksPerMs = 1UL;
                TimerQueue.Schedule(curr, OS.Hal.Timer.Hpet.ReadCounter() + (ulong)timeoutMs * ticksPerMs);
            }

            Preemption.Allow();
            ulong parked = OS.Kernel.Diagnostics.PerfCounters.Now();
            Scheduler.Yield();
            OS.Kernel.Diagnostics.PerfCounters.CountTimed(
                OS.Kernel.Diagnostics.PerfCounter.AddressWaits, OS.Kernel.Diagnostics.PerfCounter.AddressWaitTicks, parked);

            if (!timed) return true;

            // A wake marks the waiter it took off the bucket; anything else is
            // the deadline, and then we take ourselves off the bucket — nobody
            // else knows we stopped waiting.
            Preemption.Suppress();
            bool signalled = curr.Wait.Signalled;
            if (signalled) TimerQueue.Cancel(curr);
            else Unlink(curr, b);
            Preemption.Allow();
            return signalled;
        }

        /// <summary>Takes a thread out of whatever bucket it is parked in (Scheduler.LeaveApp).</summary>
        public static void Forget(Thread t)
        {
            if (s_buckets == null || t.Wait.Address == null) return;
            Unlink(t, BucketOf(t.Wait.Address));
        }

        private static void Unlink(Thread t, int b)
        {
            Thread? prev = null;
            Thread? cur = s_buckets![b];
            while (cur != null && cur != t) { prev = cur; cur = cur.Wait.Next; }
            if (cur != null)
            {
                if (prev == null) s_buckets[b] = cur.Wait.Next;
                else prev.Wait.Next = cur.Wait.Next;
            }
            t.Wait.Next = null;
            t.Wait.Address = null;
            t.Wait.Kind = WaitKind.None;
        }

        public static void WakeByAddressSingle(void* addr)
        {
            if (addr == null || s_buckets == null) return;
            OS.Kernel.Diagnostics.PerfCounters.Increment(OS.Kernel.Diagnostics.PerfCounter.AddressWakes);

            // The other half of the wait's critical section: walking and
            // editing a bucket while a waiter joins it corrupts the list.
            Preemption.Suppress();
            int b = BucketOf(addr);
            Thread? prev = null;
            Thread? cur = s_buckets[b];
            while (cur != null)
            {
                if (cur.Wait.Address == addr)
                {
                    if (prev == null) s_buckets[b] = cur.Wait.Next;
                    else prev.Wait.Next = cur.Wait.Next;
                    Wake(cur);
                    break;
                }
                prev = cur;
                cur = cur.Wait.Next;
            }
            Preemption.Allow();
        }

        public static void WakeByAddressAll(void* addr)
        {
            if (addr == null || s_buckets == null) return;
            OS.Kernel.Diagnostics.PerfCounters.Increment(OS.Kernel.Diagnostics.PerfCounter.AddressWakes);

            Preemption.Suppress();
            int b = BucketOf(addr);
            Thread? prev = null;
            Thread? cur = s_buckets[b];
            while (cur != null)
            {
                Thread? next = cur.Wait.Next;
                if (cur.Wait.Address == addr)
                {
                    if (prev == null) s_buckets[b] = next;
                    else prev.Wait.Next = next;
                    Wake(cur);
                    // prev unchanged (we removed `cur`)
                }
                else
                {
                    prev = cur;
                }
                cur = next;
            }
            Preemption.Allow();
        }

        private static void Wake(Thread t)
        {
            t.Wait.Next = null;
            t.Wait.Address = null;
            t.Wait.Kind = WaitKind.None;
            t.Wait.Signalled = true;
            Scheduler.WakeFromWait(t);
        }
    }
}
