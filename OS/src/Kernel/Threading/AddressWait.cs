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
    //     - timeoutMs == INFINITE (-1) -> wait indefinitely. Finite ms
    //       not yet supported (would need TimerQueue cancel-on-wake);
    //       degrade to infinite.
    //   WakeByAddressSingle(addr):
    //     - Wake the first thread parked on this exact `addr`.
    //   WakeByAddressAll(addr):
    //     - Wake every thread parked on this `addr`.
    //
    // Single-CPU cooperative -- any Wait/Wake sequence is by construction
    // serialised across threads on this CPU. No locks needed today. SMP
    // would need per-bucket spinlocks.
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

        // Returns true on signal-driven wake (or no-wait fast path),
        // false on timeout. timeoutMs == 0xFFFFFFFF means infinite.
        // Finite timeouts use a HPET-deadline yield-poll on the value at
        // `addr` (same pattern as ThreadStubs.WaitForSingleObject for
        // Event/Semaphore): re-check memcmp on every Yield, return false
        // when deadline passes. Less efficient than a real composite
        // address+timer wait, but it works without needing cancel-on-wake
        // plumbing through WakeByAddress*. SP1-class composite wait is a
        // future Phase F task.
        public static bool WaitOnAddress(void* addr, void* cmpAddr, uint size, uint timeoutMs)
        {
            if (addr == null || cmpAddr == null || size == 0) return true;
            if (s_buckets == null) return true;

            // No-wait fast path: the value already differs.
            if (!MemEq(addr, cmpAddr, size))
                return true;

            Thread? curr = Scheduler.Current;
            if (curr == null) return true;

            bool infinite = (timeoutMs == 0xFFFFFFFFu);
            if (infinite)
            {
                // Infinite — park on bucket and wait for WakeByAddress*.
                int b = BucketOf(addr);
                curr.Wait.Address = addr;
                curr.Wait.Kind = WaitKind.Address;
                curr.Wait.Next = s_buckets[b];
                s_buckets[b] = curr;
                curr.State = ThreadState.Waiting;
                Scheduler.Yield();
                return true;
            }

            // Finite — HPET-deadline poll-yield. We don't park on the
            // bucket because WakeByAddress* doesn't cancel timers; instead
            // we observe value changes directly by re-reading memory.
            ulong freq = OS.Hal.Timer.Hpet.FrequencyHz;
            if (freq == 0)
            {
                // No HPET — fall back to infinite wait behaviour.
                int b = BucketOf(addr);
                curr.Wait.Address = addr;
                curr.Wait.Kind = WaitKind.Address;
                curr.Wait.Next = s_buckets[b];
                s_buckets[b] = curr;
                curr.State = ThreadState.Waiting;
                Scheduler.Yield();
                return true;
            }
            ulong ticksPerMs = freq / 1000UL;
            if (ticksPerMs == 0UL) ticksPerMs = 1UL;
            ulong deadline = OS.Hal.Timer.Hpet.ReadCounter() + (ulong)timeoutMs * ticksPerMs;
            while (true)
            {
                if (!MemEq(addr, cmpAddr, size)) return true;          // signaled
                if (OS.Hal.Timer.Hpet.ReadCounter() >= deadline) return false;  // timed out
                Scheduler.Yield();
            }
        }

        public static void WakeByAddressSingle(void* addr)
        {
            if (addr == null || s_buckets == null) return;
            int b = BucketOf(addr);
            Thread? prev = null;
            Thread? cur = s_buckets[b];
            while (cur != null)
            {
                if (cur.Wait.Address == addr)
                {
                    if (prev == null) s_buckets[b] = cur.Wait.Next;
                    else prev.Wait.Next = cur.Wait.Next;
                    Thread woken = cur;
                    woken.Wait.Next = null;
                    woken.Wait.Address = null;
                    woken.Wait.Kind = WaitKind.None;
                    Scheduler.WakeFromWait(woken);
                    return;
                }
                prev = cur;
                cur = cur.Wait.Next;
            }
        }

        public static void WakeByAddressAll(void* addr)
        {
            if (addr == null || s_buckets == null) return;
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
                    cur.Wait.Next = null;
                    cur.Wait.Address = null;
                    cur.Wait.Kind = WaitKind.None;
                    Scheduler.WakeFromWait(cur);
                    // prev unchanged (we removed `cur`)
                }
                else
                {
                    prev = cur;
                }
                cur = next;
            }
        }
    }
}
