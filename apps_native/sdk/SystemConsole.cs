// System.Console for the app tier, backed by the AppHost service table
// (kernel console via WriteString). Write/WriteLine, ReadKey and Clear — the
// subset ported app code (ManagedDoom's logging, the shell's line editor) uses.

using SharpOS.AppSdk;

namespace System
{
    public static class Console
    {
        public static void Write(string value)
        {
            if (value != null) AppHost.WriteString(value);
        }

        public static void WriteLine(string value)
        {
            if (value != null) AppHost.WriteString(value);
            AppHost.WriteString("\n");
        }

        public static void WriteLine()
        {
            AppHost.WriteString("\n");
        }

        // BCL's object overloads. Ported code reaches for these without
        // thinking — Console.WriteLine(e) in a catch block is the common one —
        // and the alternative is a compile error at a call site nobody wants to
        // edit. ToString() on an exception gives its message here rather than
        // the type-and-stack the BCL prints.
        public static void Write(object value) => Write(value?.ToString());

        public static void WriteLine(object value) => WriteLine(value?.ToString());

        /// <summary>
        /// The next key press, waiting for one (step197); echoed unless
        /// <paramref name="intercept"/>. Ctrl+letter arrives as its control
        /// character with the letter's key and Control set, as on a terminal.
        /// Shift and Alt are reported only where the character shows them.
        /// </summary>
        public static ConsoleKeyInfo ReadKey(bool intercept)
        {
            while (true)
            {
                if (AppHost.TryReadKey(out KeyInfo raw) != AppServiceStatus.Ok)
                {
                    AppThreads.Sleep(KeyPollMilliseconds);
                    continue;
                }
                if (!TryTranslate(raw, out ConsoleKeyInfo key)) continue;
                if (!intercept && key.KeyChar >= ' ') AppHost.WriteChar(key.KeyChar);
                return key;
            }
        }

        public static ConsoleKeyInfo ReadKey() => ReadKey(false);

        /// <summary>Clears the screen and puts the cursor at its top left (the terminal's ESC [H ESC [2J).</summary>
        public static void Clear() => AppHost.WriteString("\u001b[H\u001b[2J");

        // The key service never blocks: a poll with a sleep, below what a
        // typist can feel (the shell's prompt used the same ten milliseconds).
        private const uint KeyPollMilliseconds = 10;

        // UEFI scan codes for the keys with no character (the kernel's
        // Platform.TryReadKey speaks them for PS/2, USB and the serial line).
        private static bool TryTranslate(KeyInfo raw, out ConsoleKeyInfo key)
        {
            key = default;
            char c = (char)raw.UnicodeChar;
            if (c == '\0')
            {
                ConsoleKey special = raw.ScanCode switch
                {
                    0x01 => ConsoleKey.UpArrow,
                    0x02 => ConsoleKey.DownArrow,
                    0x03 => ConsoleKey.RightArrow,
                    0x04 => ConsoleKey.LeftArrow,
                    0x05 => ConsoleKey.Home,
                    0x06 => ConsoleKey.End,
                    0x07 => ConsoleKey.Insert,
                    0x08 => ConsoleKey.Delete,
                    0x09 => ConsoleKey.PageUp,
                    0x0A => ConsoleKey.PageDown,
                    0x17 => ConsoleKey.Escape,
                    _ => ConsoleKey.None,
                };
                if (special == ConsoleKey.None) return false;   // a release, a modifier
                key = new ConsoleKeyInfo(special == ConsoleKey.Escape ? '\u001b' : '\0', special, false, false, false);
                return true;
            }

            if (c == '\r' || c == '\n') { key = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false); return true; }
            if (c == '\b' || c == '\u007f') { key = new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false); return true; }
            if (c == '\t') { key = new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false); return true; }
            if (c == '\u001b') { key = new ConsoleKeyInfo(c, ConsoleKey.Escape, false, false, false); return true; }
            if (c >= '\u0001' && c <= '\u001a')
            {
                key = new ConsoleKeyInfo(c, (ConsoleKey)((int)ConsoleKey.A + c - 1), false, false, true);
                return true;
            }
            if (c == ' ') { key = new ConsoleKeyInfo(c, ConsoleKey.Spacebar, false, false, false); return true; }
            if (c >= 'a' && c <= 'z') { key = new ConsoleKeyInfo(c, (ConsoleKey)((int)ConsoleKey.A + c - 'a'), false, false, false); return true; }
            if (c >= 'A' && c <= 'Z') { key = new ConsoleKeyInfo(c, (ConsoleKey)((int)ConsoleKey.A + c - 'A'), true, false, false); return true; }
            if (c >= '0' && c <= '9') { key = new ConsoleKeyInfo(c, (ConsoleKey)((int)ConsoleKey.D0 + c - '0'), false, false, false); return true; }
            if (c < ' ') return false;
            key = new ConsoleKeyInfo(c, ConsoleKey.None, false, false, false);
            return true;
        }
    }
}
