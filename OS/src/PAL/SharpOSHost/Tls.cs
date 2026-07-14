using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel;

namespace OS.PAL.SharpOSHost
{
    // L3 TLS surface — per D2 (TLS), Phase 5.5 prereq.
    //
    // Win32 names → SharpOSHost:
    //   TlsAlloc     → SharpOSHost_TlsAlloc
    //   TlsFree      → SharpOSHost_TlsFree
    //   TlsGetValue  → SharpOSHost_TlsGet
    //   TlsSetValue  → SharpOSHost_TlsSet
    //
    // Single thread в Phase 6.1 → implementation = static slot array
    // ~64 slots. После Phase 5.5 native TLS bring-up — реальная
    // FS-segment relative storage. До тех пор — global static array.
    //
    // CoreCLR vm/ uses TLS extensively (current thread, EE state slot,
    // GC alloc context). Critical для coreclr_initialize.
    internal static unsafe class SharpOSHostTls
    {
        [RuntimeExport("SharpOSHost_TlsAlloc")]
        public static uint TlsAlloc()
        {
            Panic.Fail("SharpOSHost_TlsAlloc not implemented (Phase 6.1.a, needs Phase 5.5 TLS bring-up)");
            return 0xFFFFFFFF;
        }

        [RuntimeExport("SharpOSHost_TlsFree")]
        public static int TlsFree(uint index)
        {
            Panic.Fail("SharpOSHost_TlsFree not implemented (Phase 6.1.a)");
            return 0;
        }

        [RuntimeExport("SharpOSHost_TlsGet")]
        public static void* TlsGet(uint index)
        {
            Panic.Fail("SharpOSHost_TlsGet not implemented (Phase 6.1.a)");
            return null;
        }

        [RuntimeExport("SharpOSHost_TlsSet")]
        public static int TlsSet(uint index, void* value)
        {
            Panic.Fail("SharpOSHost_TlsSet not implemented (Phase 6.1.a)");
            return 0;
        }
    }
}
