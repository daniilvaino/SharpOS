using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step126.8 — kernel-side policy for advapi32 Token/Privilege/SID surface.
    // ProcessManager / WindowsIdentity / .cctor's try to acquire SE_DEBUG_NAME
    // and inspect security tokens. On unikernel there is no security
    // subsystem — every operation reports controlled failure with a
    // sensible Win32 LastError, so BCL catches it as Win32Exception /
    // UnauthorizedAccessException rather than crashing.
    //
    // Per SharpOS invariant: fork is a passthrough shim, all error codes
    // and decisions live here. Return tuple (success_bool, last_error_code)
    // packed into a single int: low 8 bits = bool, high 24 bits = error.
    //
    // For simpler functions returning just a code, we return Win32 error
    // code directly (caller treats non-zero as failure).
    internal static unsafe class TokenSecurity
    {
        // Win32 error codes (WinError.h):
        public const int ERROR_SUCCESS         = 0;
        public const int ERROR_INVALID_HANDLE  = 6;
        public const int ERROR_ACCESS_DENIED   = 5;
        public const int ERROR_NO_SUCH_PRIVILEGE = 1313;

        // LookupPrivilegeValueW: looks up a LUID for a privilege name (e.g.
        // "SeDebugPrivilege"). On unikernel no privileges exist — clear
        // output LUID and report ERROR_NO_SUCH_PRIVILEGE.
        [RuntimeExport("SharpOSHost_LookupPrivilegeValue")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_LookupPrivilegeValue")]
        public static int LookupPrivilegeValue(ulong* outLuid)
        {
            if (outLuid != null) *outLuid = 0;
            return ERROR_NO_SUCH_PRIVILEGE;
        }

        // LookupPrivilegeNameW: reverse mapping. Same answer.
        [RuntimeExport("SharpOSHost_LookupPrivilegeName")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_LookupPrivilegeName")]
        public static int LookupPrivilegeName(uint* outNameLen)
        {
            if (outNameLen != null) *outNameLen = 0;
            return ERROR_NO_SUCH_PRIVILEGE;
        }

        // OpenProcessToken / OpenThreadToken: no tokens on unikernel.
        // Return failure with ERROR_INVALID_HANDLE so BCL surface it as
        // catchable Win32Exception.
        [RuntimeExport("SharpOSHost_OpenProcessToken")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_OpenProcessToken")]
        public static int OpenProcessToken(void** outToken)
        {
            if (outToken != null) *outToken = null;
            return ERROR_INVALID_HANDLE;
        }

        [RuntimeExport("SharpOSHost_OpenThreadToken")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_OpenThreadToken")]
        public static int OpenThreadToken(void** outToken)
        {
            if (outToken != null) *outToken = null;
            return ERROR_INVALID_HANDLE;
        }

        [RuntimeExport("SharpOSHost_AdjustTokenPrivileges")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_AdjustTokenPrivileges")]
        public static int AdjustTokenPrivileges() => ERROR_INVALID_HANDLE;

        [RuntimeExport("SharpOSHost_GetTokenInformation")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetTokenInformation")]
        public static int GetTokenInformation(uint* outReturnLen)
        {
            if (outReturnLen != null) *outReturnLen = 0;
            return ERROR_INVALID_HANDLE;
        }

        [RuntimeExport("SharpOSHost_ImpersonateLoggedOnUser")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_ImpersonateLoggedOnUser")]
        public static int ImpersonateLoggedOnUser() => ERROR_INVALID_HANDLE;

        // RevertToSelf: succeeds trivially. There is no impersonation to
        // revert from in our single-process world.
        [RuntimeExport("SharpOSHost_RevertToSelf")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RevertToSelf")]
        public static int RevertToSelf() => ERROR_SUCCESS;  // BOOL=1 mapping handled in fork

        [RuntimeExport("SharpOSHost_CheckTokenMembership")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_CheckTokenMembership")]
        public static int CheckTokenMembership(int* outIsMember)
        {
            if (outIsMember != null) *outIsMember = 0;
            return ERROR_INVALID_HANDLE;
        }

        [RuntimeExport("SharpOSHost_DuplicateTokenEx")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_DuplicateTokenEx")]
        public static int DuplicateTokenEx(void** outNewToken)
        {
            if (outNewToken != null) *outNewToken = null;
            return ERROR_INVALID_HANDLE;
        }
    }
}
