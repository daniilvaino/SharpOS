using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel.Threading;

namespace OS.PAL.SharpOSHost
{
    // Phase E11 -- IO completion port-like PAL exports. Backs CoreCLR's
    // LowLevelLifoSemaphore.Windows.cs which uses CreateIoCompletionPort /
    // GetQueuedCompletionStatus / PostQueuedCompletionStatus to implement
    // a LIFO counting semaphore (for PortableThreadPool worker wake-up).
    // Kernel-side impl lives in OS.Kernel.Threading.Iocp (Semaphore-backed).
    // Fork-side crt_imp_stubs.cpp wraps these into the Win32 names and
    // registers them in the kernel32 GetProcAddress resolver.
    //
    // Not a real IOCP -- no file association, no overlapped completion. We
    // ignore the file/key/overlapped pointers from the Win32 surface; the
    // ThreadPool's LIFO sem use case only needs counting + wait + post.
    internal static unsafe class IocpBridge
    {
        [RuntimeExport("SharpOSHost_IocpCreate")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_IocpCreate")]
        public static ulong IocpCreate(int maxConcurrent)
        {
            if (!HandleTable.IsInitialized) return 0;
            var iocp = new Iocp(maxConcurrent);
            return HandleTable.Alloc(iocp);
        }

        // Note: NOT using HandleTable.LookupAs<Iocp>(...) here. Generic
        // `as T` lowering needs RhTypeCast_IsInstanceOf which the std/
        // no-runtime doesn't ship -- only the concrete-class helper
        // RhTypeCast_IsInstanceOfClass is implemented. The `is not Iocp`
        // pattern with a concrete type goes through the class helper
        // and links cleanly. Same shape as EventBridge / MutexBridge /
        // SemaphoreBridge / ThreadStubs.
        [RuntimeExport("SharpOSHost_IocpWait")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_IocpWait")]
        public static int IocpWait(ulong handle, int timeoutMs)
        {
            object? target = HandleTable.Lookup(handle);
            if (target is not Iocp iocp) return 0;
            return iocp.Wait(timeoutMs) ? 1 : 0;
        }

        [RuntimeExport("SharpOSHost_IocpPost")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_IocpPost")]
        public static int IocpPost(ulong handle, int count)
        {
            object? target = HandleTable.Lookup(handle);
            if (target is not Iocp iocp) return 0;
            iocp.Post(count);
            return 1;
        }
    }
}
