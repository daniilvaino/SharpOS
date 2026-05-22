using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel.Threading;

namespace OS.PAL.SharpOSHost
{
    // Phase E9.b -- Win32 Semaphore PAL bridge. CreateSemaphoreW /
    // CreateSemaphoreExW / ReleaseSemaphore route through here so
    // kernel-side OS.Kernel.Threading.Semaphore drives Wait/Release
    // through Scheduler. ThreadStubs.WaitForSingleObject's Semaphore
    // branch is already in place (E9.a) -- now sees real semaphores
    // instead of null on Lookup.
    internal static unsafe class SemaphoreBridge
    {
        [RuntimeExport("SharpOSHost_CreateSemaphore")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_CreateSemaphore")]
        public static ulong CreateSemaphore(int initialCount, int maxCount)
        {
            if (!HandleTable.Init()) return 0;
            Semaphore sem = new Semaphore(initialCount, maxCount > 0 ? maxCount : int.MaxValue);
            return HandleTable.Alloc(sem);
        }

        // Win32 ReleaseSemaphore writes the previous count to *outPrev
        // if non-null. Returns 1 on success, 0 on bad handle.
        [RuntimeExport("SharpOSHost_ReleaseSemaphore")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_ReleaseSemaphore")]
        public static int ReleaseSemaphore(ulong handle, int releaseCount, int* outPrev)
        {
            object? target = HandleTable.Lookup(handle);
            if (target is not Semaphore sem) return 0;
            if (releaseCount <= 0) return 0;
            int prev = sem.Count;
            sem.Release(releaseCount);
            if (outPrev != null) *outPrev = prev;
            return 1;
        }
    }
}
