using OS.Hal;

namespace OS.Kernel.Input
{
    /// <summary>
    /// The terminal's input as bytes (step217): what a program reads from its
    /// terminal on Unix — keys as a terminal sends them, and the terminal's
    /// answers to the queries the program wrote.
    /// </summary>
    /// <remarks>
    /// Keys come from the same events as the raw key service
    /// (Keyboard.TryReadKeyRaw): one source, one modifier latch, so a game
    /// reading raw events and a shell reading bytes never take each other's
    /// half of a key. A press becomes what xterm sends for it: a printable
    /// key UTF-8, Enter CR, Backspace DEL, Ctrl+letter its control byte, Alt
    /// an ESC before the key, the arrows and the navigation cluster CSI
    /// sequences, F1–F4 SS3 and F5–F12 CSI n ~, modifiers on those as
    /// "1;m". Releases are not bytes.
    ///
    /// The terminal engine's answers (cursor position, device attributes,
    /// sizes) wait in a queue of their own and go out ahead of keys: a
    /// program that asked is waiting for exactly that.
    ///
    /// Fixed rings, no allocation: answers are queued from inside the
    /// terminal's write path, with switching suppressed.
    /// </remarks>
    internal static unsafe class TerminalInput
    {
        private const int AnswerSize = 512;
        private static readonly byte[] s_answers = new byte[AnswerSize];
        private static int s_answerHead, s_answerCount;

        // A key's bytes that did not fit the reader's buffer.
        private const int CarrySize = 32;
        private static readonly byte[] s_carry = new byte[CarrySize];
        private static int s_carryHead, s_carryCount;

        public static ulong AnswersDropped { get; private set; }

        /// <summary>The terminal's answer to a query, queued for the reader (TerminalConsole's delegate).</summary>
        public static void Answer(byte[] data)
        {
            if (data == null) return;
            for (int i = 0; i < data.Length; i++)
            {
                if (s_answerCount == AnswerSize) { AnswersDropped++; continue; }
                s_answers[(s_answerHead + s_answerCount) % AnswerSize] = data[i];
                s_answerCount++;
            }
        }

        /// <summary>
        /// Up to <paramref name="capacity"/> bytes into <paramref name="buffer"/>,
        /// without waiting: the terminal's answers first, then keys. 0 when
        /// there is nothing; negative when the keyboard is unsupported.
        /// </summary>
        public static int Read(byte* buffer, int capacity)
        {
            int n = 0;
            while (n < capacity && s_answerCount > 0)
            {
                buffer[n++] = s_answers[s_answerHead];
                s_answerHead = (s_answerHead + 1) % AnswerSize;
                s_answerCount--;
            }
            while (n < capacity && s_carryCount > 0)
            {
                buffer[n++] = s_carry[s_carryHead];
                s_carryHead = (s_carryHead + 1) % CarrySize;
                s_carryCount--;
            }

            byte* key = stackalloc byte[CarrySize];
            while (n < capacity)
            {
                KeyReadStatus status = Keyboard.TryReadKeyRaw(out KeyInfo info, out uint raw);
                if (status == KeyReadStatus.Unsupported) return n > 0 ? n : -1;
                if (status != KeyReadStatus.KeyAvailable) break;

                int length = Encode(info.UnicodeChar, info.ScanCode, key);
                int i = 0;
                for (; i < length && n < capacity; i++) buffer[n++] = key[i];
                for (; i < length && s_carryCount < CarrySize; i++)
                {
                    s_carry[(s_carryHead + s_carryCount) % CarrySize] = key[i];
                    s_carryCount++;
                }
            }
            return n;
        }

        // One key press as a terminal's bytes; 0 for a release or a modifier.
        private static int Encode(ushort unicode, ushort scan, byte* o)
        {
            bool shift = Ps2Keyboard.ShiftHeld, alt = Ps2Keyboard.AltHeld, ctrl = Ps2Keyboard.CtrlHeld;
            int modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

            if (unicode != 0)
            {
                int n = 0;
                if (alt) o[n++] = 0x1B;
                char c = (char)unicode;
                if (c == '\t' && shift) return Csi(o, 0, 'Z', 1);       // back-tab, ESC [ Z
                if (c == '\r' || c == '\n') { o[n++] = 0x0D; return n; }
                if (c == '\b') { o[n++] = 0x7F; return n; }
                return n + Utf8(c, o + n);
            }

            switch (scan)
            {
                case 0x01: return Csi(o, 1, 'A', modifier);
                case 0x02: return Csi(o, 1, 'B', modifier);
                case 0x03: return Csi(o, 1, 'C', modifier);
                case 0x04: return Csi(o, 1, 'D', modifier);
                case 0x05: return Csi(o, 1, 'H', modifier);
                case 0x06: return Csi(o, 1, 'F', modifier);
                case 0x07: return Tilde(o, 2, modifier);
                case 0x08: return Tilde(o, 3, modifier);
                case 0x09: return Tilde(o, 5, modifier);
                case 0x0A: return Tilde(o, 6, modifier);
                case 0x0B: return Ss3(o, 'P', modifier);
                case 0x0C: return Ss3(o, 'Q', modifier);
                case 0x0D: return Ss3(o, 'R', modifier);
                case 0x0E: return Ss3(o, 'S', modifier);
                case 0x0F: return Tilde(o, 15, modifier);
                case 0x10: return Tilde(o, 17, modifier);
                case 0x11: return Tilde(o, 18, modifier);
                case 0x12: return Tilde(o, 19, modifier);
                case 0x13: return Tilde(o, 20, modifier);
                case 0x14: return Tilde(o, 21, modifier);
                case 0x15: return Tilde(o, 23, modifier);
                case 0x16: return Tilde(o, 24, modifier);
                case 0x17: o[0] = 0x1B; return 1;
                default: return 0;
            }
        }

        // ESC [ final, or ESC [ 1 ; m final with modifiers (xterm).
        private static int Csi(byte* o, int parameter, char final, int modifier)
        {
            int n = 0;
            o[n++] = 0x1B; o[n++] = (byte)'[';
            if (modifier > 1)
            {
                n += Digits(parameter, o + n);
                o[n++] = (byte)';';
                n += Digits(modifier, o + n);
            }
            o[n++] = (byte)final;
            return n;
        }

        // ESC [ n ~, or ESC [ n ; m ~.
        private static int Tilde(byte* o, int number, int modifier)
        {
            int n = 0;
            o[n++] = 0x1B; o[n++] = (byte)'[';
            n += Digits(number, o + n);
            if (modifier > 1)
            {
                o[n++] = (byte)';';
                n += Digits(modifier, o + n);
            }
            o[n++] = (byte)'~';
            return n;
        }

        // F1–F4: ESC O P..S; with modifiers ESC [ 1 ; m P..S.
        private static int Ss3(byte* o, char final, int modifier)
        {
            if (modifier > 1) return Csi(o, 1, final, modifier);
            o[0] = 0x1B; o[1] = (byte)'O'; o[2] = (byte)final;
            return 3;
        }

        private static int Digits(int value, byte* o)
        {
            if (value >= 10) { o[0] = (byte)('0' + value / 10); o[1] = (byte)('0' + value % 10); return 2; }
            o[0] = (byte)('0' + value);
            return 1;
        }

        private static int Utf8(char c, byte* o)
        {
            if (c < 0x80) { o[0] = (byte)c; return 1; }
            if (c < 0x800) { o[0] = (byte)(0xC0 | (c >> 6)); o[1] = (byte)(0x80 | (c & 0x3F)); return 2; }
            o[0] = (byte)(0xE0 | (c >> 12)); o[1] = (byte)(0x80 | ((c >> 6) & 0x3F)); o[2] = (byte)(0x80 | (c & 0x3F));
            return 3;
        }
    }
}
