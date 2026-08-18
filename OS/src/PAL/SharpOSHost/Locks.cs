using System.Runtime;
using OS.Kernel.Threading;

namespace OS.PAL.SharpOSHost
{
    // ABI shape only. The lock itself — ownership, recursion, wait lists,
    // condition variables — lives in the kernel; here we do nothing but turn
    // a caller's lock object into the identity the kernel keys on, which is
    // its address. The bytes of the caller's CRITICAL_SECTION / SRWLOCK are
    // never read or written: the runtime owns that memory and its layout is
    // none of our business.
    internal static unsafe class SharpOSHostLocks
    {
        [RuntimeExport("SharpOSHost_LockEnter")]
        public static void LockEnter(void* key) => HostLocks.Enter((ulong)key);

        [RuntimeExport("SharpOSHost_LockReset")]
        public static void LockReset(void* key) => HostLocks.Reset((ulong)key);

        [RuntimeExport("SharpOSHost_LockTryEnter")]
        public static int LockTryEnter(void* key) => HostLocks.TryEnter((ulong)key);

        [RuntimeExport("SharpOSHost_LockLeave")]
        public static void LockLeave(void* key) => HostLocks.Leave((ulong)key);

        // Returns 1 if woken by a wake call, 0 if the deadline expired —
        // the distinction the caller's predicate loop depends on.
        [RuntimeExport("SharpOSHost_CondWait")]
        public static int CondWait(void* cv, void* lockKey, uint milliseconds)
            => HostLocks.CondWait((ulong)cv, (ulong)lockKey, milliseconds);

        [RuntimeExport("SharpOSHost_CondWake")]
        public static void CondWake(void* cv, int all) => HostLocks.CondWake((ulong)cv, all);
    }
}
