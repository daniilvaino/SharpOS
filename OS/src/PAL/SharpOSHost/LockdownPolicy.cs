using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step126.8 — kernel-side policy for Windows Lockdown Policy (wldp.dll).
    // PowerShell queries WldpGetLockdownPolicy / WldpQueryDynamicCodeTrust /
    // WldpIsClassInApprovedList / WldpQueryWindowsLockdownMode to detect
    // AppLocker / WDAC / Constrained Language Mode. On unikernel there's
    // no admin/managed environment — we report "no policy / everything
    // allowed" so PowerShell runs in Full Language Mode with all features.
    //
    // All functions return S_OK + values indicating "no restrictions".
    internal static unsafe class LockdownPolicy
    {
        public const int S_OK = 0;

        // WLDP_LOCKDOWN_STATE values (wldp.h). DEFINED_FLAG must be set so
        // PS treats the state as "policy was successfully retrieved" rather
        // than "couldn't determine — fail-secure to ConstrainedLanguage".
        //   WLDP_LOCKDOWN_DEFINED_FLAG   = 0x80000000 — policy was retrieved
        //   WLDP_LOCKDOWN_USER_MODE_FLAG = 0x00000004 — user-mode enforce
        //   WLDP_LOCKDOWN_UMCI_AUDIT_FLAG= 0x00000010 — UMCI audit
        // We set ONLY DEFINED_FLAG: state was determined, no enforcement.
        public const uint WLDP_LOCKDOWN_DEFINED_FLAG = 0x80000000;
        public const uint WLDP_LOCKDOWN_OFF_DEFINED = WLDP_LOCKDOWN_DEFINED_FLAG;

        // WLDP_WINDOWS_LOCKDOWN_MODE values:
        public const uint WLDP_WINDOWS_LOCKDOWN_MODE_UNLOCKED = 0;

        [RuntimeExport("SharpOSHost_WldpGetLockdownPolicy")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_WldpGetLockdownPolicy")]
        public static int GetLockdownPolicy(uint* outLockdownState)
        {
            if (outLockdownState != null) *outLockdownState = WLDP_LOCKDOWN_OFF_DEFINED;
            return S_OK;
        }

        [RuntimeExport("SharpOSHost_WldpQueryDynamicCodeTrust")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_WldpQueryDynamicCodeTrust")]
        public static int QueryDynamicCodeTrust() => S_OK;  // trusted

        [RuntimeExport("SharpOSHost_WldpSetDynamicCodeTrust")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_WldpSetDynamicCodeTrust")]
        public static int SetDynamicCodeTrust() => S_OK;

        [RuntimeExport("SharpOSHost_WldpIsClassInApprovedList")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_WldpIsClassInApprovedList")]
        public static int IsClassInApprovedList(int* outIsApproved)
        {
            if (outIsApproved != null) *outIsApproved = 1;  // approved
            return S_OK;
        }

        [RuntimeExport("SharpOSHost_WldpQueryWindowsLockdownMode")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_WldpQueryWindowsLockdownMode")]
        public static int QueryWindowsLockdownMode(uint* outLockdownMode)
        {
            if (outLockdownMode != null) *outLockdownMode = WLDP_WINDOWS_LOCKDOWN_MODE_UNLOCKED;
            return S_OK;
        }

        [RuntimeExport("SharpOSHost_WldpIsDynamicCodePolicyEnabled")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_WldpIsDynamicCodePolicyEnabled")]
        public static int IsDynamicCodePolicyEnabled(int* outIsEnabled)
        {
            if (outIsEnabled != null) *outIsEnabled = 0;  // not enforced
            return S_OK;
        }

        [RuntimeExport("SharpOSHost_WldpCanExecuteFile")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_WldpCanExecuteFile")]
        public static int CanExecuteFile(int* outResult)
        {
            if (outResult != null) *outResult = 1;  // ALLOWED
            return S_OK;
        }
    }
}
