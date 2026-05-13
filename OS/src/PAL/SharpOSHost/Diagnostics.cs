using System.Runtime;
using System.Runtime.InteropServices;
using OS.Hal;
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
        // UTF-8 message, null-terminated. Writes one character at a time
        // to OS.Console (which routes to UEFI ConOut / kernel serial port).
        // Used by CRT walker для прогресса диагностики во время Phase 6.1.a.
        [RuntimeExport("SharpOSHost_DebugPrint")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DebugPrint")]
        public static void DebugPrint(byte* utf8Message)
        {
            if (utf8Message == null) return;
            byte* p = utf8Message;
            while (*p != 0)
            {
                Console.WriteChar((char)*p);
                p++;
            }
        }

        // Print hex value followed by newline — used от walker для
        // emitting ctor addresses each iteration.
        [RuntimeExport("SharpOSHost_DebugPrintHex")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DebugPrintHex")]
        public static void DebugPrintHex(ulong value)
        {
            Console.WriteHex(value);
            Console.WriteLine("");
        }

        [RuntimeExport("SharpOSHost_DebugBreak")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DebugBreak")]
        public static void DebugBreak()
        {
            Panic.Fail("SharpOSHost_DebugBreak not implemented (Phase 6.1.a)");
        }
    }
}
