using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel.Threading;

namespace OS.PAL.SharpOSHost
{
    // Phase E9.b -- Win32 Mutex PAL bridge. CreateMutexW/Ex /
    // ReleaseMutex route through here. Backed by OS.Kernel.Threading
    // .Win32Mutex (reentrant, owner-tracking; distinct from the
    // non-reentrant OS.Kernel.Threading.Mutex used for Phase-E6
    // alloc-stress test infrastructure).
    //
    // WaitForSingleObject on a Mutex handle is implemented in
    // ThreadStubs.cs (looks up via HandleTable, calls Wait() in a loop
    // until ownership transfer; returns WAIT_OBJECT_0 normally or
    // WAIT_ABANDONED if the prior owner exited).
    internal static unsafe class MutexBridge
    {
        // bInitialOwner != 0 means the calling thread starts owning the
        // mutex (RecursionCount = 1, Owner = current). Otherwise the
        // mutex is created unowned.
        [RuntimeExport("SharpOSHost_CreateMutex")]
        public static ulong CreateMutex(int bInitialOwner)
        {
            if (!HandleTable.Init()) return 0;
            Win32Mutex mtx = new Win32Mutex();
            if (bInitialOwner != 0)
            {
                Thread? curr = Scheduler.Current;
                if (curr != null)
                {
                    mtx.Owner = curr;
                    mtx.RecursionCount = 1;
                }
            }
            return HandleTable.Alloc(mtx);
        }

        // OpenMutexW asks for an EXISTING named mutex. We have no namespace: every
        // CreateMutex here makes a fresh unnamed object, so nothing can ever be
        // "found" by name. Report that honestly — NULL handle with
        // ERROR_FILE_NOT_FOUND — which is what Windows returns for an unknown name
        // and what Mutex.TryOpenExisting expects for "no, it is not there".
        //
        // Returning success instead would be worse than useless: the caller would
        // get a handle to a mutex nobody owns and conclude another instance is
        // running.
        public const uint ErrorFileNotFound = 2;

        [RuntimeExport("SharpOSHost_OpenMutex")]
        public static ulong OpenMutex(uint* outLastError)
        {
            if (outLastError != null) *outLastError = ErrorFileNotFound;
            return 0;
        }

        [RuntimeExport("SharpOSHost_ReleaseMutex")]
        public static int ReleaseMutex(ulong handle)
        {
            object? target = HandleTable.Lookup(handle);
            if (target is not Win32Mutex mtx) return 0;
            return mtx.Release() ? 1 : 0;
        }
    }
}
