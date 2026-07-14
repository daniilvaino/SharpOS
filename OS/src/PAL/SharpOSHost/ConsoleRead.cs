using System.Runtime;
using System.Runtime.InteropServices;
using OS.Hal;

namespace OS.PAL.SharpOSHost
{
    // step126.14 — kernel-side ReadConsole. PowerShell ConsoleControl.
    // NativeMethods.ReadConsole hits kernel32 to read user input from the
    // console. On a real Windows console this is line-mode by default:
    // blocks until user presses Enter, returns the typed line as wide
    // chars including the trailing \r\n.
    //
    // SharpOS has a real PS/2 keyboard driver in OS.Hal.Ps2Keyboard +
    // LineEditor — boot shell already uses them for the launcher menu.
    // Wire those into ReadConsole:
    //   1. Loop polling Ps2Keyboard.TryReadScancode
    //   2. Decode each scancode to KeyKind + char
    //   3. Feed LineEditor (handles printable insert / Backspace / Enter)
    //   4. Echo each accepted char (and \n on Enter) to UART so user sees
    //      what they typed
    //   5. When LineEditor reports Submitted, copy buffer + \r\n to caller's
    //      wchar_t* output
    //
    // PowerShell sees this as a normal Windows console line read — it
    // gets "<typed line>\r\n" exactly like on Windows.
    internal static unsafe class ConsoleRead
    {
        [RuntimeExport("SharpOSHost_ReadConsole")]
        public static int ReadConsole(char* lpBuffer, uint nCharsToRead, uint* lpNumberOfCharsRead)
        {
            if (lpNumberOfCharsRead != null) *lpNumberOfCharsRead = 0;
            if (lpBuffer == null || nCharsToRead == 0) return 1;

            LineEditor.Reset();

            // Blocking line-mode loop. Polls keyboard hardware until Enter
            // Cooperative-friendly poll: when no scancode is ready, yield
            // so background threads (PS telemetry sender, GC finalizer,
            // ThreadPool workers, deferred JIT) get CPU. Without the yield
            // the entire managed runtime stalls behind keyboard input,
            // causing both interactive hangs (prompt never renders after a
            // cmdlet that scheduled async work) and overall sluggishness.
            while (true)
            {
                if (!Ps2Keyboard.TryReadScancode(out byte sc))
                {
                    OS.Kernel.Threading.Scheduler.Yield();
                    continue;
                }

                var kind = Ps2Keyboard.Decode(sc, out char ch, out byte make);
                if (make == 0) continue;  // ignore break events

                var status = LineEditor.Feed(kind, ch);
                if (status == LineEditor.Status.Changed)
                {
                    if (kind == Ps2Keyboard.KeyKind.Backspace)
                    {
                        // Erase last on-screen char: BS-space-BS. FbTty parses
                        // 0x08 directly via FbTty.Backspace, UART terminals
                        // interpret it via the standard backspace handling.
                        // Single sequence — emitting both BS and ANSI \e[D
                        // double-erases on terminals that interpret both.
                        OS.Hal.Platform.WriteChar((char)0x08);
                        OS.Hal.Platform.WriteChar(' ');
                        OS.Hal.Platform.WriteChar((char)0x08);
                    }
                    else
                    {
                        OS.Hal.Platform.WriteChar(ch);
                    }
                }
                if (status == LineEditor.Status.Submitted)
                {
                    OS.Hal.Platform.WriteChar('\r');
                    OS.Hal.Platform.WriteChar('\n');
                    break;
                }
            }

            // Copy the buffered line + \r\n into caller's wide buffer.
            uint written = 0;
            int len = LineEditor.Length;
            var src = LineEditor.Buffer;
            for (int i = 0; i < len && written < nCharsToRead; i++)
                lpBuffer[written++] = src[i];
            if (written < nCharsToRead) lpBuffer[written++] = '\r';
            if (written < nCharsToRead) lpBuffer[written++] = '\n';

            if (lpNumberOfCharsRead != null) *lpNumberOfCharsRead = written;
            return 1;
        }
    }
}
