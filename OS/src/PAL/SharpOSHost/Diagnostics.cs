using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel;

namespace OS.PAL.SharpOSHost
{
    // L3 Diagnostics surface — Win32 names → SharpOSHost mapping:
    //   OutputDebugStringA/W      → SharpOSHost_DebugPrint    (UTF-8 кanonical)
    //   DebugBreak                → SharpOSHost_DebugBreak    (int 3 / kernel-mode equivalent)
    //
    // Phase 6.1.a: CoreCLR debug output during init (stress logging,
    // assertion failures). Critical for diagnosing init failures.
    //
    // Implementation routes via OS.Kernel.Console (existing serial/COM1
    // log facility).
    internal static unsafe class SharpOSHostDiagnostics
    {
        // UTF-8 message, null-terminated. Length capped at реасонable
        // limit (1024?) by callee to prevent runaway формат strings.
        [RuntimeExport("SharpOSHost_DebugPrint")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DebugPrint")]
        public static void DebugPrint(byte* utf8Message)
        {
            Panic.Fail("SharpOSHost_DebugPrint not implemented (Phase 6.1.a)");
        }

        [RuntimeExport("SharpOSHost_DebugBreak")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DebugBreak")]
        public static void DebugBreak()
        {
            Panic.Fail("SharpOSHost_DebugBreak not implemented (Phase 6.1.a)");
        }
    }
}
