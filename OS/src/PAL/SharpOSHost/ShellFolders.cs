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

        // SHGetKnownFolderPath: caller expects HRESULT + PWSTR allocated
        // through CoTaskMemAlloc. We never write a path, just E_FAIL.
        [RuntimeExport("SharpOSHost_ShellGetKnownFolderPath")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_ShellGetKnownFolderPath")]
        public static int GetKnownFolderPath(void** outPathPtr)
        {
            if (outPathPtr != null) *outPathPtr = null;
            return E_FAIL;
        }

        // SHGetFolderPathW: caller provides MAX_PATH buffer. We clear it
        // and return E_FAIL.
        [RuntimeExport("SharpOSHost_ShellGetFolderPath")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_ShellGetFolderPath")]
        public static int GetFolderPath(char* pszPath)
        {
            if (pszPath != null) pszPath[0] = (char)0;
            return E_FAIL;
        }
    }
}
