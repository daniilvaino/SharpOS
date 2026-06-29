using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step125 — minimal in-memory Windows Registry surface for advapi32
    // forwarding. PowerShell on bare metal reaches Group Policy lookup at
    // boot (Microsoft.Win32.Registry → advapi32 RegOpenKeyExW / RegQueryValueExW
    // / RegEnumKeyExW / ...); without a controlled response it derefs null and
    // halts (seen in step124 trace: CommandLineParameterParser::
    // GetConfigurationNameFromGroupPolicy → GetPolicySettingFromGPOImpl #PF).
    //
    // Strategy: **empty registry**. Well-known root HKEY values
    // (HKLM/HKCU/HKCR/HKU/HKCC) are recognised — RegOpenKeyExW with an
    // empty subKey returns the same root handle. Any actual subkey
    // returns ERROR_FILE_NOT_FOUND. All value queries return
    // ERROR_FILE_NOT_FOUND. All enumerations return ERROR_NO_MORE_ITEMS.
    // RegCloseKey is a no-op success.
    //
    // This satisfies the contract for read-only "does this policy exist"
    // probes — which is what PowerShell does during boot — and for any
    // Win32-aware managed code that asks "is HKLM\Software\X present?"
    // and falls back to defaults when the answer is no.
    //
    // **What this does NOT do:** persistent KV store (no write/read
    // round-trip), permissions/ACL, value type round-tripping,
    // notification, transactions. Those land in step126+ if needed.
    //
    // **Policy split.** Per SharpOS invariant: PAL stubs in the fork are
    // thin ABI marshallers (wchar→UTF-8, length protocol, struct field
    // packing). All decisions ("which root is valid", "do we have this
    // subkey", "what error code to return") live HERE.
    //
    // String marshalling: registry paths are ASCII in practice (HKLM\
    // Software\Microsoft\..., etc.). The fork converts wide-char input
    // to byte[] by zero-extension; we operate on UTF-8 bytes.
    internal static unsafe class Registry
    {
        // Win32 root HKEY constants — see WinReg.h. These are the sign-
        // extended 32-bit pseudo-handles BCL passes through.
        // On 64-bit systems the upper 32 bits may or may not be set; we
        // mask to lower 32 bits before compare.
        public const uint HKEY_CLASSES_ROOT     = 0x80000000;
        public const uint HKEY_CURRENT_USER     = 0x80000001;
        public const uint HKEY_LOCAL_MACHINE    = 0x80000002;
        public const uint HKEY_USERS            = 0x80000003;
        public const uint HKEY_PERFORMANCE_DATA = 0x80000004;
        public const uint HKEY_CURRENT_CONFIG   = 0x80000005;
        public const uint HKEY_DYN_DATA         = 0x80000006;

        // Win32 LSTATUS / error codes — see WinError.h. RegistryAPIs
        // return these as int (not HRESULT).
        public const int ERROR_SUCCESS         = 0;
        public const int ERROR_FILE_NOT_FOUND  = 2;
        public const int ERROR_INVALID_HANDLE  = 6;
        public const int ERROR_NO_MORE_ITEMS   = 259;
        public const int ERROR_MORE_DATA       = 234;

        // Returns true if the given handle is a recognised well-known
        // Win32 root HKEY. Anything we returned from RegOpenKey is also
        // a root handle (we don't allocate per-key state).
        private static bool IsKnownRoot(ulong hKey)
        {
            uint lo = (uint)(hKey & 0xFFFFFFFFu);
            return lo == HKEY_CLASSES_ROOT
                || lo == HKEY_CURRENT_USER
                || lo == HKEY_LOCAL_MACHINE
                || lo == HKEY_USERS
                || lo == HKEY_PERFORMANCE_DATA
                || lo == HKEY_CURRENT_CONFIG
                || lo == HKEY_DYN_DATA;
        }

        // RegOpenKeyExW(hKey, lpSubKey, ulOptions, samDesired, phkResult)
        // BCL uses this everywhere — RegistryKey.OpenSubKey, group policy
        // probes, etc.
        //
        // Behaviour:
        //   - hKey must be a well-known root (anything else → ERROR_INVALID_HANDLE)
        //   - empty subKey (len==0 or first byte 0) → return same handle as
        //     opened (phkResult = hKey), SUCCESS
        //   - non-empty subKey → ERROR_FILE_NOT_FOUND, phkResult=0
        //
        // The fork zero-extends wide subKey into the byte buffer before
        // calling us; subKeyLen is the byte length (not wchar count).
        [RuntimeExport("SharpOSHost_RegOpenKey")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RegOpenKey")]
        public static int RegOpenKey(ulong hKey, byte* subKey, int subKeyLen, ulong* phkResult)
        {
            if (phkResult != null) *phkResult = 0;
            if (!IsKnownRoot(hKey)) return ERROR_INVALID_HANDLE;
            if (subKey == null || subKeyLen == 0 || subKey[0] == 0)
            {
                if (phkResult != null) *phkResult = hKey;
                return ERROR_SUCCESS;
            }
            // Empty registry — no real subkeys exist.
            return ERROR_FILE_NOT_FOUND;
        }

        // RegCloseKey(hKey) — always success. We don't allocate per-key
        // state; close is just a contract.
        [RuntimeExport("SharpOSHost_RegCloseKey")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RegCloseKey")]
        public static int RegCloseKey(ulong hKey)
        {
            return ERROR_SUCCESS;
        }

        // RegQueryValueExW(hKey, lpValueName, lpReserved, lpType, lpData, lpcbData)
        // Reads a named value from a key. Empty registry → all queries miss.
        //
        // We accept the call (return appropriate error) — no marshalling
        // of value data needed since we don't return any.
        [RuntimeExport("SharpOSHost_RegQueryValue")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RegQueryValue")]
        public static int RegQueryValue(
            ulong hKey, byte* valueName, int valueNameLen,
            uint* outType, byte* outData, uint* outDataLen)
        {
            if (!IsKnownRoot(hKey)) return ERROR_INVALID_HANDLE;
            // Value does not exist.
            if (outType    != null) *outType    = 0;
            if (outDataLen != null) *outDataLen = 0;
            return ERROR_FILE_NOT_FOUND;
        }

        // RegEnumKeyExW(hKey, dwIndex, ...) — empty enum.
        [RuntimeExport("SharpOSHost_RegEnumKey")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RegEnumKey")]
        public static int RegEnumKey(
            ulong hKey, uint dwIndex,
            byte* outName, uint* outNameLen,
            byte* outClass, uint* outClassLen,
            long* outLastWriteTime)
        {
            if (!IsKnownRoot(hKey)) return ERROR_INVALID_HANDLE;
            if (outName    != null && outNameLen != null) *outNameLen = 0;
            if (outClass   != null && outClassLen != null) *outClassLen = 0;
            if (outLastWriteTime != null) *outLastWriteTime = 0;
            return ERROR_NO_MORE_ITEMS;
        }

        // RegEnumValueW(hKey, dwIndex, ...) — empty enum.
        [RuntimeExport("SharpOSHost_RegEnumValue")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RegEnumValue")]
        public static int RegEnumValue(
            ulong hKey, uint dwIndex,
            byte* outName, uint* outNameLen,
            uint* outType,
            byte* outData, uint* outDataLen)
        {
            if (!IsKnownRoot(hKey)) return ERROR_INVALID_HANDLE;
            if (outName    != null && outNameLen != null) *outNameLen = 0;
            if (outType    != null) *outType    = 0;
            if (outDataLen != null) *outDataLen = 0;
            return ERROR_NO_MORE_ITEMS;
        }

        // RegQueryInfoKeyW(hKey, ...) — return zero counts. PowerShell
        // calls this to learn key cardinality before enumerating; an
        // empty key is a valid answer that satisfies the contract.
        [RuntimeExport("SharpOSHost_RegQueryInfoKey")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RegQueryInfoKey")]
        public static int RegQueryInfoKey(
            ulong hKey,
            byte* outClass, uint* outClassLen,
            uint* outNumSubKeys, uint* outMaxSubKeyLen, uint* outMaxClassLen,
            uint* outNumValues, uint* outMaxValueNameLen, uint* outMaxValueDataLen,
            uint* outSecurityDescriptor, long* outLastWriteTime)
        {
            if (!IsKnownRoot(hKey)) return ERROR_INVALID_HANDLE;
            if (outClass != null && outClassLen != null) *outClassLen = 0;
            if (outNumSubKeys       != null) *outNumSubKeys       = 0;
            if (outMaxSubKeyLen     != null) *outMaxSubKeyLen     = 0;
            if (outMaxClassLen      != null) *outMaxClassLen      = 0;
            if (outNumValues        != null) *outNumValues        = 0;
            if (outMaxValueNameLen  != null) *outMaxValueNameLen  = 0;
            if (outMaxValueDataLen  != null) *outMaxValueDataLen  = 0;
            if (outSecurityDescriptor != null) *outSecurityDescriptor = 0;
            if (outLastWriteTime    != null) *outLastWriteTime    = 0;
            return ERROR_SUCCESS;
        }

        // RegCreateKeyExW — empty registry doesn't accept writes. We
        // could return ERROR_ACCESS_DENIED but ERROR_FILE_NOT_FOUND is
        // closer to "this hive is read-only and empty".
        [RuntimeExport("SharpOSHost_RegCreateKey")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RegCreateKey")]
        public static int RegCreateKey(
            ulong hKey, byte* subKey, int subKeyLen,
            uint reserved, byte* keyClass, uint options, uint samDesired,
            void* securityAttrs, ulong* phkResult, uint* outDisposition)
        {
            if (phkResult != null) *phkResult = 0;
            if (outDisposition != null) *outDisposition = 0;
            if (!IsKnownRoot(hKey)) return ERROR_INVALID_HANDLE;
            return ERROR_FILE_NOT_FOUND;
        }

        // RegFlushKey — no-op for empty registry.
        [RuntimeExport("SharpOSHost_RegFlushKey")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RegFlushKey")]
        public static int RegFlushKey(ulong hKey)
        {
            if (!IsKnownRoot(hKey)) return ERROR_INVALID_HANDLE;
            return ERROR_SUCCESS;
        }
    }
}
