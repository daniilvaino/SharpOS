using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step126.8 — kernel-side policy for shell32 known-folder lookups.
    // BCL Environment.GetFolderPath calls these for AppData / UserProfile /
    // ProgramData / Documents / etc. On unikernel none of these exist —
    // we return E_FAIL, BCL treats this as string.Empty.
    internal static unsafe class ShellFolders
    {
        // HRESULT codes:
        public const int S_OK    = 0;
        public const int E_FAIL  = unchecked((int)0x80004005);

        // Returning S_OK + synthetic path makes PS' SystemPolicy::
        // GetAppLockerPolicy walk deeper into SaferIdentifyLevel and
        // throw an uncaught SecurityException(0x80070006) → FailFast.
        // E_FAIL is the lesser evil: PS catches it, caches CLM for the
        // runspace, but stays alive. Real fix is to make the downstream
        // Safer/AppLocker probe chain consistent — separate work.
        [RuntimeExport("SharpOSHost_ShellGetKnownFolderPath")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_ShellGetKnownFolderPath")]
        public static int GetKnownFolderPath(void** outPathPtr)
        {
            if (outPathPtr != null) *outPathPtr = null;
            return E_FAIL;
        }

        [RuntimeExport("SharpOSHost_ShellGetFolderPath")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_ShellGetFolderPath")]
        public static int GetFolderPath(char* pszPath)
        {
            if (pszPath != null) pszPath[0] = (char)0;
            return E_FAIL;
        }
    }
}
