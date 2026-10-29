// The terminal's input, read as a program on Unix reads its terminal
// (step217): bytes — keys as a terminal sends them, and the terminal's own
// answers to queries — parsed here into ConsoleKeyInfo.
//
// Written after the sequences xterm sends (and BCL's TerminalFormatStrings /
// KeyMapper on Unix decodes): CSI A..D H F with "1;m" modifiers, CSI n ~ for
// the navigation cluster and F5–F12, SS3 P..S for F1–F4, ESC before a key for
// Alt, control bytes for Ctrl+letter, DEL for Backspace. A lone Esc is told
// from the start of a sequence by a pause (ncurses' ESCDELAY). Answers — a
// cursor position report, device attributes, an OSC or DCS reply — are
// swallowed rather than handed out as key presses; the cursor position one is
// kept for CursorLeft/CursorTop.

using System;

namespace SharpOS.AppSdk
{
    /// <summary>A parser of terminal input bytes into key presses.</summary>
    internal sealed class TerminalKeyParser
    {
        /// <summary>How long the byte after ESC may take to still belong to it.</summary>
        public const int EscapeDelayMs = 50;

        // The next byte, waiting at most timeoutMs (-1: for good); -1 when none came.
        private readonly Func<int, int> _next;

        public TerminalKeyParser(Func<int, int> next) => _next = next;

        /// <summary>Set while a cursor position query waits for its answer: CSI row;col R is that answer, not F3.</summary>
        public bool CursorQueryPending;

        /// <summary>The last cursor position the terminal reported, 0-based; -1 before any.</summary>
        public int ReportedLeft = -1, ReportedTop = -1;

        /// <summary>
        /// The next key press within <paramref name="timeoutMs"/> (-1: wait
        /// for one). False when none came — answers swallowed on the way
        /// still count as read.
        /// </summary>
        public bool TryRead(int timeoutMs, out ConsoleKeyInfo key)
        {
            key = default;
            while (true)
            {
                int b = _next(timeoutMs);
                if (b < 0) return false;
                if (b == 0x1B)
                {
                    int c = _next(EscapeDelayMs);
                    if (c < 0) { key = new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false); return true; }
                    if (c == '[') { if (Csi(out key)) return true; continue; }
                    if (c == 'O') { if (Ss3(out key)) return true; continue; }
                    if (c == ']') { SkipString(); continue; }
                    if (c == 0x1B) { key = new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, true, false); return true; }
                    // Alt and a key.
                    ConsoleKeyInfo plain = Plain(c);
                    key = new ConsoleKeyInfo(plain.KeyChar, plain.Key,
                        (plain.Modifiers & ConsoleModifiers.Shift) != 0, true, (plain.Modifiers & ConsoleModifiers.Control) != 0);
                    return true;
                }
                key = Plain(b);
                if (key.Key == ConsoleKey.None && key.KeyChar == '\0') continue;
                return true;
            }
        }

        // A byte that starts no sequence: a character, maybe UTF-8 lead.
        private ConsoleKeyInfo Plain(int b)
        {
            if (b == 0x0D || b == 0x0A) return new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);
            if (b == 0x7F || b == 0x08) return new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false);
            if (b == 0x09) return new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false);
            if (b == 0x00) return new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, true);
            if (b >= 0x01 && b <= 0x1A) return new ConsoleKeyInfo((char)b, (ConsoleKey)((int)ConsoleKey.A + b - 1), false, false, true);
            if (b < 0x20) return new ConsoleKeyInfo((char)b, ConsoleKey.None, false, false, true);

            char c = (char)b;
            if (b >= 0x80) c = Utf8(b);
            if (c == ' ') return new ConsoleKeyInfo(c, ConsoleKey.Spacebar, false, false, false);
            if (c >= 'a' && c <= 'z') return new ConsoleKeyInfo(c, (ConsoleKey)((int)ConsoleKey.A + c - 'a'), false, false, false);
            if (c >= 'A' && c <= 'Z') return new ConsoleKeyInfo(c, (ConsoleKey)((int)ConsoleKey.A + c - 'A'), true, false, false);
            if (c >= '0' && c <= '9') return new ConsoleKeyInfo(c, (ConsoleKey)((int)ConsoleKey.D0 + c - '0'), false, false, false);
            return new ConsoleKeyInfo(c, ConsoleKey.None, false, false, false);
        }

        // The rest of a UTF-8 character; U+FFFD for a broken one.
        private char Utf8(int lead)
        {
            int more, value;
            if ((lead & 0xE0) == 0xC0) { more = 1; value = lead & 0x1F; }
            else if ((lead & 0xF0) == 0xE0) { more = 2; value = lead & 0x0F; }
            else if ((lead & 0xF8) == 0xF0) { more = 3; value = lead & 0x07; }
            else return '�';
            for (int i = 0; i < more; i++)
            {
                int c = _next(EscapeDelayMs);
                if (c < 0 || (c & 0xC0) != 0x80) return '�';
                value = (value << 6) | (c & 0x3F);
            }
            return value <= 0xFFFF ? (char)value : '�';
        }

        // After ESC [: parameters, then the final byte. False: an answer, or a
        // sequence that is no key — read and dropped.
        private bool Csi(out ConsoleKeyInfo key)
        {
            key = default;
            int first = -1, second = -1, count = 0;
            int current = -1;
            bool privateMarker = false;
            int b;
            while (true)
            {
                b = _next(EscapeDelayMs);
                if (b < 0)
                {
                    // ESC [ and nothing: Alt+[.
                    if (count == 0 && current < 0 && !privateMarker) { key = new ConsoleKeyInfo('[', ConsoleKey.Oem4, false, true, false); return true; }
                    return false;
                }
                if (b >= '0' && b <= '9') { current = (current < 0 ? 0 : current) * 10 + (b - '0'); if (current > 9999) current = 9999; continue; }
                if (b == ';') { Push(ref first, ref second, ref count, current); current = -1; continue; }
                if (b == '?' || b == '>' || b == '<' || b == '=') { privateMarker = true; continue; }
                if (b >= 0x20 && b <= 0x2F) continue;          // intermediates
                if (b >= 0x40 && b <= 0x7E) break;              // final
                return false;                                   // not a sequence
            }
            Push(ref first, ref second, ref count, current);
            char final = (char)b;
            if (privateMarker) return false;                    // device attributes and the like

            if (final == 'R' && (CursorQueryPending || count == 2 && first > 1))
            {
                // The cursor position report: CSI row ; col R, 1-based.
                if (count == 2) { ReportedTop = first - 1; ReportedLeft = second - 1; }
                CursorQueryPending = false;
                return false;
            }

            int modifier = count >= 2 ? second : 1;
            bool shift = ((modifier - 1) & 1) != 0, alt = ((modifier - 1) & 2) != 0, ctrl = ((modifier - 1) & 4) != 0;
            ConsoleKey k;
            switch (final)
            {
                case 'A': k = ConsoleKey.UpArrow; break;
                case 'B': k = ConsoleKey.DownArrow; break;
                case 'C': k = ConsoleKey.RightArrow; break;
                case 'D': k = ConsoleKey.LeftArrow; break;
                case 'H': k = ConsoleKey.Home; break;
                case 'F': k = ConsoleKey.End; break;
                case 'P': k = ConsoleKey.F1; break;
                case 'Q': k = ConsoleKey.F2; break;
                case 'R': k = ConsoleKey.F3; break;
                case 'S': k = ConsoleKey.F4; break;
                case 'Z': key = new ConsoleKeyInfo('\t', ConsoleKey.Tab, true, false, false); return true;
                case '~': k = Tilde(count >= 1 ? first : -1); break;
                default: return false;                          // a report (t, n, c...) or unknown
            }
            if (k == ConsoleKey.None) return false;
            key = new ConsoleKeyInfo('\0', k, shift, alt, ctrl);
            return true;
        }

        private static void Push(ref int first, ref int second, ref int count, int value)
        {
            if (count == 0) first = value < 0 ? 1 : value;
            else if (count == 1) second = value < 0 ? 1 : value;
            count++;
        }

        private static ConsoleKey Tilde(int number) => number switch
        {
            1 or 7 => ConsoleKey.Home,
            2 => ConsoleKey.Insert,
            3 => ConsoleKey.Delete,
            4 or 8 => ConsoleKey.End,
            5 => ConsoleKey.PageUp,
            6 => ConsoleKey.PageDown,
            11 => ConsoleKey.F1,
            12 => ConsoleKey.F2,
            13 => ConsoleKey.F3,
            14 => ConsoleKey.F4,
            15 => ConsoleKey.F5,
            17 => ConsoleKey.F6,
            18 => ConsoleKey.F7,
            19 => ConsoleKey.F8,
            20 => ConsoleKey.F9,
            21 => ConsoleKey.F10,
            23 => ConsoleKey.F11,
            24 => ConsoleKey.F12,
            _ => ConsoleKey.None,
        };

        // After ESC O: one byte.
        private bool Ss3(out ConsoleKeyInfo key)
        {
            key = default;
            int b = _next(EscapeDelayMs);
            if (b < 0) { key = new ConsoleKeyInfo('O', ConsoleKey.O, true, true, false); return true; }
            ConsoleKey k = b switch
            {
                'P' => ConsoleKey.F1,
                'Q' => ConsoleKey.F2,
                'R' => ConsoleKey.F3,
                'S' => ConsoleKey.F4,
                'A' => ConsoleKey.UpArrow,
                'B' => ConsoleKey.DownArrow,
                'C' => ConsoleKey.RightArrow,
                'D' => ConsoleKey.LeftArrow,
                'H' => ConsoleKey.Home,
                'F' => ConsoleKey.End,
                _ => ConsoleKey.None,
            };
            if (k == ConsoleKey.None) return false;
            key = new ConsoleKeyInfo('\0', k, false, false, false);
            return true;
        }

        // An OSC answer: up to BEL or ESC \.
        private void SkipString()
        {
            for (int i = 0; i < 4096; i++)
            {
                int b = _next(EscapeDelayMs);
                if (b < 0 || b == 0x07) return;
                if (b == 0x1B) { _next(EscapeDelayMs); return; }
            }
        }
    }

    /// <summary>The terminal's input of this program (TerminalRead), parsed.</summary>
    internal static unsafe class TerminalKeys
    {
        private const int PollMs = 10;

        private static byte[] s_bytes;
        private static int s_head, s_tail;
        private static TerminalKeyParser s_parser;

        // Keys read while waiting for an answer, handed out first.
        private static ConsoleKeyInfo[] s_keys;
        private static int s_keyHead, s_keyCount;

        /// <summary>Whether the kernel gives the terminal's input as bytes.</summary>
        public static bool Available
            => AppRuntime.Services != null && AppRuntime.Services->TerminalReadAddress != 0;

        private static TerminalKeyParser Parser
        {
            get
            {
                if (s_parser == null)
                {
                    s_bytes = new byte[256];
                    s_keys = new ConsoleKeyInfo[32];
                    s_parser = new TerminalKeyParser(Next);
                }
                return s_parser;
            }
        }

        /// <summary>The next key press within <paramref name="timeoutMs"/> (-1: wait).</summary>
        public static bool TryRead(int timeoutMs, out ConsoleKeyInfo key)
        {
            TerminalKeyParser parser = Parser;
            if (s_keyCount > 0)
            {
                key = s_keys[s_keyHead];
                s_keyHead = (s_keyHead + 1) % s_keys.Length;
                s_keyCount--;
                return true;
            }
            return parser.TryRead(timeoutMs, out key);
        }

        /// <summary>Whether a key press is waiting (Console.KeyAvailable).</summary>
        public static bool KeyAvailable
        {
            get
            {
                TerminalKeyParser parser = Parser;
                if (s_keyCount > 0) return true;
                if (s_head == s_tail && !Fill()) return false;
                if (!parser.TryRead(0, out ConsoleKeyInfo key)) return false;
                Keep(key);
                return true;
            }
        }

        /// <summary>
        /// Asks the terminal where the cursor is (ESC [6n) and waits for the
        /// answer, keeping keys that arrive meanwhile. False when none came:
        /// no terminal answering, as BCL on Unix with output redirected.
        /// </summary>
        public static bool TryGetCursorPosition(out int left, out int top)
        {
            TerminalKeyParser parser = Parser;
            left = top = 0;
            parser.CursorQueryPending = true;
            parser.ReportedLeft = parser.ReportedTop = -1;
            AppHost.Terminal.Write("\u001b[6n");
            for (int waited = 0; waited < 500 && parser.CursorQueryPending; waited += PollMs)
            {
                while (parser.TryRead(0, out ConsoleKeyInfo key)) Keep(key);
                if (parser.CursorQueryPending) AppThreads.Sleep(PollMs);
            }
            parser.CursorQueryPending = false;
            if (parser.ReportedLeft < 0) return false;
            left = parser.ReportedLeft;
            top = parser.ReportedTop;
            return true;
        }

        private static void Keep(ConsoleKeyInfo key)
        {
            if (s_keyCount == s_keys.Length) return;
            s_keys[(s_keyHead + s_keyCount) % s_keys.Length] = key;
            s_keyCount++;
        }

        private static int Next(int timeoutMs)
        {
            for (int waited = 0; ; waited += PollMs)
            {
                if (s_head != s_tail)
                {
                    byte b = s_bytes[s_head];
                    s_head = (s_head + 1) % s_bytes.Length;
                    return b;
                }
                if (Fill()) continue;
                if (timeoutMs >= 0 && waited >= timeoutMs) return -1;
                AppThreads.Sleep(timeoutMs >= 0 && timeoutMs < PollMs ? (uint)Math.Max(1, timeoutMs) : PollMs);
            }
        }

        // Bytes from the kernel into the ring (empty when called); false when none.
        private static bool Fill()
        {
            var read = (delegate* unmanaged<byte*, int, int>)AppRuntime.Services->TerminalReadAddress;
            s_head = s_tail = 0;
            int n;
            fixed (byte* buffer = s_bytes) n = read(buffer, s_bytes.Length - 1);
            if (n <= 0) return false;
            s_tail = n;
            return true;
        }
    }
}
