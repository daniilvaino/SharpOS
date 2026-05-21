using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel.Threading;

namespace OS.PAL.SharpOSHost
{
    // Phase E9.a -- minimum SharpOSHost_* threading surface to support
    // managed `new Thread(() => ...).Start(); Join();` from CoreCLR-hosted
    // code. Fork-side `dotnet-runtime-sharpos/src/coreclr/pal/sharpos/
    // crt_imp_stubs.cpp` will replace its current fake-handle stubs
    // (CreateThread returns fake handle but never starts the thread;
    // WaitForSingleObject returns WAIT_OBJECT_0 immediately) with calls
    // to these exports.
    //
    // Surface (E9.a):
    //   SharpOSHost_CreateThread          -> launches via Scheduler.SpawnHosted
    //   SharpOSHost_ExitThread            -> Scheduler.Exit + JoinEvent.Set
    //   SharpOSHost_GetCurrentThreadId    -> kernel.Thread.Id
    //   SharpOSHost_GetCurrentThread      -> opaque (per Win32 ABI, "current
    //                                        thread" pseudo-handle is -2)
    //   SharpOSHost_WaitForSingleObject   -> blocks on Thread.JoinEvent or
    //                                        Event/Semaphore.Wait
    //   SharpOSHost_CloseHandle           -> HandleTable.Free
    //   SharpOSHost_Sleep                 -> Scheduler.Sleep
    //   SharpOSHost_SwitchToThread        -> Scheduler.Yield + return 1
    //
    // Subsequent sub-phases extend with CreateEvent/SetEvent/CreateSemaphore/
    // CreateMutex/SRW locks/WaitOnAddress (E9.b), IOCP (E9.c), Suspend/Resume
    // (E9.d).
    //
    // Init: HandleTable.Init() must run before first CreateThread call.
    // Boot wires this in Phase 2 (before CoreCLR session brings any
    // SharpOSHost_* call online).
    internal static unsafe class ThreadStubs
    {
        // Win32 wait result codes.
        private const uint WAIT_OBJECT_0  = 0x00000000;
        private const uint WAIT_TIMEOUT   = 0x00000102;
        private const uint WAIT_FAILED    = 0xFFFFFFFF;

        // Win32 CreateThread dwCreationFlags bits we honor (E9.a/b).
        private const uint CREATE_SUSPENDED = 0x00000004;
        // STACK_SIZE_PARAM_IS_A_RESERVATION = 0x00010000 -- ignored; we
        // already give 1 MiB to every hosted thread.

        // Phase E9 -- Windows-style CoreCLR threads expect ~1 MiB stack
        // by convention. Our Scheduler.Spawn default is 64 KiB which is
        // plenty for our kernel test threads but well below what CoreCLR
        // and managed user code (recursive parsers, async state machine
        // capture, etc.) typically use. Hosted threads get 1 MiB to
        // match the platform convention; the original WinAPI CreateThread
        // dwStackSize argument from CoreCLR is ignored for now (fork
        // currently doesn't forward it; a future pass plumbs it through).
        private const uint HostedDefaultStackBytes = 1 * 1024 * 1024;

        // CoreCLR-side LPTHREAD_START_ROUTINE = DWORD(WINAPI*)(LPVOID).
        // WINAPI is no-op on x64 (Microsoft x64 ABI). We bind directly
        // as `delegate* unmanaged<void*, uint>`.
        //
        // dwCreationFlags: only CREATE_SUSPENDED (0x4) is interpreted.
        // CoreCLR always passes CREATE_SUSPENDED (vm/threads.cpp:2145),
        // then post-init dispatches ResumeThread. We honor it so the
        // managed thread's setup (TLS init, ICLRTask attach, etc.) can
        // race-free complete before the entry runs.
        [RuntimeExport("SharpOSHost_CreateThread")]
        public static ulong CreateThread(void* lpStartAddress, void* lpParam, uint dwCreationFlags, uint* lpThreadId)
        {
            OS.Hal.Console.Write("[CT] entry=0x"); OS.Hal.Console.WriteHex((ulong)lpStartAddress);
            OS.Hal.Console.Write(" param=0x"); OS.Hal.Console.WriteHex((ulong)lpParam);
            OS.Hal.Console.Write(" flags=0x"); OS.Hal.Console.WriteHex(dwCreationFlags);
            OS.Hal.Console.WriteLine("");

            if (!HandleTable.Init()) return 0;

            var entry = (delegate* unmanaged<void*, uint>)lpStartAddress;
            if (entry == null) return 0;

            bool suspended = (dwCreationFlags & CREATE_SUSPENDED) != 0;

            // Spawn first; SpawnHosted sets HostedEntry/HostedParam under
            // single-CPU cooperative invariant before EnqueueRunnable can
            // be observed by another thread.
            Thread? t = Scheduler.SpawnHosted(entry, lpParam,
                                              stackBytes: HostedDefaultStackBytes,
                                              owner: null,
                                              suspended: suspended);
            if (t == null) {
                OS.Hal.Console.WriteLine("[CT] SpawnHosted FAILED");
                return 0;
            }

            // Manual-reset Event so multiple Joins all unblock at exit.
            t.JoinEvent = new Event(manualReset: true, initialState: false);

            ulong handle = HandleTable.Alloc(t);
            if (handle == 0)
            {
                // Out of slots. The thread is already enqueued; can't
                // un-spawn. Mark the entry null so trampoline early-Exits.
                t.HostedEntry = null;
                OS.Hal.Console.WriteLine("[CT] HandleTable.Alloc FAILED");
                return 0;
            }

            if (lpThreadId != null) *lpThreadId = (uint)t.Id;
            OS.Hal.Console.Write("[CT] OK id=");
            OS.Hal.Console.WriteUInt((uint)t.Id);
            OS.Hal.Console.Write(" handle=0x");
            OS.Hal.Console.WriteHex(handle);
            OS.Hal.Console.Write(" teb=0x");
            OS.Hal.Console.WriteHex((ulong)t.Teb);
            OS.Hal.Console.Write(" stackBase=0x");
            OS.Hal.Console.WriteHex((ulong)t.StackBase);
            OS.Hal.Console.Write(" stackTop=0x");
            OS.Hal.Console.WriteHex((ulong)t.StackTop);
            OS.Hal.Console.WriteLine("");
            return handle;
        }

        [RuntimeExport("SharpOSHost_ExitThread")]
        public static void ExitThread(uint exitCode)
        {
            Thread? curr = Scheduler.Current;
            if (curr != null)
            {
                curr.HostedExitCode = exitCode;
                curr.HasExited = true;
                if (curr.JoinEvent != null)
                    curr.JoinEvent.Set();
            }
            Scheduler.Exit();
            // unreachable
        }

        [RuntimeExport("SharpOSHost_GetCurrentThreadId")]
        public static uint GetCurrentThreadId()
        {
            Thread? curr = Scheduler.Current;
            return curr == null ? 0 : (uint)curr.Id;
        }

        // Win32 GetCurrentThread returns a pseudo-handle (-2) that always
        // refers to "the calling thread". Real handle is via OpenThread or
        // CreateThread's return. We mirror the pseudo-handle convention so
        // existing fork code that compared against -2 keeps working.
        [RuntimeExport("SharpOSHost_GetCurrentThread")]
        public static void* GetCurrentThread()
        {
            return (void*)(nint)(-2);
        }

        [RuntimeExport("SharpOSHost_WaitForSingleObject")]
        public static uint WaitForSingleObject(ulong handle, uint timeoutMs)
        {
            object? target = HandleTable.Lookup(handle);
            if (target == null)
            {
                // Phase E9.a/b: handle not in our HandleTable -- a fake
                // handle that fork PAL produced (CreateEventW / Mutex /
                // file mapping / etc. not yet routed through SharpOSHost).
                // The pre-E9 fake stub returned WAIT_OBJECT_0 always (lie:
                // "signaled"). Preserve that behavior so CoreCLR's
                // internal call sites that depend on it keep working,
                // but yield first -- a tight WaitForSingleObject loop on
                // a fake event (e.g. finalizer wait, IPC pump) would
                // otherwise busy-spin and starve the rest of the
                // cooperative scheduler. Skip yield only for ms == 0 polls.
                if (timeoutMs != 0) Scheduler.Yield();
                return WAIT_OBJECT_0;
            }

            // Win32 semantics: ms == 0 is a poll, ms == INFINITE blocks.
            // Finite ms in between: not yet supported -- degrade to
            // infinite (TODO: plumb TimerQueue cancel-on-set).
            bool poll = (timeoutMs == 0);

            if (target is Thread t)
            {
                if (t.HasExited) return WAIT_OBJECT_0;
                if (poll) return WAIT_TIMEOUT;
                if (t.JoinEvent != null)
                {
                    t.JoinEvent.Wait();
                }
                else
                {
                    while (!t.HasExited) Scheduler.Yield();
                }
                return WAIT_OBJECT_0;
            }

            if (target is Event e)
            {
                if (poll) return e.IsSet ? WAIT_OBJECT_0 : WAIT_TIMEOUT;
                e.Wait();
                return WAIT_OBJECT_0;
            }

            if (target is Semaphore s)
            {
                if (poll) return s.Count > 0 ? WAIT_OBJECT_0 : WAIT_TIMEOUT;
                s.Wait();
                return WAIT_OBJECT_0;
            }

            // Unknown handle type for E9.a (Mutex / IOCP / file handle / ...).
            return WAIT_FAILED;
        }

        [RuntimeExport("SharpOSHost_CloseHandle")]
        public static int CloseHandle(ulong handle)
        {
            return HandleTable.Free(handle) ? 1 : 0;
        }

        [RuntimeExport("SharpOSHost_Sleep")]
        public static void Sleep(uint ms)
        {
            Scheduler.Sleep(ms);
        }

        [RuntimeExport("SharpOSHost_SwitchToThread")]
        public static int SwitchToThread()
        {
            Scheduler.Yield();
            return 1;
        }

        // Win32 ResumeThread returns the thread's PREVIOUS suspend count
        // (Win32 semantics). We don't keep a real suspend count yet --
        // honored values: 1 = was suspended, 0 = was already running,
        // 0xFFFFFFFF = error (unknown handle / wrong type). Sufficient
        // for CoreCLR which only checks the != -1 / != 0 distinctions.
        [RuntimeExport("SharpOSHost_ResumeThread")]
        public static uint ResumeThread(ulong handle)
        {
            object? target = HandleTable.Lookup(handle);
            if (target is not Thread t)
            {
                OS.Hal.Console.Write("[RT] BAD handle=0x");
                OS.Hal.Console.WriteHex(handle);
                OS.Hal.Console.WriteLine("");
                return 0xFFFFFFFF;
            }

            // MakeRunnable returns false if the thread isn't in `New` state
            // (i.e. already running / exited). Map to Win32 "previous
            // suspend count was 0" -- the resume was a no-op.
            bool wasSuspended = Scheduler.MakeRunnable(t);
            OS.Hal.Console.Write("[RT] id=");
            OS.Hal.Console.WriteUInt((uint)t.Id);
            OS.Hal.Console.Write(wasSuspended ? " -> RUNNABLE" : " -> already-running/exited");
            OS.Hal.Console.WriteLine("");
            return wasSuspended ? 1u : 0u;
        }
    }
}
