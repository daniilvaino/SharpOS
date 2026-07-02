using System.Runtime;
using System.Runtime.InteropServices;
using OS.Hal;
using OS.Kernel;

namespace OS.PAL.SharpOSHost
{
    // Per-category gates for fork-side diagnostic chatter. Each line in the
    // log starts with a "[Tag] ..." prefix. Diagnostics.DebugPrint classifies
    // by tag, consults the matching gate, and either passes the line through
    // to Console.WriteChar or silently drops it. State is per-line: the
    // category is captured at the first '[' of a fresh line and remembered
    // for follow-up DebugPrint/DebugPrintHex/DebugWrite calls until '\n'.
    //
    // Default profile is tuned for "PowerShell interactive UX": JIT chatter
    // and Crst lock traces (which dominate UART throughput) off, EH +
    // FileOpen on (useful for diagnosing user-visible issues). Toggle from
    // CoreClrProbe.cs or interactively via /probe shell (when that lands).
    internal static class TraceGate
    {
        public static bool Jit       = false;  // [prestub] [DoPrestub] [PIBC] [JCCL*] [PSW]
        public static bool Crst      = false;  // [Crst::Enter] [Crst::Leave]
        public static bool Real      = false;  // [real] [CRT trap]
        public static bool LoadLib   = false;  // [LoadLibrary] [GetProcAddress]
        public static bool Vm        = false;  // [vm-reserve] [stub-reg]
        public static bool Eh        = true;   // [seh*] [SFI] [DESP] [CCF*] [PCRE*]
        public static bool Host      = true;   // [host] FileOpen
        public static bool Thread    = false;  // [CT] [RT] [Tramp]
        public static bool Probe     = true;   // [probe-*]
        public static bool Info      = true;   // [info]
        public static bool Unknown   = true;   // any line not matching above
    }

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

        // Per-line state for TraceGate dispatch. The fork emits a single
        // logical line via several DebugPrint+DebugPrintHex calls; we remember
        // whether the current line is being suppressed/printed until '\n'
        // ends it. s_needClassify=true means "next non-empty input begins a
        // fresh line — re-classify on first byte".
        private static bool s_needClassify = true;
        private static bool s_currentLineOn = true;

        // Match `prefix` against `p` (NUL-bounded). Returns true on equality
        // through prefix.Length chars. ASCII only — TraceGate tags are
        // bracket-quoted ASCII identifiers.
        private static bool StartsWith(byte* p, string prefix)
        {
            for (int i = 0; i < prefix.Length; i++)
            {
                byte c = p[i];
                if (c == 0) return false;
                if (c != (byte)prefix[i]) return false;
            }
            return true;
        }

        // Classify the bracket-quoted tag at the line head. Returns whether
        // its TraceGate is on. p points at the '[' character.
        private static bool ClassifyHead(byte* p)
        {
            // p[0] == '['; look at p[1]+
            byte* t = p + 1;
            // Fast first-byte dispatch keeps this branch-cheap.
            switch ((char)t[0])
            {
                case 'p':
                    if (StartsWith(t, "prestub")) return TraceGate.Jit;
                    if (StartsWith(t, "probe"))   return TraceGate.Probe;
                    break;
                case 'D':
                    if (StartsWith(t, "DoPrestub")) return TraceGate.Jit;
                    if (StartsWith(t, "DESP"))      return TraceGate.Eh;
                    if (StartsWith(t, "DbgBrk"))    return TraceGate.Unknown;
                    break;
                case 'P':
                    if (StartsWith(t, "PIBC"))    return TraceGate.Jit;
                    if (StartsWith(t, "PSW"))     return TraceGate.Jit;
                    if (StartsWith(t, "PCRE"))    return TraceGate.Eh;
                    break;
                case 'J':
                    if (StartsWith(t, "JCC"))     return TraceGate.Jit;
                    break;
                case 'C':
                    if (StartsWith(t, "Crst"))    return TraceGate.Crst;
                    if (StartsWith(t, "CCF"))     return TraceGate.Eh;
                    if (StartsWith(t, "CT]"))     return TraceGate.Thread;
                    break;
                case 'R':
                    if (StartsWith(t, "RT]"))     return TraceGate.Thread;
                    if (StartsWith(t, "real"))    return TraceGate.Real;
                    break;
                case 'L':
                    if (StartsWith(t, "LoadLibrary")) return TraceGate.LoadLib;
                    break;
                case 'G':
                    if (StartsWith(t, "GetProcAddress")) return TraceGate.LoadLib;
                    break;
                case 'v':
                    if (StartsWith(t, "vm-"))     return TraceGate.Vm;
                    break;
                case 's':
                    if (StartsWith(t, "stub-"))   return TraceGate.Vm;
                    if (StartsWith(t, "seh"))     return TraceGate.Eh;
                    break;
                case 'S':
                    if (StartsWith(t, "SFI"))     return TraceGate.Eh;
                    if (StartsWith(t, "SOS"))     return TraceGate.Eh;
                    break;
                case 'T':
                    if (StartsWith(t, "Tramp"))   return TraceGate.Thread;
                    break;
                case 'h':
                    if (StartsWith(t, "host"))    return TraceGate.Host;
                    break;
                case 'i':
                    if (StartsWith(t, "info"))    return TraceGate.Info;
                    break;
            }
            return TraceGate.Unknown;
        }

        // Emit a NUL-terminated UTF-8 chunk through the gate. Detects bracket
        // prefix at line-start and classifies via ClassifyHead.
        private static void GatedEmitString(byte* utf8)
        {
            byte* p = utf8;
            while (*p != 0)
            {
                if (s_needClassify)
                {
                    s_needClassify = false;
                    s_currentLineOn = (*p == (byte)'[') ? ClassifyHead(p) : TraceGate.Unknown;
                }
                if (s_currentLineOn) Console.WriteChar((char)*p);
                if (*p == (byte)'\n') s_needClassify = true;
                p++;
            }
        }

        // UTF-8 message, null-terminated. Writes one character at a time
        // to OS.Console (which routes to UEFI ConOut / kernel serial port).
        // Used by CRT walker для прогресса диагностики во время Phase 6.1.a.
        [RuntimeExport("SharpOSHost_DebugPrint")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DebugPrint")]
        public static void DebugPrint(byte* utf8Message)
        {
            if (!Verbose) return;
            if (utf8Message == null) return;
            GatedEmitString(utf8Message);
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
            // Console.Quiet AND TraceGate categories. Fork-side EH/CCF traces
            // use Forced; they're routed through the same per-category gate
            // so they can be silenced when not under investigation.
            if (Console.Quiet) return;
            GatedEmitString(utf8Message);
        }

        // Length-prefixed serial write — no NUL terminator required. Used by
        // the libSystem.Native console shim (SystemNative_Write) so managed
        // System.Console output reaches COM1 byte-exact (handles embedded
        // NULs / non-ASCII / no trailing zero).
        [RuntimeExport("SharpOSHost_DebugWrite")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DebugWrite")]
        public static void DebugWrite(byte* buf, int len)
        {
            // Length-prefixed path is the managed-System.Console output funnel
            // (SystemNative_Write). Always unconditional — this is program
            // output, not diagnostic chatter. Per-category gating would
            // swallow PowerShell stdout.
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
            // Hex chunks are mid-line continuations of an already-classified
            // [tag] header — respect s_currentLineOn so a Jit-off line stays
            // suppressed even when the hex part lands.
            if (!Verbose) return;
            if (!s_currentLineOn) return;
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
