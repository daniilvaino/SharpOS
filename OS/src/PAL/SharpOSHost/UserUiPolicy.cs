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
    // Returns TRUE on success. pvParam is action-dependent and its real
    // marshaled size is owned by the caller, so do not blindly clear a fixed
    // range here.
    internal static unsafe class UserUiPolicy
    {
        private const uint SPI_GETICONTITLELOGFONT = 0x1F;
        private const uint SPI_GETNONCLIENTMETRICS = 0x29;
        private const uint SPI_GETHIGHCONTRAST = 0x42;

        [RuntimeExport("SharpOSHost_SystemParametersInfo")]
        public static int SystemParametersInfo(uint action, uint param, byte* pvParam, uint fWinIni)
        {
            _ = fWinIni;
            if (pvParam != null)
            {
                switch (action)
                {
                    case SPI_GETHIGHCONTRAST:
                        // HIGHCONTRAST starts with cbSize; preserve it and
                        // clear dwFlags. The optional scheme pointer follows.
                        if (param >= 8)
                        {
                            ZeroBytes(pvParam + 4, 4);
                        }
                        else
                        {
                            pvParam[0] = 0;
                            break;
                        }
                        if (param >= 16)
                        {
                            ZeroBytes(pvParam + 8, 8);
                        }
                        break;

                    case SPI_GETICONTITLELOGFONT:
                    case SPI_GETNONCLIENTMETRICS:
                        // These callers pass a structure size in uiParam.
                        // Cap the clear so a bad size cannot damage a frame.
                        ZeroBytes(pvParam, ClampStructSize(param));
                        break;

                    default:
                        // Many PowerShell/user32 queries marshal pvParam as a
                        // one-byte bool. Clearing more can overwrite the
                        // P/Invoke stub's saved nonvolatile registers.
                        pvParam[0] = 0;
                        break;
                }
            }
            return 1;  // TRUE
        }

        private static int ClampStructSize(uint size)
        {
            if (size == 0) return 1;
            if (size > 256) return 256;
            return (int)size;
        }

        private static void ZeroBytes(byte* ptr, int length)
        {
            for (int i = 0; i < length; i++)
            {
                ptr[i] = 0;
            }
        }

        // GetSystemMetrics — width/height/border etc. Return 0 for everything
        // (no display); PowerShell uses this rarely but some code paths
        // check screen width to format help text.
        [RuntimeExport("SharpOSHost_GetSystemMetrics")]
        public static int GetSystemMetrics(int nIndex)
        {
            _ = nIndex;
            return 0;
        }

        // GetConsoleWindow — returns HWND of console window. Null indicates
        // "no associated window" which is the truth for our setup.
        [RuntimeExport("SharpOSHost_GetConsoleWindow")]
        public static void* GetConsoleWindow() => null;
    }
}
