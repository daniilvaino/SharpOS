// One key, two ways to deliver it (step243). The kernel decodes a keystroke
// once — its character, its UEFI scan code, the modifiers held at that moment
// — and both deliveries are built from that: the raw key service hands the
// event over as is (with the physical key and press/release, for games and
// Terminal.Gui), the terminal encodes it as the bytes xterm sends. A reader of
// those bytes parses them back through the same FromChar, so a key reads the
// same on both paths wherever a terminal can say it at all.
//
// ConsoleKey values for characters follow Windows on a US keyboard (VK_OEM_*
// for punctuation, the digit key for its shifted glyph): the stock
// Terminal.Gui drivers rely on them to tell a character from a key.

using System;

namespace SharpOS.Std
{
    public static class ConsoleKeys
    {
        /// <summary>The longest a key's terminal bytes get (ESC [ 2 4 ; 8 ~ and the like).</summary>
        public const int MaxTerminalBytes = 16;

        /// <summary>
        /// A keyboard event as the kernel decodes it: the character it produced
        /// — Enter '\r', Backspace '\b', Tab '\t', Ctrl+letter the letter (or
        /// its control code) — else a UEFI scan code (EFI_SIMPLE_TEXT_INPUT:
        /// arrows 1–4, Home 5, End 6, Insert 7, Delete 8, PageUp 9, PageDown
        /// 0x0A, F1–F12 0x0B–0x16, Esc 0x17). Default (no key) for a release
        /// or a modifier alone. Ctrl+H stays Ctrl+H here and Ctrl+Backspace
        /// Ctrl+Backspace; in the terminal's bytes they are one byte.
        /// </summary>
        public static ConsoleKeyInfo FromKeyboard(char unicode, ushort scanCode, ConsoleModifiers modifiers)
        {
            if ((modifiers & ConsoleModifiers.Control) != 0)
            {
                // Ctrl+letter as Windows reports it: the control code, the letter's key.
                if (unicode >= 'a' && unicode <= 'z') return Make((char)(unicode - 'a' + 1), ConsoleKey.A + (unicode - 'a'), modifiers);
                if (unicode >= 'A' && unicode <= 'Z') return Make((char)(unicode - 'A' + 1), ConsoleKey.A + (unicode - 'A'), modifiers);
            }
            if (unicode != '\0') return FromChar(unicode, modifiers);

            ConsoleKey key;
            if (scanCode >= 0x0B && scanCode <= 0x16) key = ConsoleKey.F1 + (scanCode - 0x0B);
            else
            {
                switch (scanCode)
                {
                    case 0x01: key = ConsoleKey.UpArrow; break;
                    case 0x02: key = ConsoleKey.DownArrow; break;
                    case 0x03: key = ConsoleKey.RightArrow; break;
                    case 0x04: key = ConsoleKey.LeftArrow; break;
                    case 0x05: key = ConsoleKey.Home; break;
                    case 0x06: key = ConsoleKey.End; break;
                    case 0x07: key = ConsoleKey.Insert; break;
                    case 0x08: key = ConsoleKey.Delete; break;
                    case 0x09: key = ConsoleKey.PageUp; break;
                    case 0x0A: key = ConsoleKey.PageDown; break;
                    case 0x17: return Make('\u001b', ConsoleKey.Escape, modifiers);
                    default: return default;
                }
            }
            return Make('\0', key, modifiers);
        }

        /// <summary>
        /// A character as a terminal's byte: Shift is added where the character
        /// implies it (A, ?, !), Control where it is a control character —
        /// except CR, LF, BS, DEL and Tab, which are their keys.
        /// </summary>
        public static ConsoleKeyInfo FromChar(char c, ConsoleModifiers modifiers)
        {
            const ConsoleModifiers Shift = ConsoleModifiers.Shift;
            switch (c)
            {
                case '\r':
                case '\n': return Make('\r', ConsoleKey.Enter, modifiers);
                case '\b':
                case '\u007f': return Make('\b', ConsoleKey.Backspace, modifiers);
                case '\t': return Make('\t', ConsoleKey.Tab, modifiers);
                case '\u001b': return Make('\u001b', ConsoleKey.Escape, modifiers);
                case ' ': return Make(' ', ConsoleKey.Spacebar, modifiers);
            }
            if (c >= '\u0001' && c <= '\u001a') return Make(c, ConsoleKey.A + (c - 1), modifiers | ConsoleModifiers.Control);
            if (c < ' ') return Make(c, ConsoleKey.None, modifiers | ConsoleModifiers.Control);
            if (c >= 'a' && c <= 'z') return Make(c, ConsoleKey.A + (c - 'a'), modifiers);
            if (c >= 'A' && c <= 'Z') return Make(c, ConsoleKey.A + (c - 'A'), modifiers | Shift);
            if (c >= '0' && c <= '9') return Make(c, ConsoleKey.D0 + (c - '0'), modifiers);

            switch (c)
            {
                case ')': return Make(c, ConsoleKey.D0, modifiers | Shift);
                case '!': return Make(c, ConsoleKey.D1, modifiers | Shift);
                case '@': return Make(c, ConsoleKey.D2, modifiers | Shift);
                case '#': return Make(c, ConsoleKey.D3, modifiers | Shift);
                case '$': return Make(c, ConsoleKey.D4, modifiers | Shift);
                case '%': return Make(c, ConsoleKey.D5, modifiers | Shift);
                case '^': return Make(c, ConsoleKey.D6, modifiers | Shift);
                case '&': return Make(c, ConsoleKey.D7, modifiers | Shift);
                case '*': return Make(c, ConsoleKey.D8, modifiers | Shift);
                case '(': return Make(c, ConsoleKey.D9, modifiers | Shift);
                case ';': return Make(c, ConsoleKey.Oem1, modifiers);
                case ':': return Make(c, ConsoleKey.Oem1, modifiers | Shift);
                case '=': return Make(c, ConsoleKey.OemPlus, modifiers);
                case '+': return Make(c, ConsoleKey.OemPlus, modifiers | Shift);
                case ',': return Make(c, ConsoleKey.OemComma, modifiers);
                case '<': return Make(c, ConsoleKey.OemComma, modifiers | Shift);
                case '-': return Make(c, ConsoleKey.OemMinus, modifiers);
                case '_': return Make(c, ConsoleKey.OemMinus, modifiers | Shift);
                case '.': return Make(c, ConsoleKey.OemPeriod, modifiers);
                case '>': return Make(c, ConsoleKey.OemPeriod, modifiers | Shift);
                case '/': return Make(c, ConsoleKey.Oem2, modifiers);
                case '?': return Make(c, ConsoleKey.Oem2, modifiers | Shift);
                case '`': return Make(c, ConsoleKey.Oem3, modifiers);
                case '~': return Make(c, ConsoleKey.Oem3, modifiers | Shift);
                case '[': return Make(c, ConsoleKey.Oem4, modifiers);
                case '{': return Make(c, ConsoleKey.Oem4, modifiers | Shift);
                case '\\': return Make(c, ConsoleKey.Oem5, modifiers);
                case '|': return Make(c, ConsoleKey.Oem5, modifiers | Shift);
                case ']': return Make(c, ConsoleKey.Oem6, modifiers);
                case '}': return Make(c, ConsoleKey.Oem6, modifiers | Shift);
                case '\'': return Make(c, ConsoleKey.Oem7, modifiers);
                case '"': return Make(c, ConsoleKey.Oem7, modifiers | Shift);
            }
            return Make(c, ConsoleKey.None, modifiers);
        }

        /// <summary>
        /// The bytes xterm sends for <paramref name="key"/>; 0 for no key.
        /// <paramref name="into"/> holds at least <see cref="MaxTerminalBytes"/>.
        /// What a terminal cannot say is dropped: Shift on a control
        /// character (Ctrl+Shift+S is Ctrl+S), modifiers on Enter, Backspace, Esc.
        /// </summary>
        public static int ToTerminal(ConsoleKeyInfo key, Span<byte> into)
        {
            ConsoleModifiers m = key.Modifiers;
            bool shift = (m & ConsoleModifiers.Shift) != 0, alt = (m & ConsoleModifiers.Alt) != 0, ctrl = (m & ConsoleModifiers.Control) != 0;
            int modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

            switch (key.Key)
            {
                case ConsoleKey.UpArrow: return Csi(into, 1, 'A', modifier);
                case ConsoleKey.DownArrow: return Csi(into, 1, 'B', modifier);
                case ConsoleKey.RightArrow: return Csi(into, 1, 'C', modifier);
                case ConsoleKey.LeftArrow: return Csi(into, 1, 'D', modifier);
                case ConsoleKey.Home: return Csi(into, 1, 'H', modifier);
                case ConsoleKey.End: return Csi(into, 1, 'F', modifier);
                case ConsoleKey.Insert: return Tilde(into, 2, modifier);
                case ConsoleKey.Delete: return Tilde(into, 3, modifier);
                case ConsoleKey.PageUp: return Tilde(into, 5, modifier);
                case ConsoleKey.PageDown: return Tilde(into, 6, modifier);
                case ConsoleKey.F1: return Ss3(into, 'P', modifier);
                case ConsoleKey.F2: return Ss3(into, 'Q', modifier);
                case ConsoleKey.F3: return Ss3(into, 'R', modifier);
                case ConsoleKey.F4: return Ss3(into, 'S', modifier);
                case ConsoleKey.F5: return Tilde(into, 15, modifier);
                case ConsoleKey.F6: return Tilde(into, 17, modifier);
                case ConsoleKey.F7: return Tilde(into, 18, modifier);
                case ConsoleKey.F8: return Tilde(into, 19, modifier);
                case ConsoleKey.F9: return Tilde(into, 20, modifier);
                case ConsoleKey.F10: return Tilde(into, 21, modifier);
                case ConsoleKey.F11: return Tilde(into, 23, modifier);
                case ConsoleKey.F12: return Tilde(into, 24, modifier);
                case ConsoleKey.Tab when shift: return Csi(into, 0, 'Z', 1);      // back-tab
            }

            int n = 0;
            if (key.Key == ConsoleKey.Escape) { if (alt) into[n++] = 0x1B; into[n++] = 0x1B; return n; }
            if (key.KeyChar == '\0') return 0;
            if (alt) into[n++] = 0x1B;
            switch (key.Key)
            {
                case ConsoleKey.Enter: into[n++] = 0x0D; return n;
                case ConsoleKey.Backspace: into[n++] = 0x7F; return n;
                case ConsoleKey.Spacebar when ctrl: into[n++] = 0x00; return n;
            }
            return n + Utf8(key.KeyChar, into.Slice(n));
        }

        private static ConsoleKeyInfo Make(char c, ConsoleKey key, ConsoleModifiers m)
            => new ConsoleKeyInfo(c, key, (m & ConsoleModifiers.Shift) != 0, (m & ConsoleModifiers.Alt) != 0, (m & ConsoleModifiers.Control) != 0);

        // ESC [ final, or ESC [ 1 ; m final with modifiers.
        private static int Csi(Span<byte> o, int parameter, char final, int modifier)
        {
            int n = 0;
            o[n++] = 0x1B; o[n++] = (byte)'[';
            if (modifier > 1)
            {
                n += Digits(parameter, o.Slice(n));
                o[n++] = (byte)';';
                n += Digits(modifier, o.Slice(n));
            }
            o[n++] = (byte)final;
            return n;
        }

        // ESC [ n ~, or ESC [ n ; m ~.
        private static int Tilde(Span<byte> o, int number, int modifier)
        {
            int n = 0;
            o[n++] = 0x1B; o[n++] = (byte)'[';
            n += Digits(number, o.Slice(n));
            if (modifier > 1)
            {
                o[n++] = (byte)';';
                n += Digits(modifier, o.Slice(n));
            }
            o[n++] = (byte)'~';
            return n;
        }

        // F1–F4: ESC O P..S; with modifiers ESC [ 1 ; m P..S.
        private static int Ss3(Span<byte> o, char final, int modifier)
        {
            if (modifier > 1) return Csi(o, 1, final, modifier);
            o[0] = 0x1B; o[1] = (byte)'O'; o[2] = (byte)final;
            return 3;
        }

        private static int Digits(int value, Span<byte> o)
        {
            if (value >= 10) { o[0] = (byte)('0' + value / 10); o[1] = (byte)('0' + value % 10); return 2; }
            o[0] = (byte)('0' + value);
            return 1;
        }

        private static int Utf8(char c, Span<byte> o)
        {
            if (c < 0x80) { o[0] = (byte)c; return 1; }
            if (c < 0x800) { o[0] = (byte)(0xC0 | (c >> 6)); o[1] = (byte)(0x80 | (c & 0x3F)); return 2; }
            o[0] = (byte)(0xE0 | (c >> 12)); o[1] = (byte)(0x80 | ((c >> 6) & 0x3F)); o[2] = (byte)(0x80 | (c & 0x3F));
            return 3;
        }
    }
}
