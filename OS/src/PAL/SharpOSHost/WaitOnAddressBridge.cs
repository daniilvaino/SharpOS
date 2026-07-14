using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel.Threading;

namespace OS.PAL.SharpOSHost
{
    // Phase E9.c -- Win32 WaitOnAddress / WakeByAddressSingle /
    // WakeByAddressAll bridge. Modern .NET (since 4.6) routes the fast
    // path of ManualResetEventSlim, SemaphoreSlim, the low-level lock
    // inside Monitor, SpinWait yields, and ConcurrentDictionary slow
    // path through this trio. Pre-E9.c the PAL had no stub (CRT trap
    // on any call); E9.c routes through OS.Kernel.Threading.AddressWait.
    //
    // The Win32 spec accepts AddressSize of 1/2/4/8 only -- we accept
    // any size as a defensive measure (the BCL only passes the four
    // documented sizes).
    internal static unsafe class WaitOnAddressBridge
    {
        // Returns 1 on signal-driven wake (or no-wait fast path), 0 on
        // timeout. timeoutMs == INFINITE (0xFFFFFFFF) waits indefinitely;
        // finite timeouts degrade to infinite for now (no TimerQueue
        // cancel-on-wake plumbing yet; matches the WaitForSingleObject
        // policy in ThreadStubs.cs).
        [RuntimeExport("SharpOSHost_WaitOnAddress")]
        public static int WaitOnAddress(void* addr, void* cmpAddr, uint addressSize, uint timeoutMs)
        {
            return AddressWait.WaitOnAddress(addr, cmpAddr, addressSize, timeoutMs) ? 1 : 0;
        }

        [RuntimeExport("SharpOSHost_WakeByAddressSingle")]
        public static void WakeByAddressSingle(void* addr)
        {
            AddressWait.WakeByAddressSingle(addr);
        }

        [RuntimeExport("SharpOSHost_WakeByAddressAll")]
        public static void WakeByAddressAll(void* addr)
        {
            AddressWait.WakeByAddressAll(addr);
        }
    }
}
