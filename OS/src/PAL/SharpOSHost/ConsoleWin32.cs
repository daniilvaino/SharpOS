using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step126 — Win32 console facade for kernel32 Console APIs that
    // Windows-impl System.Console.dll calls during early init.
    // PowerShell, Microsoft.Win32.Registry static init, Console.Write
    // probe lines — all of these route through GetStdHandle +
    // WriteConsoleW / WriteFile / GetConsoleMode / GetConsoleScreenBufferInfo.
    //
    // Backing: stdout/stderr → existing SharpOSHost_DebugWrite (UART).
    // stdin → not interactive yet (returns empty / zero bytes).
    //
    // Per SharpOS invariant: all policy ("what cursor position to report",
    // "what color attributes are valid", "what console mode is enabled")
    // lives HERE; the fork side is a thin marshalling shim.
    //
    // Handle values: small magic constants distinguishable from real
    // pointers — the fork shim passes them through, and our managed code
    // matches by value (not memory address). Picked outside any real
    // memory range so accidental misuse is detectable.
    internal static unsafe class ConsoleWin32
    {
        // Win32 STD_*_HANDLE values per GetStdHandle docs
        public const int STD_INPUT_HANDLE  = -10;  // (DWORD)-10 = 0xFFFFFFF6
        public const int STD_OUTPUT_HANDLE = -11;
        public const int STD_ERROR_HANDLE  = -12;

        // Our pseudo-handle sentinels. High bits set so they won't clash
        // with allocated heap pointers (those start ~0x500000xxxxxx in our
        // VA space). Low byte identifies which stream.
        public const ulong HandleStdIn  = 0x0000_BEEF_C0DE_0001UL;
        public const ulong HandleStdOut = 0x0000_BEEF_C0DE_0002UL;
        public const ulong HandleStdErr = 0x0000_BEEF_C0DE_0003UL;

        // Win32 file type for GetFileType
        public const uint FILE_TYPE_UNKNOWN = 0x0000;
        public const uint FILE_TYPE_DISK    = 0x0001;
        public const uint FILE_TYPE_CHAR    = 0x0002;
        public const uint FILE_TYPE_PIPE    = 0x0003;

        // Win32 console mode flags (subset). We accept any combination and
        // claim it's set — Console BCL only checks ENABLE_PROCESSED_OUTPUT
        // and ENABLE_VIRTUAL_TERMINAL_PROCESSING bits typically.
        public const uint ENABLE_PROCESSED_OUTPUT             = 0x0001;
        public const uint ENABLE_WRAP_AT_EOL_OUTPUT           = 0x0002;
        public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING  = 0x0004;
        public const uint ENABLE_DISABLE_NEWLINE_AUTO_RETURN  = 0x0008;
        public const uint ENABLE_LVB_GRID_WORLDWIDE           = 0x0010;
        // Input modes (irrelevant for stdout/stderr but BCL queries them)
        public const uint ENABLE_ECHO_INPUT       = 0x0004;
        public const uint ENABLE_LINE_INPUT       = 0x0002;
        public const uint ENABLE_PROCESSED_INPUT  = 0x0001;

        // Synthetic console dimensions — match a reasonable terminal.
        public const short DefaultCols   = 80;
        public const short DefaultRows   = 25;

        // GetStdHandle(DWORD nStdHandle) → HANDLE
        // BCL Console.OpenStandardOutput/Error/Input all call this on init.
        [RuntimeExport("SharpOSHost_GetStdHandle")]
        public static ulong GetStdHandle(int nStdHandle)
        {
            if (nStdHandle == STD_INPUT_HANDLE)  return HandleStdIn;
            if (nStdHandle == STD_OUTPUT_HANDLE) return HandleStdOut;
            if (nStdHandle == STD_ERROR_HANDLE)  return HandleStdErr;
            return 0; // INVALID_HANDLE_VALUE-ish; BCL will probe and handle
        }

        // Returns true if the handle is one of our std stream sentinels.
        private static bool IsStdHandle(ulong h)
        {
            return h == HandleStdIn || h == HandleStdOut || h == HandleStdErr;
        }

        // WriteConsoleW — converts UTF-16 input to UTF-8 byte stream and
        // forwards to existing UART writer. BCL uses this for Console.Write,
        // Console.WriteLine when the underlying stream identifies as a
        // console (GetFileType=CHAR + GetConsoleMode returns success).
        //
        // BCL signature: BOOL WriteConsoleW(HANDLE, const VOID*, DWORD,
        //                                    LPDWORD numWritten, LPVOID reserved)
        // Returns: 1 on success, 0 on failure (caller checks GetLastError).
        [RuntimeExport("SharpOSHost_ConsoleWriteW")]
        public static int WriteConsoleW(ulong hConsole, char* buffer, uint nChars, uint* numCharsWritten)
        {
            if (numCharsWritten != null) *numCharsWritten = 0;
            if (buffer == null || nChars == 0) return 1; // empty write succeeds
            if (!IsStdHandle(hConsole)) return 0;
            // Convert UTF-16 → bytes (BMP only — surrogate pairs would
            // produce replacement chars; acceptable for kernel console).
            for (uint i = 0; i < nChars; i++)
            {
                char c = buffer[i];
                if (c < 0x80)
                {
                    OS.Hal.Platform.WriteChar((char)c);
                }
                else if (c < 0x800)
                {
                    OS.Hal.Platform.WriteChar((char)(0xC0 | (c >> 6)));
                    OS.Hal.Platform.WriteChar((char)(0x80 | (c & 0x3F)));
                }
                else
                {
                    OS.Hal.Platform.WriteChar((char)(0xE0 | (c >> 12)));
                    OS.Hal.Platform.WriteChar((char)(0x80 | ((c >> 6) & 0x3F)));
                    OS.Hal.Platform.WriteChar((char)(0x80 | (c & 0x3F)));
                }
            }
            if (numCharsWritten != null) *numCharsWritten = nChars;
            return 1;
        }

        // WriteFile to console handle (BCL FileStream backend uses this
        // when console identifies as CHAR file type).
        //
        // BCL signature: BOOL WriteFile(HANDLE, LPCVOID, DWORD,
        //                                LPDWORD numWritten, LPOVERLAPPED)
        [RuntimeExport("SharpOSHost_ConsoleWriteFile")]
        public static int WriteFile(ulong hHandle, byte* buffer, uint nBytes, uint* numBytesWritten)
        {
            if (numBytesWritten != null) *numBytesWritten = 0;
            if (buffer == null || nBytes == 0) return 1;
            if (!IsStdHandle(hHandle)) return 0;
            for (uint i = 0; i < nBytes; i++)
                OS.Hal.Platform.WriteChar((char)buffer[i]);
            if (numBytesWritten != null) *numBytesWritten = nBytes;
            return 1;
        }

        // GetConsoleMode(HANDLE, LPDWORD) — return reasonable default.
        // BCL checks for processed-output / virtual-terminal-processing
        // flags before deciding to emit ANSI escape sequences.
        [RuntimeExport("SharpOSHost_GetConsoleMode")]
        public static int GetConsoleMode(ulong hConsole, uint* outMode)
        {
            if (outMode == null) return 0;
            *outMode = 0;
            if (!IsStdHandle(hConsole)) return 0;
            if (hConsole == HandleStdIn)
                *outMode = ENABLE_PROCESSED_INPUT | ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT;
            else
                *outMode = ENABLE_PROCESSED_OUTPUT | ENABLE_WRAP_AT_EOL_OUTPUT
                         | ENABLE_VIRTUAL_TERMINAL_PROCESSING;
            return 1;
        }

        // SetConsoleMode — accept any, no-op success. Our UART doesn't have
        // distinct modes; BCL gets the courtesy of "yes, I set it".
        [RuntimeExport("SharpOSHost_SetConsoleMode")]
        public static int SetConsoleMode(ulong hConsole, uint mode)
        {
            return IsStdHandle(hConsole) ? 1 : 0;
        }

        // GetFileType — used by BCL to decide if a handle is a console
        // (CHAR), file (DISK), or pipe. Console handles report CHAR so
        // BCL takes the WriteConsoleW path; file handles get DISK.
        [RuntimeExport("SharpOSHost_GetFileType")]
        public static uint GetFileType(ulong hHandle)
        {
            if (IsStdHandle(hHandle)) return FILE_TYPE_CHAR;
            // Any other handle — assume disk (BCL FileStream backend).
            return FILE_TYPE_DISK;
        }

        // GetConsoleScreenBufferInfo — used by Console.WindowWidth /
        // .WindowHeight / .CursorTop / .CursorLeft / .BufferWidth / .BufferHeight.
        // We synthesize a stable 80x25 buffer with cursor at top-left.
        // BCL never actually positions our cursor — it just reads what we
        // claim. The output struct is CONSOLE_SCREEN_BUFFER_INFO:
        //   short dwSizeX, dwSizeY;            // +0, +2
        //   short dwCursorPositionX, dwCursorPositionY;  // +4, +6
        //   ushort wAttributes;                // +8
        //   short srWindowLeft, srWindowTop, srWindowRight, srWindowBottom;  // +10..+16
        //   short dwMaxWindowSizeX, dwMaxWindowSizeY;    // +18, +20
        // Total: 22 bytes.
        [RuntimeExport("SharpOSHost_GetConsoleScreenBufferInfo")]
        public static int GetConsoleScreenBufferInfo(ulong hConsole, void* outInfo)
        {
            if (outInfo == null) return 0;
            if (!IsStdHandle(hConsole)) return 0;
            short* p = (short*)outInfo;
            p[0] = DefaultCols;  // dwSize.X (buffer width)
            p[1] = DefaultRows;  // dwSize.Y (buffer height)
            p[2] = 0;            // cursor X
            p[3] = 0;            // cursor Y
            ((ushort*)outInfo)[4] = 0x0007; // wAttributes (light gray on black)
            p[5] = 0;            // srWindow.Left
            p[6] = 0;            // srWindow.Top
            p[7] = (short)(DefaultCols - 1);  // srWindow.Right
            p[8] = (short)(DefaultRows - 1);  // srWindow.Bottom
            p[9]  = DefaultCols; // dwMaxWindowSize.X
            p[10] = DefaultRows; // dwMaxWindowSize.Y
            return 1;
        }

        // SetConsoleCursorPosition — no-op (UART has no positionable cursor).
        // Returns success so BCL doesn't propagate failure.
        [RuntimeExport("SharpOSHost_SetConsoleCursorPosition")]
        public static int SetConsoleCursorPosition(ulong hConsole, int packedCoord)
        {
            return IsStdHandle(hConsole) ? 1 : 0;
        }

        // SetConsoleTextAttribute — no-op success.
        [RuntimeExport("SharpOSHost_SetConsoleTextAttribute")]
        public static int SetConsoleTextAttribute(ulong hConsole, ushort attrs)
        {
            return IsStdHandle(hConsole) ? 1 : 0;
        }

        // SetConsoleCtrlHandler — Ctrl+C/Ctrl+Break/Ctrl+Close handler
        // registration. PowerShell registers a BreakHandler to translate
        // Ctrl+C presses into PipelineStopException. On our UART there's
        // no Ctrl+C signal source yet, so we accept the registration but
        // it stays dormant — when we wire keyboard scancode -> Ctrl+C
        // detection later, we'll invoke the registered handler. For now
        // return TRUE so PowerShell init proceeds.
        [RuntimeExport("SharpOSHost_SetConsoleCtrlHandler")]
        public static int SetConsoleCtrlHandler() => 1;  // success

        // GetStartupInfoW — populates STARTUPINFOW struct with process
        // startup data (window title, cmd line, std handle inheritance,
        // showWindow flag, etc.). On unikernel single-process model:
        // all-zero is the canonical "default startup" — cb=sizeof(struct)
        // is the only required write; PowerShell only reads dwFlags to
        // detect console-redirection inheritance which we don't use.
        // Caller passes pointer + sizeOf to write.
        [RuntimeExport("SharpOSHost_GetStartupInfo")]
        public static void GetStartupInfo(uint* lpStartupInfo, uint structSize)
        {
            if (lpStartupInfo == null || structSize == 0) return;
            // Zero entire struct, then write cb at offset 0.
            uint dwords = structSize / 4;
            for (uint i = 0; i < dwords; i++) lpStartupInfo[i] = 0;
            lpStartupInfo[0] = structSize;  // cb field
        }
    }
}
