// System.Console for the app tier. Write/WriteLine go to the standard output
// (step212): a pipe of strings when one was handed over, the terminal
// otherwise; Error to the error stream. ReadKey, Clear, colours, window size,
// cursor and title talk to the terminal itself — the subset ported app code
// (ManagedDoom's logging, the shell's line editor, a full-screen pager) uses.
// Keys are the terminal's input as bytes since step217 (TerminalKeys), as on
// Unix; CursorLeft/CursorTop ask the terminal (ESC [6n) and read its answer
// there.

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

        /// <summary>Sets <see cref="Out"/>, as BCL does (without its synchronised wrapper).</summary>
        public static void SetOut(System.IO.TextWriter newOut)
            => s_out = newOut ?? throw new ArgumentNullException(nameof(newOut));

        public static void Write(string value) => Out.Write(value);

        public static void WriteLine(string value) => Out.WriteLine(value);

        public static void WriteLine() => Out.WriteLine();

        /// <summary>
        /// An escape sequence for the terminal — cursor, colours, clearing, a
        /// key's echo — in the same stream as the text (step219), as on Unix:
        /// when the shell shows the standard output, text sent past it would
        /// land after the cursor moves meant for it. Straight to the terminal
        /// otherwise: a file or a pipe gets no escapes, and an output opened
        /// as a type carries no text.
        /// </summary>
        internal static void Control(string sequence)
        {
            if (SharpOS.Std.Pipes.Pipe.OutputIsScreenPipe && (s_out != null || !SharpOS.Std.Pipes.Pipe.OutputIsOpen))
                Out.Write(sequence);
            else
                AppHost.Terminal.Write(sequence);
        }

        /// <summary>
        /// The unfinished lines of the standard output, the error stream and
        /// stdtext, sent as the program ends (AppRuntime.Finish): a text
        /// writer on a pipe sends a line when it ends, and one without a
        /// line break was otherwise lost with the process.
        /// </summary>
        internal static void FlushAtExit()
        {
            s_out?.Flush();
            s_error?.Flush();
            SharpOS.Std.Pipes.Pipe.FlushText();
        }

        /// <summary>
        /// As BCL (step239): whether the standard output goes elsewhere than
        /// the terminal — a file, the next stage, nowhere. The screen the
        /// shell shows is the terminal (isatty, Pipe.OutputIsTerminal). What
        /// ported code asks before it colours: `!IsOutputRedirected`.
        /// </summary>
        public static bool IsOutputRedirected => !SharpOS.Std.Pipes.Pipe.OutputIsTerminal;

        /// <summary>As BCL: whether the error stream goes elsewhere than the terminal (`2&gt; file`, `2&gt;&amp;1 | …`).</summary>
        public static bool IsErrorRedirected => !SharpOS.Std.Pipes.Pipe.ErrorIsTerminal;

        /// <summary>As BCL: whether the standard input comes from elsewhere than the keyboard — a pipe, `&lt; file`.</summary>
        public static bool IsInputRedirected => !SharpOS.Std.Pipes.Pipe.InputIsTerminal;

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
                else AppHost.Terminal.Write(value);
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

        // BCL's value overloads, without the box the object ones take.
        public static void Write(char value) => Out.Write(value);
        public static void Write(bool value) => Write(value.ToString());
        public static void Write(int value) => Write(value.ToString());
        public static void Write(uint value) => Write(value.ToString());
        public static void Write(long value) => Write(value.ToString());
        public static void Write(ulong value) => Write(value.ToString());

        public static void WriteLine(char value) => WriteLine(value.ToString());
        public static void WriteLine(bool value) => WriteLine(value.ToString());
        public static void WriteLine(int value) => WriteLine(value.ToString());
        public static void WriteLine(uint value) => WriteLine(value.ToString());
        public static void WriteLine(long value) => WriteLine(value.ToString());
        public static void WriteLine(ulong value) => WriteLine(value.ToString());

        /// <summary>
        /// The next key press, waiting for one (step197); echoed unless
        /// <paramref name="intercept"/>. Ctrl+letter arrives as its control
        /// character with the letter's key and Control set, as on a terminal;
        /// Ctrl+C is 0x03. Since step217 parsed from the terminal's bytes:
        /// modifiers on the arrows, the navigation keys and F1–F12, Alt as an
        /// ESC before a key.
        /// </summary>
        public static ConsoleKeyInfo ReadKey(bool intercept)
        {
            if (TerminalKeys.Available)
            {
                TerminalKeys.TryRead(-1, out ConsoleKeyInfo read);
                if (!intercept && read.KeyChar >= ' ') Control(read.KeyChar.ToString());
                return read;
            }
            while (true)
            {
                if (AppHost.TryReadKey(out KeyInfo raw) != AppServiceStatus.Ok)
                {
                    AppThreads.Sleep(KeyPollMilliseconds);
                    continue;
                }
                ConsoleKeyInfo key = raw.ToConsoleKeyInfo();
                if (key.Key == ConsoleKey.None && key.KeyChar == '\0') continue;   // a release, a modifier
                if (!intercept && key.KeyChar >= ' ') Control(key.KeyChar.ToString());
                return key;
            }
        }

        public static ConsoleKeyInfo ReadKey() => ReadKey(false);

        /// <summary>Whether a key press is waiting to be read.</summary>
        public static bool KeyAvailable => TerminalKeys.Available && TerminalKeys.KeyAvailable;

        // Ctrl+C (release 0.1 §10): the kernel's signal to the terminal's
        // owner. CancelKeyPress as the BCL's on Unix — over
        // PosixSignalRegistration (SIGINT and SIGQUIT), its handlers in a
        // thread of their own; uncancelled, the process ends with 130.
        private static readonly object s_syncObject = new object();
        private static ConsoleCancelEventHandler? s_cancelCallbacks;
        private static System.Runtime.InteropServices.PosixSignalRegistration? s_sigIntRegistration;
        private static System.Runtime.InteropServices.PosixSignalRegistration? s_sigQuitRegistration;

        public static event ConsoleCancelEventHandler? CancelKeyPress
        {
            add
            {
                // Threads before the lock (see EnsureThreads).
                System.Runtime.InteropServices.PosixSignalRegistration.EnsureThreads();

                lock (s_syncObject)
                {
                    s_cancelCallbacks += value;

                    // If we haven't registered our control-C/Break handlers, do it.
                    if (s_sigIntRegistration is null)
                    {
                        Action<System.Runtime.InteropServices.PosixSignalContext> handler = HandlePosixSignal;
                        s_sigIntRegistration = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, handler);
                        s_sigQuitRegistration = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGQUIT, handler);
                    }
                }
            }
            remove
            {
                lock (s_syncObject)
                {
                    s_cancelCallbacks -= value;

                    // If there are no more callbacks, unregister registered posix signal handlers.
                    if (s_cancelCallbacks == null)
                    {
                        s_sigIntRegistration?.Dispose();
                        s_sigQuitRegistration?.Dispose();
                        s_sigIntRegistration = s_sigQuitRegistration = null;
                    }
                }
            }
        }

        /// <summary>
        /// Ctrl+C is a key to read (0x03), not a signal — while any process of
        /// the terminal's owning group says so, as ISIG off on Unix.
        /// </summary>
        public static bool TreatControlCAsInput
        {
            get => Process.InterruptSettings(0, false, out bool input, out _) && input;
            set => Process.InterruptSettings(1, value, out _, out _);
        }

        private static void HandlePosixSignal(System.Runtime.InteropServices.PosixSignalContext ctx)
        {
            if (s_cancelCallbacks is ConsoleCancelEventHandler handler)
            {
                var args = new ConsoleCancelEventArgs(ctx.Signal == System.Runtime.InteropServices.PosixSignal.SIGINT ? ConsoleSpecialKey.ControlC : ConsoleSpecialKey.ControlBreak);
                args.Cancel = ctx.Cancel;
                handler(null, args);
                ctx.Cancel = args.Cancel;
            }
        }

        /// <summary>800 Hz for 200 ms, as BCL.</summary>
        public static void Beep() => Beep(800, 200);

        /// <summary>
        /// BCL's Console.Beep (step248): the PC speaker, waiting out the
        /// duration as Windows does. Without a speaker, BEL to the terminal —
        /// what BCL on Unix does for every beep.
        /// </summary>
        public static void Beep(int frequency, int duration)
        {
            if (frequency < 37 || frequency > 32767)
                throw new ArgumentOutOfRangeException(nameof(frequency), "Frequency must be between 37 and 32767.");
            if (duration <= 0)
                throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
            if (SharpOS.AppSdk.Audio.Speaker(frequency, duration))
                System.Threading.Thread.Sleep(duration);
            else
                Control("\u0007");
        }

        /// <summary>
        /// The cursor's column and row, from 0, as the terminal reports them
        /// (ESC [6n); (0, 0) when no answer comes — BCL on Unix gives the same
        /// when nothing answers.
        /// </summary>
        public static (int Left, int Top) GetCursorPosition()
        {
            if (TerminalKeys.Available && TerminalKeys.TryGetCursorPosition(out int left, out int top)) return (left, top);
            return (0, 0);
        }

        public static int CursorLeft
        {
            get => GetCursorPosition().Left;
            set => SetCursorPosition(value, GetCursorPosition().Top);
        }

        public static int CursorTop
        {
            get => GetCursorPosition().Top;
            set => SetCursorPosition(GetCursorPosition().Left, value);
        }

        /// <summary>Clears the screen and puts the cursor at its top left (the terminal's ESC [H ESC [2J).</summary>
        public static void Clear() => Control("\u001b[H\u001b[2J");

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
            Control("\u001b[" + (top + 1).ToString() + ";" + (left + 1).ToString() + "H");
        }

        private static bool s_cursorVisible = true;

        /// <summary>Shows or hides the cursor (ESC [?25h / ESC [?25l).</summary>
        public static bool CursorVisible
        {
            get => s_cursorVisible;
            set
            {
                s_cursorVisible = value;
                Control(value ? "\u001b[?25h" : "\u001b[?25l");
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
                Control("\u001b]0;" + value + "\u0007");
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
            Control("\u001b[0m");
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
            Control("\u001b[" + code.ToString() + "m");
        }

        // The key service never blocks: a poll with a sleep, below what a
        // typist can feel (the shell's prompt used the same ten milliseconds).
        private const uint KeyPollMilliseconds = 10;
    }
}
