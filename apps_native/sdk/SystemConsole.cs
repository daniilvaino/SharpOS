// System.Console for the app tier. Write/WriteLine go to the standard output
// (step212): a pipe of strings when one was handed over, the terminal
// otherwise; Error to the error stream. ReadKey, Clear, colours, window size,
// cursor and title talk to the terminal itself — the subset ported app code
// (ManagedDoom's logging, the shell's line editor, a full-screen pager) uses.
// Not here: CursorLeft/CursorTop, which need the terminal's answer to ESC [6n
// in the input (donext, "Ввод программы — поток байт терминала").

using SharpOS.AppSdk;

namespace System
{
    public static class Console
    {
        /// <summary>
        /// The standard output as text (step212), as BCL has it: a pipe of
        /// strings, a message a line, when the output was handed to a pipe or a
        /// file; the terminal otherwise. Opened on first use — after it, the
        /// standard output cannot be opened as another type (one type a pipe).
        /// </summary>
        public static System.IO.TextWriter Out => s_out ??= SharpOS.Std.Pipes.Pipe.ConsoleText();
        private static System.IO.TextWriter s_out;

        public static void Write(string value) => Out.Write(value);

        public static void WriteLine(string value) => Out.WriteLine(value);

        public static void WriteLine() => Out.WriteLine();

        /// <summary>
        /// The error stream (step208): the kernel's error port (COM4), or the
        /// console when the kernel has no such service.
        /// </summary>
        public static System.IO.TextWriter Error => s_error ??= SharpOS.Std.Pipes.Pipe.ErrorText() ?? new ErrorWriter();
        private static System.IO.TextWriter s_error;

        /// <summary>
        /// The standard input as bytes (step208): a pipe's byte[] messages, or
        /// its strings as UTF-8 text (SharpOS.Std.Pipes.Pipe.ReadBytes). With
        /// no input handed over, a stream that ends at once — the keyboard is
        /// ReadKey's. Opened once, as the pipe's standard end is.
        /// </summary>
        public static System.IO.Stream OpenStandardInput() => SharpOS.Std.Pipes.Pipe.ReadBytes();

        /// <summary>
        /// The standard output as bytes: the pipe's byte[] messages, on the
        /// screen as text when nothing reads the pipe
        /// (SharpOS.Std.Pipes.Pipe.WriteBytes). Buffered up to 64 KiB a
        /// message — Flush sends what is gathered.
        /// </summary>
        public static System.IO.Stream OpenStandardOutput() => SharpOS.Std.Pipes.Pipe.WriteBytes();

        /// <summary>The error stream as bytes, decoded as UTF-8 onto <see cref="Error"/>.</summary>
        public static System.IO.Stream OpenStandardError() => new ErrorStream();

        /// <summary>
        /// The next character of the standard input, or -1 at its end — as the
        /// BCL's Console.In.Read(). The keyboard is ReadKey's.
        /// </summary>
        public static int Read() => (s_in ??= SharpOS.Std.Pipes.Pipe.ReadText()).Read();
        private static System.IO.TextReader s_in;

        // Bytes to Error as UTF-8 text; a sequence cut by a write boundary
        // waits for the next write.
        private sealed class ErrorStream : System.IO.Stream
        {
            private readonly byte[] _pending = new byte[4];
            private int _pendingCount;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Flush() { }

            public override void Write(byte[] buffer, int offset, int count)
                => Write(new ReadOnlySpan<byte>(buffer, offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (buffer.Length == 0) return;
                byte[] all = new byte[_pendingCount + buffer.Length];
                new ReadOnlySpan<byte>(_pending, 0, _pendingCount).CopyTo(all);
                buffer.CopyTo(new Span<byte>(all, _pendingCount, buffer.Length));

                // Hold back an incomplete UTF-8 sequence at the end.
                int end = all.Length;
                int lead = end - 1;
                while (lead >= 0 && end - lead < 4 && (all[lead] & 0xC0) == 0x80) lead--;
                if (lead >= 0)
                {
                    byte b = all[lead];
                    int need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
                    if (end - lead < need) end = lead;
                }
                _pendingCount = all.Length - end;
                for (int i = 0; i < _pendingCount; i++) _pending[i] = all[end + i];

                if (end > 0) Error.Write(System.Text.Encoding.UTF8.GetString(all, 0, end));
            }
        }

        private sealed class ErrorWriter : System.IO.TextWriter
        {
            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
            public override void Write(char value) => Write(value.ToString());
            public override void Write(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                if (AppHost.HasErrorStream) AppHost.WriteError(value);
                else AppHost.WriteString(value);
            }
            public override void WriteLine(string value) => Write((value ?? "") + "\n");
            public override void WriteLine() => Write("\n");
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

        // The window (step210), as BCL has it on Unix: the size is what the
        // terminal's owner reports (here the kernel, ConsoleSize — the
        // TIOCGWINSZ of this system), cursor, visibility and title are escape
        // sequences written to the terminal. The size cannot be set. Zero when
        // there is no terminal to ask, as a failed ioctl gives there. Where BCL
        // on Unix throws from a getter (CursorVisible, Title), these give back
        // what was last set: the terminal does not report it.

        public static int WindowWidth
        {
            get => AppConsole.TryGetSize(out int columns, out _) ? columns : 0;
            set => throw new PlatformNotSupportedException();
        }

        public static int WindowHeight
        {
            get => AppConsole.TryGetSize(out _, out int rows) ? rows : 0;
            set => throw new PlatformNotSupportedException();
        }

        public static int BufferWidth => WindowWidth;
        public static int BufferHeight => WindowHeight;

        /// <summary>Moves the cursor to column <paramref name="left"/>, row <paramref name="top"/>, both from 0 (ESC [row;col H).</summary>
        public static void SetCursorPosition(int left, int top)
        {
            if (left < 0 || left >= short.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(left), left, "The value must be greater than or equal to zero and less than the console's buffer size in that dimension.");
            if (top < 0 || top >= short.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(top), top, "The value must be greater than or equal to zero and less than the console's buffer size in that dimension.");
            AppHost.WriteString("\u001b[" + (top + 1).ToString() + ";" + (left + 1).ToString() + "H");
        }

        private static bool s_cursorVisible = true;

        /// <summary>Shows or hides the cursor (ESC [?25h / ESC [?25l).</summary>
        public static bool CursorVisible
        {
            get => s_cursorVisible;
            set
            {
                s_cursorVisible = value;
                AppHost.WriteString(value ? "\u001b[?25h" : "\u001b[?25l");
            }
        }

        private static string s_title = "";

        /// <summary>
        /// The window title (OSC 0). Our terminal has no title bar and takes the
        /// sequence silently; a terminal on the far end of a serial line shows it.
        /// </summary>
        public static string Title
        {
            get => s_title;
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                if (value.Length > 24500)
                    throw new ArgumentOutOfRangeException(nameof(value), "The console title is too long.");
                s_title = value;
                AppHost.WriteString("\u001b]0;" + value + "\u0007");
            }
        }

        // Colours (step200) as the terminal takes them: ANSI SGR, the sixteen
        // ConsoleColor values on the 30–37/90–97 codes (backgrounds +10). The
        // terminal does not report its colours back, so these remember what
        // was set; the starting values are BCL's defaults on Windows.
        private static ConsoleColor s_foreground = ConsoleColor.Gray;
        private static ConsoleColor s_background = ConsoleColor.Black;

        public static ConsoleColor ForegroundColor
        {
            get => s_foreground;
            set { s_foreground = value; SetColor(value, background: false); }
        }

        public static ConsoleColor BackgroundColor
        {
            get => s_background;
            set { s_background = value; SetColor(value, background: true); }
        }

        public static void ResetColor()
        {
            s_foreground = ConsoleColor.Gray;
            s_background = ConsoleColor.Black;
            AppHost.WriteString("\u001b[0m");
        }

        private static void SetColor(ConsoleColor color, bool background)
        {
            if ((uint)color > (uint)ConsoleColor.White)
                throw new ArgumentException("The ConsoleColor enum value was not defined on that enum.", background ? "BackgroundColor" : "ForegroundColor");
            // ConsoleColor counts blue as bit 0, ANSI counts red as bit 0.
            // Dark ones (Black..Gray) on 30–37, bright ones (DarkGray..White) on 90–97.
            int c = (int)color;
            int ansi = ((c & 1) << 2) | (c & 2) | ((c & 4) >> 2);
            int code = (c < 8 ? 30 : 90) + ansi + (background ? 10 : 0);
            AppHost.WriteString("\u001b[" + code.ToString() + "m");
        }

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
