using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel.Diagnostics;

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

        // PSReadLine renders by writing and by asking where the cursor is. Any
        // one of those calls quietly returning failure leaves it with nothing on
        // screen and no exception to show, which is indistinguishable from a
        // dead keyboard. Report the first few, with the handle that was refused.
        private static int s_apiTraceLeft = 24;

        private static void ApiTrace(string what, ulong handle, uint extra)
        {
            if (!Probes.ConsoleInputTrace || s_apiTraceLeft <= 0) return;
            s_apiTraceLeft--;
            OS.Hal.Console.Write("[capi] ");
            OS.Hal.Console.Write(what);
            OS.Hal.Console.Write(" h=");
            OS.Hal.Console.WriteHexRaw(handle, 2);
            OS.Hal.Console.Write(" n=");
            OS.Hal.Console.WriteHexRaw(extra, 1);
            OS.Hal.Console.Write("\n");
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
            if (!IsStdHandle(hConsole)) { ApiTrace("write REJECT", hConsole, nChars); return 0; }
            ApiTrace("write", hConsole, nChars);

            // The whole buffer as one unit: this is where PowerShell's escape
            // sequences arrive, and half of one is worse than none.
            OS.Kernel.Threading.Preemption.Suppress();
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
            OS.Kernel.Threading.Preemption.Allow();

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
            if (!IsStdHandle(hConsole)) { ApiTrace("sbinfo REJECT", hConsole, 0); return 0; }
            // Real geometry and cursor when the terminal engine owns the screen; the
            // synthetic 80x25 stays as the headless/no-framebuffer fallback. PSReadLine
            // and anything else that positions text needs these to be true, not polite.
            short cols = DefaultCols;
            short rows = DefaultRows;
            short curX = 0;
            short curY = 0;
            if (OS.Hal.TerminalConsole.IsReady)
            {
                var engine = OS.Hal.TerminalConsole.Engine;
                cols = (short)engine.Cols;
                rows = (short)engine.Rows;
                var termBuffer = engine.Buffer;
                curX = (short)termBuffer.X;
                curY = (short)(termBuffer.YBase + termBuffer.Y - termBuffer.YDisp);
            }

            short* p = (short*)outInfo;
            p[0] = cols;         // dwSize.X (buffer width)
            p[1] = rows;         // dwSize.Y (buffer height)
            p[2] = curX;         // cursor X
            p[3] = curY;         // cursor Y
            ((ushort*)outInfo)[4] = 0x0007; // wAttributes (light gray on black)
            p[5] = 0;            // srWindow.Left
            p[6] = 0;            // srWindow.Top
            p[7] = (short)(cols - 1);  // srWindow.Right
            p[8] = (short)(rows - 1);  // srWindow.Bottom
            p[9]  = cols;        // dwMaxWindowSize.X
            p[10] = rows;        // dwMaxWindowSize.Y
            return 1;
        }

        // GetCurrentConsoleFontEx — PSReadLine asks for the console font to decide
        // how wide a cell is before it draws. CONSOLE_FONT_INFO_EX on x64:
        //   +0  ULONG cbSize        +4  DWORD nFont
        //   +8  COORD dwFontSize    +12 UINT  FontFamily
        //   +16 UINT  FontWeight    +20 WCHAR FaceName[32]   (84 bytes total)
        // cbSize belongs to the caller and bounds what may be written — a blind
        // fixed-size fill here is what corrupted a P/Invoke stub's saved registers
        // once already (see SharpOSHost_SystemParametersInfo).
        [RuntimeExport("SharpOSHost_GetCurrentConsoleFontEx")]
        public static int GetCurrentConsoleFontEx(ulong hConsole, int bMaximumWindow, void* outFontEx)
        {
            _ = bMaximumWindow;
            if (outFontEx == null) return 0;
            if (!IsStdHandle(hConsole)) { ApiTrace("fontex REJECT", hConsole, 0); return 0; }

            uint cbSize = *(uint*)outFontEx;
            if (cbSize < 20) return 0;

            byte* p = (byte*)outFontEx;
            *(uint*)(p + 4) = 0;                       // nFont — index 0, the only one
            *(short*)(p + 8) = FontCellWidth;
            *(short*)(p + 10) = FontCellHeight;
            *(uint*)(p + 12) = 48;                     // FF_MODERN, no TMPF_TRUETYPE:
            *(uint*)(p + 16) = 400;                    // this really is a bitmap font
            if (cbSize < 20 + 2 * 9) return 1;         // no room for the name + NUL

            // "Terminal" — the name Windows itself reports for its raster font.
            char* face = (char*)(p + 20);
            face[0] = 'T'; face[1] = 'e'; face[2] = 'r'; face[3] = 'm';
            face[4] = 'i'; face[5] = 'n'; face[6] = 'a'; face[7] = 'l';
            face[8] = '\0';
            return 1;
        }

        // GetConsoleCursorInfo / SetConsoleCursorInfo — PSReadLine reads the
        // cursor shape before it starts editing and hides the cursor while it
        // repaints. CONSOLE_CURSOR_INFO is { DWORD dwSize; BOOL bVisible; },
        // dwSize being the filled percentage of the cell (1..100).
        //
        // The block cursor is drawn by TerminalConsole, which has no shape or
        // visibility control yet, so report a plain visible cursor and accept
        // whatever is set. Answering at all is what matters: the missing export
        // threw EntryPointNotFound on the first keystroke and took PSReadLine's
        // whole render path down with it.
        [RuntimeExport("SharpOSHost_GetConsoleCursorInfo")]
        public static int GetConsoleCursorInfo(ulong hConsole, void* outInfo)
        {
            if (outInfo == null) return 0;
            if (!IsStdHandle(hConsole)) { ApiTrace("curinfo REJECT", hConsole, 0); return 0; }
            *(uint*)outInfo = 25;                    // dwSize — a normal underline
            *(int*)((byte*)outInfo + 4) = 1;         // bVisible
            return 1;
        }

        [RuntimeExport("SharpOSHost_SetConsoleCursorInfo")]
        public static int SetConsoleCursorInfo(ulong hConsole, void* info)
        {
            _ = info;
            if (!IsStdHandle(hConsole)) { ApiTrace("setcurinfo REJECT", hConsole, 0); return 0; }
            return 1;
        }

        // Glyph box of the framebuffer console. Kept in sync with
        // TerminalConsole's CellW/CellH, which come from Font8x8 at scale 1.
        private const short FontCellWidth = 8;
        private const short FontCellHeight = 8;

        // SetConsoleCursorPosition — no-op (UART has no positionable cursor).
        // Returns success so BCL doesn't propagate failure.
        [RuntimeExport("SharpOSHost_SetConsoleCursorPosition")]
        public static int SetConsoleCursorPosition(ulong hConsole, int packedCoord)
        {
            if (!IsStdHandle(hConsole)) { ApiTrace("setcur REJECT", hConsole, 0); return 0; }
            ApiTrace("setcur", hConsole, (uint)packedCoord);

            // COORD packs Y in the high half, X in the low half; both are signed.
            short x = (short)(packedCoord & 0xFFFF);
            short y = (short)((packedCoord >> 16) & 0xFFFF);
            if (x < 0) x = 0;
            if (y < 0) y = 0;

            // The engine is the one that knows where the cursor is, so move it the way
            // any other program would: CUP is 1-based. Writing through Platform keeps
            // the UART log in sync too.
            if (OS.Hal.TerminalConsole.IsReady)
            {
                WriteCsi();
                WriteNumber(y + 1);
                OS.Hal.Platform.WriteChar(';');
                WriteNumber(x + 1);
                OS.Hal.Platform.WriteChar('H');
            }
            return 1;
        }

        private static void WriteCsi()
        {
            OS.Hal.Platform.WriteChar((char)0x1B);
            OS.Hal.Platform.WriteChar('[');
        }

        // Digits without allocating: this runs on the console write path.
        private static void WriteNumber(int value)
        {
            if (value <= 0) { OS.Hal.Platform.WriteChar('0'); return; }
            int divisor = 1;
            while (value / divisor >= 10) divisor *= 10;
            while (divisor > 0)
            {
                OS.Hal.Platform.WriteChar((char)('0' + (value / divisor) % 10));
                divisor /= 10;
            }
        }

        // SetConsoleTextAttribute — no-op success.
        [RuntimeExport("SharpOSHost_SetConsoleTextAttribute")]
        public static int SetConsoleTextAttribute(ulong hConsole, ushort attrs)
        {
            return IsStdHandle(hConsole) ? 1 : 0;
        }

        // SetConsoleCtrlHandler — Ctrl+C/Ctrl+Break handler registration.
        // PowerShell registers one to turn Ctrl+C into a pipeline stop.
        //
        // The registration used to be accepted and the handler pointer thrown
        // away, so the shell could never be interrupted: the key reached the
        // line editor and echoed as ^C, which made it look wired up, while
        // `sleep 1000` ran to completion regardless. Accepting a registration
        // one cannot honour is worse than refusing it — it looks like support.
        private const int MaxCtrlHandlers = 8;
        private static ulong[] s_ctrlHandlers = null!;
        private static int s_ctrlHandlerCount;

        [RuntimeExport("SharpOSHost_SetConsoleCtrlHandler")]
        public static int SetConsoleCtrlHandler(void* handler, int add)
        {
            if (s_ctrlHandlers == null) s_ctrlHandlers = new ulong[MaxCtrlHandlers];

            // A null routine is Windows' "ignore Ctrl+C" switch, not a handler.
            if (handler == null) return 1;

            if (add != 0)
            {
                if (s_ctrlHandlerCount >= MaxCtrlHandlers) return 0;
                s_ctrlHandlers[s_ctrlHandlerCount++] = (ulong)handler;
                return 1;
            }

            for (int i = 0; i < s_ctrlHandlerCount; i++)
            {
                if (s_ctrlHandlers[i] != (ulong)handler) continue;
                for (int j = i; j + 1 < s_ctrlHandlerCount; j++) s_ctrlHandlers[j] = s_ctrlHandlers[j + 1];
                s_ctrlHandlerCount--;
                return 1;
            }
            return 0;
        }

        /// <summary>How many handlers are registered. Zero means the shell is
        /// not up yet: nothing to deliver to, and nobody to attach for.</summary>
        public static int CtrlHandlerCount => s_ctrlHandlerCount;

        /// <summary>
        /// Deliver a console control event. Handlers run most-recently-added
        /// first and the first one to return TRUE consumes it — Win32 order,
        /// which is what PowerShell's handler expects.
        /// Returns true if someone handled it.
        /// </summary>
        public static bool RaiseCtrlEvent(uint eventType)
        {
            for (int i = s_ctrlHandlerCount - 1; i >= 0; i--)
            {
                var fn = (delegate* unmanaged<uint, int>)s_ctrlHandlers[i];
                if (fn == null) continue;
                if (fn(eventType) != 0) return true;
            }
            return false;
        }

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
