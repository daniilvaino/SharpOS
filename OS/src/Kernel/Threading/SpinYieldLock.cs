using OS.Hal;

namespace OS.Kernel.Threading
{
    // Phase E6 -- bootstrap-safe lock for foundational allocators
    // (KernelHeap, GcHeap). The lock state is an external ulong field
    // owned by the caller; no managed allocation, no cctor dependency.
    //
    // Fast path: lock cmpxchg flag, 1, 0 via X64Asm.CmpXchg64 -- atomic
    // against IRQ-driven preemption (when it lands) and against SMP.
    // Slow path on contention: Scheduler.Yield() if a scheduler exists;
    // raw spin otherwise (e.g., Phase 1 KernelHeap pre-Phase-E4 boot,
    // when Scheduler.Current is null).
    //
    // Why not a Mutex class: new Mutex() calls GcHeap.AllocateRaw which
    // we want to lock too -- bootstrap dependency loop. Plus the
    // KernelHeap pre-GcHeap path can't even allocate a class instance.
    // Inline byte-flag side-steps both.
    internal static unsafe class SpinYieldLock
    {
        public static void Acquire(ulong* flag)
        {
            while (X64Asm.CmpXchg64(flag, value: 1UL, comparand: 0UL) != 0UL)
            {
                // Contended. If we have a scheduler context, yield so
                // some other thread can release. Pre-scheduler boot:
                // single-thread, the flag is logically uncontended -- if
                // we're here something is very wrong; spin to surface it.
                if (Scheduler.Current != null)
                    Scheduler.Yield();
            }
        }

        public static void Release(ulong* flag)
        {
            X64Asm.Xchg64(flag, 0UL);
        }
    }
}
