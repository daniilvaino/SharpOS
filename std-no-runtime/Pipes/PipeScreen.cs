using System;
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
        /// streams shown at once take turns a message each. An object or an
        /// array also goes to <paramref name="objects"/>, as a tree, when
        /// one is given: the inspector beside the screen — one marked not for
        /// the terminal too.
        /// </summary>
        public static unsafe void Show(PipeReadEnd end, TextWriter objects = null)
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
                    // Data for programs is not a line for a person; the
                    // inspector is where one looks at it.
                    if ((attributes & (uint)PipeAttributes.NotForTerminal) != 0 && HintsHonoured)
                    {
                        if (objects != null)
                            lock (showing) ScreenText.Inspect(region.Root, objects);
                        continue;
                    }
                    lock (showing)
                    {
                        if ((attributes & (uint)PipeAttributes.TerminalText) != 0
                            && region.Root.TryGetChars(out char* chars, out int count))
                            PipeTransport.PrintText(new string(new ReadOnlySpan<char>(chars, count)));
                        else
                        {
                            ScreenText.Print(region.Root);
                            if (objects != null) ScreenText.Inspect(region.Root, objects);
                        }
                    }
                }
                finally
                {
                    region.Dispose();
                }
            }
            lock (showing) ScreenText.End();
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
