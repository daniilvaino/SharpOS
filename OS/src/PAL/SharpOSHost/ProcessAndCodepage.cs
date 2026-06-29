using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step126.13 — kernel-side policy for kernel32 OpenProcess + GetCPInfoEx
    // and advapi32 LookupAccountNameW. Touched during PowerShell Runspace
    // creation (process listing, locale resolution, account-name resolution).
    // On unikernel:
    //   - OpenProcess: only "current process" makes sense; any other PID
    //     returns null handle + ERROR_INVALID_PARAMETER.
    //   - GetCPInfoExW: codepage info — we use invariant globalization, so
    //     return failure; BCL falls back to ASCII semantics.
    //   - LookupAccountNameW: no SAM/AD database; report ERROR_NONE_MAPPED.
    internal static unsafe class ProcessAndCodepage
    {
        public const int ERROR_SUCCESS         = 0;
        public const int ERROR_INVALID_PARAMETER = 87;
        public const int ERROR_NONE_MAPPED      = 1332;
        public const int ERROR_INVALID_FUNCTION = 1;

        // OpenProcess(DesiredAccess, InheritHandle, dwProcessId) — return
        // pseudo-handle for our single process (always PID=1), null for
        // anything else.
        [RuntimeExport("SharpOSHost_OpenProcess")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_OpenProcess")]
        public static void* OpenProcess(uint dwProcessId)
        {
            // PID 1 = current process (matches our GetCurrentProcessId).
            // BCL Process.GetCurrentProcess returns -1 as pseudo-handle;
            // OpenProcess of PID 1 returns the same pseudo-value.
            if (dwProcessId == 1) return (void*)(nint)(-1);
            return null;
        }

        // GetCPInfoExW(uiCodePage, dwFlags, lpCPInfoEx) — returns extended
        // info struct (maxCharSize, defaultChar, leadByte ranges, codepage,
        // name). With invariant globalization we always return failure.
        // BCL surfaces this as platform fallback to ASCII / UTF-8.
        [RuntimeExport("SharpOSHost_GetCPInfoEx")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetCPInfoEx")]
        public static int GetCPInfoEx()
        {
            return ERROR_INVALID_FUNCTION;  // BCL handles as fallback
        }

        // K32EnumProcesses — fork writes pid=1 into caller buffer if it has
        // room for at least one DWORD, sets *needed accordingly. Kernel
        // decides "we have one process, id=1". Fork marshals into Win32 buffer.
        [RuntimeExport("SharpOSHost_EnumProcesses")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_EnumProcesses")]
        public static int EnumProcesses(uint* outPid)
        {
            if (outPid != null) *outPid = 1;
            return 1;  // count of processes
        }

        // LookupAccountNameW — translate account name (e.g. "Administrator",
        // "DOMAIN\user") to SID. On unikernel there's no SAM database,
        // every lookup fails with ERROR_NONE_MAPPED.
        [RuntimeExport("SharpOSHost_LookupAccountName")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_LookupAccountName")]
        public static int LookupAccountName(uint* outSidSize, uint* outDomainSize, int* outUse)
        {
            if (outSidSize    != null) *outSidSize    = 0;
            if (outDomainSize != null) *outDomainSize = 0;
            if (outUse        != null) *outUse        = 0;
            return ERROR_NONE_MAPPED;
        }
    }
}
