using System.Runtime;
using System.Runtime.InteropServices;
using OS.Hal;

namespace OS.PAL.SharpOSHost
{
    // Stage A — libSystem.Native substrate for Unix-flavored BCL.
    //
    // The fork's framework assemblies (shipped from coreclr-pack/net10.0) are
    // Unix-flavored: System.Console P/Invokes `libSystem.Native` (the 25
    // SystemNative_* entrypoints below), NOT kernel32. On real .NET these live
    // in libSystem.Native.so; on bare metal we provide them here.
    //
    // Resolution path: managed `[LibraryImport("libSystem.Native")]` →
    // runtime PInvoke resolver → LoadLibrary("libSystem.Native") → our
    // crt_imp_stubs.cpp returns SHARPOS_SYSNATIVE_HMODULE sentinel →
    // GetProcAddress("SystemNative_X") → &SystemNative_X (these [RuntimeExport]
    // symbols, resolved at kernel link). Same proven pattern as Advapi32/ETW.
    //
    // Only SystemNative_Write must be faithful (Console output → COM1). The
    // rest return safe defaults that steer ConsolePal.Unix down the
    // non-interactive / not-a-tty path (no termios, no signal handling).
    internal static unsafe class SystemNativeStubs
    {
        // ssize_t SystemNative_Write(intptr_t fd, const void* buffer, int32_t count)
        // fd 1=stdout 2=stderr → COM1 serial. Returns bytes written.
        [RuntimeExport("SystemNative_Write")]
        public static int Write(nint fd, byte* buffer, int count)
        {
            if (buffer == null || count <= 0) return 0;
            if (fd == 1 || fd == 2)
            {
                for (int i = 0; i < count; i++)
                    Console.WriteChar((char)buffer[i]);
            }
            return count;
        }

        // int SystemNative_Read(intptr_t fd, void* buffer, int32_t count) — no stdin → EOF (0).
        [RuntimeExport("SystemNative_Read")]
        public static int Read(nint fd, byte* buffer, int count) { _ = fd; _ = buffer; _ = count; return 0; }

        // int SystemNative_IsATty(intptr_t fd) — 0 = not a terminal → Console
        // takes the plain redirected-stream path (no termios/keypad/signals).
        [RuntimeExport("SystemNative_IsATty")]
        public static int IsATty(nint fd) { _ = fd; return 0; }

        // int SystemNative_InitializeTerminalAndSignalHandling() — 1 = success.
        [RuntimeExport("SystemNative_InitializeTerminalAndSignalHandling")]
        public static int InitializeTerminalAndSignalHandling() => 1;

        [RuntimeExport("SystemNative_InitializeConsoleBeforeRead")]
        public static void InitializeConsoleBeforeRead(byte minChars, byte decisecondsTimeout)
        { _ = minChars; _ = decisecondsTimeout; }

        [RuntimeExport("SystemNative_UninitializeConsoleAfterRead")]
        public static void UninitializeConsoleAfterRead() { }

        [RuntimeExport("SystemNative_UninitializeTerminal")]
        public static void UninitializeTerminal() { }

        // int SystemNative_GetWindowSize(WinSize*) — -1 = failure → Console
        // falls back to default 80x24 dimensions.
        [RuntimeExport("SystemNative_GetWindowSize")]
        public static int GetWindowSize(void* w) { _ = w; return -1; }

        // Error-code conversion — identity (we don't map errno spaces).
        [RuntimeExport("SystemNative_ConvertErrorPlatformToPal")]
        public static int ConvertErrorPlatformToPal(int e) => e;

        [RuntimeExport("SystemNative_ConvertErrorPalToPlatform")]
        public static int ConvertErrorPalToPlatform(int e) => e;

        // char* SystemNative_StrErrorR(int err, char* buf, int bufLen) — empty string.
        [RuntimeExport("SystemNative_StrErrorR")]
        public static byte* StrErrorR(int err, byte* buf, int bufLen)
        {
            _ = err;
            if (buf != null && bufLen > 0) buf[0] = 0;
            return buf;
        }

        [RuntimeExport("SystemNative_GetControlCharacters")]
        public static void GetControlCharacters(int* termios, byte* controlChars, int count, int posixDisable)
        { _ = termios; _ = controlChars; _ = count; _ = posixDisable; }

        [RuntimeExport("SystemNative_SetSignalForBreak")]
        public static int SetSignalForBreak(int signalForBreak) { _ = signalForBreak; return 1; }

        [RuntimeExport("SystemNative_GetSignalForBreak")]
        public static int GetSignalForBreak() => 1;

        [RuntimeExport("SystemNative_SetTerminalInvalidationHandler")]
        public static void SetTerminalInvalidationHandler(void* handler) { _ = handler; }

        [RuntimeExport("SystemNative_SetKeypadXmit")]
        public static void SetKeypadXmit(nint fd, byte* terminfoString) { _ = fd; _ = terminfoString; }

        // int SystemNative_StdinReady() — no input available.
        [RuntimeExport("SystemNative_StdinReady")]
        public static int StdinReady() => 0;

        // int SystemNative_ReadStdin(void* buffer, int bufferSize) — EOF.
        [RuntimeExport("SystemNative_ReadStdin")]
        public static int ReadStdin(byte* buffer, int bufferSize) { _ = buffer; _ = bufferSize; return 0; }

        // intptr_t SystemNative_Dup(intptr_t oldfd) — return same fd (no real dup).
        [RuntimeExport("SystemNative_Dup")]
        public static nint Dup(nint oldfd) => oldfd;

        // int SystemNative_Open(const char* path, int flags, int mode) — ENOENT.
        [RuntimeExport("SystemNative_Open")]
        public static int Open(byte* path, int flags, int mode) { _ = path; _ = flags; _ = mode; return -1; }

        // int SystemNative_Poll(...) — return 0 events.
        [RuntimeExport("SystemNative_Poll")]
        public static int Poll(void* pollEvents, uint eventCount, int timeout, uint* triggered)
        { _ = pollEvents; _ = eventCount; _ = timeout; if (triggered != null) *triggered = 0; return 0; }

        // uint SystemNative_GetEUid() — root (0).
        [RuntimeExport("SystemNative_GetEUid")]
        public static uint GetEUid() => 0;

        // int SystemNative_GetPwUidR(uint uid, Passwd* pwd, byte* buf, int bufLen) — fail.
        [RuntimeExport("SystemNative_GetPwUidR")]
        public static int GetPwUidR(uint uid, void* pwd, byte* buf, int bufLen)
        { _ = uid; _ = pwd; _ = buf; _ = bufLen; return -1; }

        // int SystemNative_SNPrintF_1I(char* str, int size, const char* fmt, int v)
        // Minimal: emit nothing, return 0. Console uses this only for terminfo
        // numeric param substitution (not on the plain non-tty write path).
        [RuntimeExport("SystemNative_SNPrintF_1I")]
        public static int SNPrintF_1I(byte* str, int size, byte* fmt, int v)
        { _ = fmt; _ = v; if (str != null && size > 0) str[0] = 0; return 0; }

        [RuntimeExport("SystemNative_SNPrintF_1S")]
        public static int SNPrintF_1S(byte* str, int size, byte* fmt, byte* v)
        { _ = fmt; _ = v; if (str != null && size > 0) str[0] = 0; return 0; }
    }
}
