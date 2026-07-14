using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel;

namespace OS.PAL.SharpOSHost
{
    // L3 Time surface — Win32 names → SharpOSHost mapping:
    //   QueryPerformanceCounter   → SharpOSHost_GetTickCount    (HPET/TSC ticks)
    //   QueryPerformanceFrequency → SharpOSHost_GetTickFreq     (ticks per second)
    //   GetTickCount64            → SharpOSHost_GetMillis       (uptime in ms)
    //   GetSystemTimeAsFileTime   → SharpOSHost_GetSystemTime   (FILETIME 100-ns ticks since 1601)
    //
    // Phase 6.1.a: GC may call these during init (heap stats, GC trigger
    // policy). Implementation reads from existing OS.Hal.Timer (Phase 0
    // initializes HPET).
    internal static unsafe class SharpOSHostTime
    {
        [RuntimeExport("SharpOSHost_GetTickCount")]
        public static ulong GetTickCount()
        {
            Panic.Fail("SharpOSHost_GetTickCount not implemented (Phase 6.1.a)");
            return 0;
        }

        [RuntimeExport("SharpOSHost_GetTickFreq")]
        public static ulong GetTickFreq()
        {
            Panic.Fail("SharpOSHost_GetTickFreq not implemented (Phase 6.1.a)");
            return 0;
        }

        [RuntimeExport("SharpOSHost_GetMillis")]
        public static ulong GetMillis()
        {
            Panic.Fail("SharpOSHost_GetMillis not implemented (Phase 6.1.a)");
            return 0;
        }

        // SharpOSHost_GetSystemTime is provided by Clock.cs (real RTC impl,
        // ushort* SYSTEMTIME signature matching the fork's crt_imp_stubs.cpp
        // declaration). A stale ulong* Panic stub used to live here and
        // duplicate-defined the symbol — ILC 7 dead-code-eliminated the
        // unreachable stub, ILC 8 roots both and errors "symbol already
        // defined". Removed; Clock.cs owns the export.
    }
}
