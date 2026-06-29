using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step126.11 — kernel-side policy for user32 system-wide UI/accessibility
    // settings. PowerShell init queries SystemParametersInfo for things
    // like SPI_GETSCREENREADER (is a screen reader running?), SPI_GETBLINKRATE,
    // SPI_GETKEYBOARDCUES, SPI_GETHIGHCONTRAST — to adapt console output.
    //
    // On unikernel there's no Windows accessibility framework — all queries
    // succeed with zero output (no screen reader, no high contrast, normal
    // defaults). PowerShell sees "regular environment" and proceeds.
    //
    // SystemParametersInfo signature:
    //   BOOL SystemParametersInfo(UINT action, UINT param, PVOID pvParam, UINT fWinIni)
    // Returns TRUE on success. pvParam is action-dependent — usually a
    // pointer to BOOL / UINT / struct that we should write a default to.
    // We zero the first 32 bytes which covers BOOL, UINT, and most small
    // structs the accessibility queries use.
    internal static unsafe class UserUiPolicy
    {
        [RuntimeExport("SharpOSHost_SystemParametersInfo")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_SystemParametersInfo")]
        public static int SystemParametersInfo(uint action, uint param, byte* pvParam, uint fWinIni)
        {
            _ = action; _ = param; _ = fWinIni;
            // Zero out output buffer (up to 32 bytes — covers HIGHCONTRAST
            // (12 bytes), BOOL (4), UINT (4), most accessibility query types).
            if (pvParam != null)
            {
                for (int i = 0; i < 32; i++) pvParam[i] = 0;
            }
            return 1;  // TRUE
        }

        // GetSystemMetrics — width/height/border etc. Return 0 for everything
        // (no display); PowerShell uses this rarely but some code paths
        // check screen width to format help text.
        [RuntimeExport("SharpOSHost_GetSystemMetrics")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetSystemMetrics")]
        public static int GetSystemMetrics(int nIndex)
        {
            _ = nIndex;
            return 0;
        }

        // GetConsoleWindow — returns HWND of console window. Null indicates
        // "no associated window" which is the truth for our setup.
        [RuntimeExport("SharpOSHost_GetConsoleWindow")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetConsoleWindow")]
        public static void* GetConsoleWindow() => null;
    }
}
