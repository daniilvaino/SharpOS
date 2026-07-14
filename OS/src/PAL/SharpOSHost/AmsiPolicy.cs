using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step126.9 — kernel-side policy for AMSI (Antimalware Scan Interface).
    // PowerShell on Windows 10+ scans every script/expression/module content
    // through AMSI before executing — it's a security hook that lets
    // antivirus engines (Windows Defender, third-party) approve or block
    // content based on policy.
    //
    // On unikernel there's no antivirus, no policy server, no concept of
    // malicious content. Every scan returns AMSI_RESULT_CLEAN (0) which
    // means "no malware detected, proceed". PowerShell accepts this and
    // executes the script.
    //
    // Functions used by PowerShell:
    //   AmsiInitialize     — create a HAMSICONTEXT, return S_OK
    //   AmsiUninitialize   — release context
    //   AmsiOpenSession    — create a HAMSISESSION inside a context
    //   AmsiCloseSession   — release session
    //   AmsiScanString     — scan a wide string, write result
    //   AmsiScanBuffer     — scan binary data, write result
    //
    // AMSI_RESULT values (amsi.h):
    //   AMSI_RESULT_CLEAN              = 0
    //   AMSI_RESULT_NOT_DETECTED       = 1
    //   AMSI_RESULT_BLOCKED_BY_ADMIN_START = 0x4000
    //   AMSI_RESULT_BLOCKED_BY_ADMIN_END   = 0x4FFF
    //   AMSI_RESULT_DETECTED           = 0x8000
    internal static unsafe class AmsiPolicy
    {
        public const int S_OK = 0;

        public const uint AMSI_RESULT_CLEAN        = 0;
        public const uint AMSI_RESULT_NOT_DETECTED = 1;

        // Sentinel handle values for HAMSICONTEXT / HAMSISESSION. Not real
        // pointers — fork wraps them as void*.
        public const ulong AmsiContextSentinel = 0xA751C0DE_C0FE_BEEFUL;
        public const ulong AmsiSessionSentinel = 0xA751C0DE_5E55_BEEFUL;

        [RuntimeExport("SharpOSHost_AmsiInitialize")]
        public static int Initialize(ulong* outContext)
        {
            if (outContext != null) *outContext = AmsiContextSentinel;
            return S_OK;
        }

        [RuntimeExport("SharpOSHost_AmsiUninitialize")]
        public static void Uninitialize() { /* no-op */ }

        [RuntimeExport("SharpOSHost_AmsiOpenSession")]
        public static int OpenSession(ulong* outSession)
        {
            if (outSession != null) *outSession = AmsiSessionSentinel;
            return S_OK;
        }

        [RuntimeExport("SharpOSHost_AmsiCloseSession")]
        public static void CloseSession() { /* no-op */ }

        [RuntimeExport("SharpOSHost_AmsiScan")]
        public static int Scan(uint* outResult)
        {
            // Single entry covers both ScanString and ScanBuffer — fork
            // shims pass the same kernel call. Always clean.
            if (outResult != null) *outResult = AMSI_RESULT_CLEAN;
            return S_OK;
        }

        // AmsiNotifyOperation / AmsiNotifyOperationA — PowerShell 7+ uses
        // these to notify AMSI of generic operations (script loads, module
        // load events). Unikernel has no AMSI engine; always S_OK.
        [RuntimeExport("SharpOSHost_AmsiNotifyOperation")]
        public static int NotifyOperation() => S_OK;
    }
}
