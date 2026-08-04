using System.Runtime;
using OS.Hal;
using OS.Kernel.Diagnostics;

namespace OS.PAL.SharpOSHost
{
    // Win32 console *event* input: ReadConsoleInput / PeekConsoleInput /
    // GetNumberOfConsoleInputEvents.
    //
    // ConsoleRead.cs is the line-mode path: it owns the editing, draws the echo
    // and hands back a finished line. PSReadLine cannot use that — it does its
    // own editing, so it wants raw key events with virtual key codes and
    // modifier state, which is what these three exports deliver.
    //
    // Only key-down records are produced. Windows emits both down and up, but
    // every consumer we care about (Console.ReadKey, PSReadLine) filters for
    // key-down, and synthesising releases from a driver that already collapses
    // them would add noise, not fidelity.
    internal static unsafe class ConsoleInput
    {
        private const ushort KEY_EVENT = 0x0001;

        // dwControlKeyState bits (wincon.h).
        private const uint RIGHT_ALT_PRESSED = 0x0001;
        private const uint LEFT_ALT_PRESSED = 0x0002;
        private const uint RIGHT_CTRL_PRESSED = 0x0004;
        private const uint LEFT_CTRL_PRESSED = 0x0008;
        private const uint SHIFT_PRESSED = 0x0010;
        private const uint CAPSLOCK_ON = 0x0080;

        // Virtual key codes (winuser.h) for the non-character keys we decode.
        private const ushort VK_BACK = 0x08;
        private const ushort VK_TAB = 0x09;
        private const ushort VK_RETURN = 0x0D;
        private const ushort VK_ESCAPE = 0x1B;
        private const ushort VK_SPACE = 0x20;
        private const ushort VK_PRIOR = 0x21;   // PageUp
        private const ushort VK_NEXT = 0x22;    // PageDown
        private const ushort VK_END = 0x23;
        private const ushort VK_HOME = 0x24;
        private const ushort VK_LEFT = 0x25;
        private const ushort VK_UP = 0x26;
        private const ushort VK_RIGHT = 0x27;
        private const ushort VK_DOWN = 0x28;
        private const ushort VK_INSERT = 0x2D;
        private const ushort VK_DELETE = 0x2E;

        // One decoded event held back by Peek, so a peek followed by a read
        // returns the same key instead of swallowing it.
        private static bool s_hasPending;
        private static ushort s_pendingVk;
        private static char s_pendingChar;
        private static uint s_pendingState;

        // BOOL ReadConsoleInputW(HANDLE, PINPUT_RECORD, DWORD nLength, LPDWORD nRead)
        [RuntimeExport("SharpOSHost_ReadConsoleInput")]
        public static int ReadConsoleInput(void* buffer, uint length, uint* eventsRead)
        {
            if (eventsRead != null) *eventsRead = 0;
            if (buffer == null || length == 0) return 0;

            // Blocking, like the real one: return only when at least one event is in.
            while (!s_hasPending)
            {
                if (!TryDecodeOne())
                {
                    // Nothing typed: paint what is pending and let other threads run.
                    // Same reason as the line-mode loop — the front-end batches drawing
                    // and this is the point where the console is idle. Only when there
                    // is something to paint: polling the lock on every iteration of this
                    // loop starved the thread doing the writing, so its output was lost.
                    if (TerminalConsole.HasPendingOutput)
                        TerminalConsole.Flush();
                    OS.Kernel.Threading.Scheduler.Yield();
                }
            }

            WriteRecord(buffer, s_pendingVk, s_pendingChar, s_pendingState);
            s_hasPending = false;
            if (eventsRead != null) *eventsRead = 1;
            return 1;
        }

        // BOOL PeekConsoleInputW(...) — non-blocking; leaves the event queued.
        [RuntimeExport("SharpOSHost_PeekConsoleInput")]
        public static int PeekConsoleInput(void* buffer, uint length, uint* eventsRead)
        {
            if (eventsRead != null) *eventsRead = 0;
            if (buffer == null || length == 0) return 1;

            if (!s_hasPending && !TryDecodeOne())
                return 1;   // success, zero events

            WriteRecord(buffer, s_pendingVk, s_pendingChar, s_pendingState);
            if (eventsRead != null) *eventsRead = 1;
            return 1;
        }

        // BOOL GetNumberOfConsoleInputEvents(HANDLE, LPDWORD) — backs
        // Console.KeyAvailable, which PSReadLine polls while it renders.
        [RuntimeExport("SharpOSHost_GetNumberOfConsoleInputEvents")]
        public static int GetNumberOfConsoleInputEvents(uint* count)
        {
            if (count == null) return 0;
            if (!s_hasPending)
                TryDecodeOne();
            *count = s_hasPending ? 1u : 0u;
            return 1;
        }

        // Bounded so a stuck reader cannot flood the log. Entry to the blocking
        // read is reported once: "read but no keys" and "never asked to read"
        // look identical from outside, and they point at different subsystems.
        private static int s_traceLeft = 200;

        // Everything interesting happens once the user starts typing, and the
        // startup traffic is large enough to exhaust any budget before then.
        // Other probes gate on this so their records land in the typing window.
        internal static bool SawKey;

        private static void Trace(string tag, ushort vk, char ch)
        {
            if (!Probes.ConsoleInputTrace || s_traceLeft <= 0) return;
            s_traceLeft--;
            Console.Write(tag);
            Console.WriteHexRaw(vk, 2);
            Console.Write(" ch=");
            Console.WriteHexRaw((ulong)ch, 2);
            Console.Write("\n");
        }

        // Pulls scancodes until one produces a key event, or the buffer runs dry.
        private static bool TryDecodeOne()
        {
            while (Ps2Keyboard.TryReadScancode(out byte scancode))
            {
                // `make` is the scancode with the break bit stripped, not a
                // pressed/released flag, so testing it for zero filters nothing.
                // Releases are already classified as KeyKind.None by DecodeEx and
                // fall out through the default case below.
                var kind = Ps2Keyboard.Decode(scancode, out char ch, out _);
                // Raw scancode before any classification: a key that never shows
                // up here is not reaching us at all, which is a different fault
                // from one that arrives and gets classified away.
                Trace("[cin] sc=", scancode, (char)kind);

                ushort vk;
                switch (kind)
                {
                    case Ps2Keyboard.KeyKind.Enter: vk = VK_RETURN; ch = '\r'; break;
                    case Ps2Keyboard.KeyKind.Backspace: vk = VK_BACK; ch = '\b'; break;
                    case Ps2Keyboard.KeyKind.Escape: vk = VK_ESCAPE; ch = (char)0x1B; break;
                    case Ps2Keyboard.KeyKind.Up: vk = VK_UP; ch = '\0'; break;
                    case Ps2Keyboard.KeyKind.Down: vk = VK_DOWN; ch = '\0'; break;
                    case Ps2Keyboard.KeyKind.Left: vk = VK_LEFT; ch = '\0'; break;
                    case Ps2Keyboard.KeyKind.Right: vk = VK_RIGHT; ch = '\0'; break;
                    // Navigation cluster: no character, only a virtual key —
                    // which is what PSReadLine binds Delete/Home/End to.
                    case Ps2Keyboard.KeyKind.Delete: vk = VK_DELETE; ch = '\0'; break;
                    case Ps2Keyboard.KeyKind.Insert: vk = VK_INSERT; ch = '\0'; break;
                    case Ps2Keyboard.KeyKind.Home: vk = VK_HOME; ch = '\0'; break;
                    case Ps2Keyboard.KeyKind.End: vk = VK_END; ch = '\0'; break;
                    case Ps2Keyboard.KeyKind.PageUp: vk = VK_PRIOR; ch = '\0'; break;
                    case Ps2Keyboard.KeyKind.PageDown: vk = VK_NEXT; ch = '\0'; break;
                    case Ps2Keyboard.KeyKind.Char: vk = VirtualKeyForChar(ch); break;
                    default: continue;      // modifiers and releases carry no event
                }

                s_pendingVk = vk;
                s_pendingChar = ch;
                s_pendingState = ControlKeyState();
                s_hasPending = true;
                SawKey = true;
                Trace("[cin] key vk=", vk, ch);
                return true;
            }
            return false;
        }

        // Windows reports the key that was pressed, not the character it produced:
        // letters map to their uppercase code, digits to themselves. Punctuation
        // is layout-dependent — consumers read UnicodeChar for those, so 0 is the
        // honest answer rather than a guess at a US-layout code.
        private static ushort VirtualKeyForChar(char ch)
        {
            if (ch >= 'a' && ch <= 'z') return (ushort)(ch - 'a' + 'A');
            if (ch >= 'A' && ch <= 'Z') return ch;
            if (ch >= '0' && ch <= '9') return ch;
            if (ch == ' ') return VK_SPACE;
            if (ch == '\t') return VK_TAB;
            return 0;
        }

        private static uint ControlKeyState()
        {
            uint state = 0;
            if (Ps2Keyboard.ShiftHeld) state |= SHIFT_PRESSED;
            if (Ps2Keyboard.CtrlHeld) state |= LEFT_CTRL_PRESSED;
            if (Ps2Keyboard.AltHeld) state |= LEFT_ALT_PRESSED;
            if (Ps2Keyboard.CapsLockOn) state |= CAPSLOCK_ON;
            return state;
        }

        // INPUT_RECORD with a KEY_EVENT payload, 20 bytes:
        //   +0  WORD  EventType
        //   +4  BOOL  bKeyDown
        //   +8  WORD  wRepeatCount
        //   +10 WORD  wVirtualKeyCode
        //   +12 WORD  wVirtualScanCode
        //   +14 WCHAR UnicodeChar
        //   +16 DWORD dwControlKeyState
        private static void WriteRecord(void* buffer, ushort vk, char ch, uint controlState)
        {
            byte* p = (byte*)buffer;
            *(ushort*)(p + 0) = KEY_EVENT;
            *(ushort*)(p + 2) = 0;
            *(int*)(p + 4) = 1;                 // bKeyDown
            *(ushort*)(p + 8) = 1;              // wRepeatCount
            *(ushort*)(p + 10) = vk;
            *(ushort*)(p + 12) = 0;             // wVirtualScanCode — consumers use vk/char
            *(ushort*)(p + 14) = ch;
            *(uint*)(p + 16) = controlState;
        }
    }
}
