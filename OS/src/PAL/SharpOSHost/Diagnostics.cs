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
        // Master verbosity gate for ALL fork-side diagnostic chatter
        // ([crt]/[real]/[prestub]/[Crst]/[VH]/[LoadLibrary]/[seh]/… — every
        // one routes through DebugPrint/DebugPrintHex). Default OFF: serial
        // I/O of those lines is the dominant runtime cost; silencing them
        // makes coverage testing fast. Program output (SystemNative_Write →
        // DebugWrite) and kernel banners (Console.WriteLine) are NOT gated
        // and always print. Panic reason (Panic_C) prints directly, also
        // ungated. Flip to true + rebuild kernel (no fork rebuild) to get
        // the full trace back for a failing case.
        public static bool Verbose = false;  // step103: temporary, for msc-throw / SEH dispatch diagnostics

        // UTF-8 message, null-terminated. Writes one character at a time
        // to OS.Console (which routes to UEFI ConOut / kernel serial port).
        // Used by CRT walker для прогресса диагностики во время Phase 6.1.a.
        [RuntimeExport("SharpOSHost_DebugPrint")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DebugPrint")]
        public static void DebugPrint(byte* utf8Message)
        {
            if (!Verbose) return;
            if (utf8Message == null) return;
            byte* p = utf8Message;
            while (*p != 0)
            {
                Console.WriteChar((char)*p);
                p++;
            }
        }

        // Always-on variant: prints UTF-8 NUL-terminated regardless of Verbose.
        // For *critical* diagnostics from fork-side that must surface even when
        // chatter is muted (missing P/Invoke targets, missing QCALL bindings).
        [RuntimeExport("SharpOSHost_DebugPrintForced")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DebugPrintForced")]
        public static void DebugPrintForced(byte* utf8Message)
        {
            if (utf8Message == null) return;
            // "Forced" bypasses the Verbose chatter gate but still respects
            // Console.Quiet — the CLR-run mute. Fork-side EH/CCF traces use
            // Forced; they're not critical enough to break user-visible
            // PowerShell output.
            if (Console.Quiet) return;
            byte* p = utf8Message;
            while (*p != 0)
            {
                Console.WriteChar((char)*p);
                p++;
            }
        }

        // Length-prefixed serial write — no NUL terminator required. Used by
        // the libSystem.Native console shim (SystemNative_Write) so managed
        // System.Console output reaches COM1 byte-exact (handles embedded
        // NULs / non-ASCII / no trailing zero).
        [RuntimeExport("SharpOSHost_DebugWrite")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DebugWrite")]
        public static void DebugWrite(byte* buf, int len)
        {
            if (buf == null || len <= 0) return;
            for (int i = 0; i < len; i++)
                Console.WriteChar((char)buf[i]);
        }

        // Print hex value inline (no trailing newline). Callers append "\n"
        // through DebugPrint when they want a line break.
        [RuntimeExport("SharpOSHost_DebugPrintHex")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DebugPrintHex")]
        public static void DebugPrintHex(ulong value)
        {
            if (!Verbose) return;
            Console.WriteHex(value);
        }

        [RuntimeExport("SharpOSHost_DebugBreak")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DebugBreak")]
        public static void DebugBreak()
        {
            Panic.Fail("SharpOSHost_DebugBreak not implemented (Phase 6.1.a)");
        }

        // Panic — fork-side trap stubs (CRT) call this after printing
        // their diagnostic. Routes к kernel Panic.Fail → clean halt
        // with backtrace instead of hlt-loop hang.
        [RuntimeExport("SharpOSHost_Panic")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_Panic")]
        public static void Panic_C(byte* utf8Message)
        {
            // Print the failing reason via existing diagnostic, then halt
            // через kernel Panic so QEMU exits cleanly via -no-reboot.
            if (utf8Message != null)
            {
                byte* p = utf8Message;
                while (*p != 0)
                {
                    Console.WriteChar((char)*p);
                    p++;
                }
                Console.WriteLine("");
            }
            Panic.Fail("SharpOSHost_Panic from fork-side trap");
        }
    }
}
