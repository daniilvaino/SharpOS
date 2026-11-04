using System;
using System.Collections.Generic;
using System.IO;

namespace SharpOS.Std.Pipes
{
    // A pipe that ends on the screen (step219) — what a tty is on Unix. The
    // shell hands the last stage of a pipeline such a pipe for its standard
    // output and stdtext and shows what comes: the stage sees the terminal
    // there (Pipe.OutputIsTerminal, isatty), writes text as it goes, escapes
    // and pieces of lines included, and the shell has its objects. What the
    // ends say to each other travels in the pipe's attributes, not in the
    // messages.

    /// <summary>Bits of a pipe both ends see (step219), kept by the kernel.</summary>
    [Flags]
    public enum PipeAttributes : uint
    {
        None = 0,

        /// <summary>The reader shows the stream on the screen: to the writer, the terminal.</summary>
        Screen = 1,

        /// <summary>The writer's hint (<see cref="PipeHints.NotForTerminal"/>): data for programs, not shown.</summary>
        NotForTerminal = 2,

        /// <summary>Strings are terminal text as written — escapes, pieces of lines — not a line each.</summary>
        TerminalText = 4,

        /// <summary>
        /// Strings are text a program wrote for the terminal (System.Console,
        /// stdtext) into a pipe that is not the screen: its controls mean
        /// nothing here, and a reader making plain text leaves them out
        /// (<see cref="Pipe.InputIsConsoleText"/>, <see cref="Pipe.PlainText"/>).
        /// </summary>
        ConsoleText = 8,
    }

    /// <summary>Which stream of a stage a shown pipe carries (step239).</summary>
    public enum ScreenStream
    {
        /// <summary>The standard output: shown, and kept by the inspector.</summary>
        Output,

        /// <summary>stdtext: text for people beside the output.</summary>
        Text,

        /// <summary>The error stream: shown, and logged as errors.</summary>
        Errors,
    }

    public static partial class Pipe
    {
        private static int s_outputScreen;      // 0 not asked, 1 a screen pipe, 2 not

        /// <summary>Whether <paramref name="handle"/> is an end of a pipe the reader shows on the screen.</summary>
        internal static bool IsScreenEnd(int handle)
            => handle != 0 && PipeTransport.Attributes(handle, 0, 0, out uint attributes) == PipeStatus.Ok
               && (attributes & (uint)PipeAttributes.Screen) != 0;

        /// <summary>Whether the standard output is a pipe whose reader shows it on the screen.</summary>
        public static bool OutputIsScreenPipe
        {
            get
            {
                if (s_outputScreen == 0) s_outputScreen = IsScreenEnd(PipeTransport.StandardEnd(1)) ? 1 : 2;
                return s_outputScreen == 1;
            }
        }

        /// <summary>
        /// Whether the standard output is the terminal (isatty(1)): none was
        /// handed over, or the one handed over is shown on the screen.
        /// </summary>
        public static bool OutputIsTerminal => PipeTransport.StandardEnd(1) == 0 || OutputIsScreenPipe;

        private static int s_errorScreen;       // 0 not asked, 1 the terminal, 2 not

        /// <summary>
        /// Whether the error stream is the terminal (isatty(2), step239): none
        /// was handed over — the kernel's error port, on the screen — or the
        /// one handed over is shown on the screen by the shell.
        /// </summary>
        public static bool ErrorIsTerminal
        {
            get
            {
                if (s_errorScreen == 0)
                {
                    int handle = PipeTransport.StandardEnd(ErrorEnd);
                    s_errorScreen = handle == 0 || IsScreenEnd(handle) ? 1 : 2;
                }
                return s_errorScreen == 1;
            }
        }

        /// <summary>Whether the standard input is the keyboard (isatty(0), step239): none was handed over.</summary>
        public static bool InputIsTerminal => PipeTransport.StandardEnd(0) == 0;

        /// <summary>
        /// Whether the standard input is text a program wrote for the terminal
        /// (step242) — `PROG > file`, `FETCH 3> file`: colours and cursor moves
        /// that a file or a line of data has no use for. Known once the writer
        /// opened its end; asked after the first message, or after WaitSchema.
        /// </summary>
        public static bool InputIsConsoleText
        {
            get
            {
                int handle = PipeTransport.StandardEnd(0);
                return handle != 0 && PipeTransport.Attributes(handle, 0, 0, out uint attributes) == PipeStatus.Ok
                       && (attributes & (uint)PipeAttributes.ConsoleText) != 0;
            }
        }

        /// <summary>
        /// Terminal text as plain text (step242): escape sequences — colours,
        /// cursor moves, titles — and other control characters left out, tab
        /// kept.
        /// </summary>
        public static string PlainText(ReadOnlySpan<char> text) => ScreenText.Plain(text);

        /// <summary>Whether the standard output was opened already, as text or as a type.</summary>
        public static bool OutputIsOpen => s_outputOpened;

        /// <summary>
        /// A new pipe whose reader shows it on the screen (<see cref="Show"/>);
        /// null on a kernel without pipe attributes.
        /// </summary>
        public static PipePair CreateScreen()
        {
            PipePair pair = Create();
            if (PipeTransport.Attributes(pair.ReadEnd.Peek(), (uint)PipeAttributes.Screen, (uint)PipeAttributes.Screen, out _) == PipeStatus.Ok)
                return pair;
            pair.WriteEnd.Dispose();
            pair.ReadEnd.Dispose();
            return null;
        }

        private static object s_showing;

        /// <summary>
        /// Shows what comes down <paramref name="end"/> on the screen until the
        /// stream ends, as a program with no output would print it: a string
        /// on its line — as it is when the writer said it is terminal text —
        /// a byte[] as text, anything else as a view prints it. Nothing of a
        /// stream marked not for the terminal, unless PIPEHINTS=off. Two
        /// streams shown at once take turns a message each. Every message
        /// also goes to <paramref name="inspector"/> when one is given — the
        /// inspector beside the screen, step237 — one marked not for the
        /// terminal too; a run of objects of one type is then its first line
        /// and a summary line, not a line each.
        /// </summary>
        public static unsafe void Show(PipeReadEnd end, IScreenInspector inspector = null)
        {
            object showing = s_showing ??= new object();
            using RawPipeReader reader = end.Read();
            bool asked = false;
            uint attributes = 0;
            while (true)
            {
                RawRegion region = reader.Receive();
                if (region == null) break;
                try
                {
                    // The writer marks the pipe before its first message.
                    if (!asked)
                    {
                        PipeTransport.Attributes(reader.Handle, 0, 0, out attributes);
                        asked = true;
                    }
                    // Data for programs is not a line for a person: only the
                    // inspector, where one looks at it, gets it (ShowOne).
                    lock (showing) ShowOne(region, attributes, ScreenStream.Output, inspector);
                }
                finally
                {
                    region.Dispose();
                }
            }
            lock (showing) ScreenText.End();
        }

        /// <summary>
        /// The streams of a stage the screen shows, read as one (step239): each
        /// message in the order it was sent across all of them — an error after
        /// the line printed before it, not ahead of it as when each stream had
        /// a reader of its own. The output goes to <paramref name="inspector"/>
        /// as <see cref="Show(PipeReadEnd, IScreenInspector)"/> gives it; stdtext
        /// and errors do not; errors are printed as errors (the error log). On
        /// a kernel without an order across pipes, each is shown on its own.
        /// </summary>
        public static unsafe void Show(IReadOnlyList<(PipeReadEnd End, ScreenStream Stream)> ends, IScreenInspector inspector = null)
        {
            int count = ends.Count;
            if (count == 0) return;
            var readers = new RawPipeReader[count];
            var streams = new ScreenStream[count];
            var attributes = new uint[count];
            var asked = new bool[count];
            int* handles = stackalloc int[count];
            for (int i = 0; i < count; i++)
            {
                readers[i] = ends[i].End.Read();
                streams[i] = ends[i].Stream;
                handles[i] = readers[i].Handle;
            }

            object showing = s_showing ??= new object();
            int live = count;
            try
            {
                while (live > 0)
                {
                    PipeStatus next = PipeTransport.NextOf(handles, live, true, out int at);
                    if (next == PipeStatus.Unsupported)
                    {
                        ShowEachAlone(readers, streams, live, inspector);
                        return;
                    }
                    if (next != PipeStatus.Ok || at < 0 || at >= live) break;

                    RawRegion region = readers[at].Receive();
                    if (region == null)
                    {
                        readers[at].Dispose();
                        live--;
                        readers[at] = readers[live];
                        streams[at] = streams[live];
                        attributes[at] = attributes[live];
                        asked[at] = asked[live];
                        handles[at] = handles[live];
                        continue;
                    }
                    try
                    {
                        if (!asked[at])
                        {
                            PipeTransport.Attributes(readers[at].Handle, 0, 0, out attributes[at]);
                            asked[at] = true;
                        }
                        lock (showing) ShowOne(region, attributes[at], streams[at], inspector);
                    }
                    finally
                    {
                        region.Dispose();
                    }
                }
            }
            finally
            {
                for (int i = 0; i < live; i++) readers[i].Dispose();
                lock (showing) ScreenText.End();
            }
        }

        // An older kernel: a task per stream, as before step239.
        private static void ShowEachAlone(RawPipeReader[] readers, ScreenStream[] streams, int count, IScreenInspector inspector)
        {
            var tasks = new System.Threading.Tasks.Task[count];
            for (int i = 0; i < count; i++)
            {
                RawPipeReader reader = readers[i];
                ScreenStream stream = streams[i];
                tasks[i] = System.Threading.Tasks.Task.Run(() =>
                {
                    object showing = s_showing;
                    uint attributes = 0;
                    bool asked = false;
                    try
                    {
                        RawRegion region;
                        while ((region = reader.Receive()) != null)
                        {
                            try
                            {
                                if (!asked)
                                {
                                    PipeTransport.Attributes(reader.Handle, 0, 0, out attributes);
                                    asked = true;
                                }
                                lock (showing) ShowOne(region, attributes, stream, inspector);
                            }
                            finally { region.Dispose(); }
                        }
                    }
                    finally { reader.Dispose(); }
                });
            }
            foreach (System.Threading.Tasks.Task task in tasks) task.Wait();
        }

        // One message of a shown stream; callers hold the screen's lock.
        private static unsafe void ShowOne(RawRegion region, uint attributes, ScreenStream stream, IScreenInspector inspector)
        {
            IScreenInspector keeper = stream == ScreenStream.Output ? inspector : null;
            if ((attributes & (uint)PipeAttributes.NotForTerminal) != 0 && HintsHonoured)
            {
                keeper?.Take(region.Root, false);
                return;
            }
            bool terminalText = (attributes & (uint)PipeAttributes.TerminalText) != 0
                                && region.Root.TryGetChars(out _, out _);
            if (terminalText && region.Root.TryGetChars(out char* chars, out int count))
            {
                ScreenText.EndRun();
                string text = new string(new ReadOnlySpan<char>(chars, count));
                if (stream == ScreenStream.Errors) PipeTransport.PrintError(text);
                else PipeTransport.PrintText(text);
            }
            else if (stream == ScreenStream.Errors && region.Root.TryGetChars(out char* line, out int length))
            {
                ScreenText.EndRun();
                PipeTransport.PrintError(ScreenText.Visible(new ReadOnlySpan<char>(line, length), keepColours: true) + "\n");
            }
            else
                ScreenText.Print(region.Root, keeper);
            keeper?.Take(region.Root, terminalText);
        }

        // A writer of text to a screen pipe: each write a message as it is,
        // no line to wait for — a prompt shows before its input, a frame
        // drawn by moving the cursor shows at all.
        private static TextWriter ScreenTextWriter(int handle, string what)
        {
            PipeTransport.Attributes(handle, (uint)PipeAttributes.TerminalText, (uint)PipeAttributes.TerminalText, out _);
            return new PipeScreenWriter(OpenWriter<string>(handle, what));
        }
    }

    internal sealed class PipeScreenWriter : TextWriter
    {
        private readonly PipeWriter<string> _writer;

        internal PipeScreenWriter(PipeWriter<string> writer) => _writer = writer;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => _writer.Copy(value.ToString());

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            // A message has a limit; a long text goes in pieces, not cut inside a pair.
            int at = 0;
            while (value.Length - at > PipeTextWriter.MaxLineChars)
            {
                int length = PipeTextWriter.MaxLineChars;
                if (char.IsHighSurrogate(value[at + length - 1])) length--;
                _writer.Copy(value.Substring(at, length));
                at += length;
            }
            _writer.Copy(at == 0 ? value : value.Substring(at));
        }

        public override void WriteLine(string? value) => Write((value ?? "") + "\n");

        public override void WriteLine() => _writer.Copy("\n");

        protected override void Dispose(bool disposing)
        {
            if (disposing) _writer.Dispose();
            base.Dispose(disposing);
        }
    }
}
