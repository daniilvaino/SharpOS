using System;
using System.IO;

namespace SharpOS.Std.Pipes
{
    // Ends without a type yet: a pair made by Pipe.Create(), and the standard
    // input and output a program is handed at start (step194 §5).
    //
    // An end of a pair is opened once — as a reader or a writer of some type,
    // or handed to a process being started — and the first end opened with a
    // type declares it; the other is checked against it, as two ends meeting
    // by name are. The standard ends are the same thing seen from the started
    // program: Pipe.Read(), Read<T>(), Write<T>() and WriteTo() without a name.
    // No input is an empty stream; no output is the screen. System.Console's
    // text is the standard output too (step212), a pipe of strings; one
    // program's standard output has one type, and text beside data goes to
    // Pipe.Text (stdtext) or Console.Error.

    /// <summary>The two ends of a new pipe, both this program's until one is opened or handed over.</summary>
    public sealed class PipePair
    {
        internal PipePair(int writer, int reader)
        {
            WriteEnd = new PipeWriteEnd(writer);
            ReadEnd = new PipeReadEnd(reader);
        }

        public PipeWriteEnd WriteEnd { get; }
        public PipeReadEnd ReadEnd { get; }
    }

    /// <summary>What both untyped ends share: a handle, used once.</summary>
    public abstract class PipeEnd : IDisposable
    {
        private int _handle;
        private string _gone;

        private protected PipeEnd(int handle) => _handle = handle;

        /// <summary>
        /// Run when what this end becomes is closed (step197): an end of a
        /// pipeline started from code waits for its stages there.
        /// </summary>
        internal Action Closed;

        /// <summary>Whether the end can still be opened or handed over.</summary>
        public bool IsAvailable => _handle != 0;

        /// <summary>The handle, the end spent: it is opened, or about to be handed over.</summary>
        internal int Take(string because)
        {
            if (_handle == 0) throw new InvalidOperationException(_gone ?? "the pipe end is closed");
            int handle = _handle;
            _handle = 0;
            _gone = because;
            return handle;
        }

        /// <summary>The handle, the end still this one's.</summary>
        internal int Peek()
        {
            if (_handle == 0) throw new InvalidOperationException(_gone ?? "the pipe end is closed");
            return _handle;
        }

        /// <summary>A start that failed gives the end back.</summary>
        internal void Restore(int handle)
        {
            _handle = handle;
            _gone = null;
        }

        /// <summary>Closes an end never opened nor handed over.</summary>
        public void Dispose()
        {
            if (_handle == 0) return;
            PipeTransport.Close(_handle);
            _handle = 0;
            _gone = "the pipe end is closed";
        }
    }

    /// <summary>The writing end of a pair: open it with <see cref="Write{T}"/>, or hand it to a process.</summary>
    public sealed partial class PipeWriteEnd : PipeEnd
    {
        internal PipeWriteEnd(int handle) : base(handle) { }

        /// <summary>
        /// Another end on the same pipe, not yet opened (step212): both write
        /// it, one type between them, and the reader's stream ends when the
        /// last of them closes. `2>&amp;1` hands a stage such a copy.
        /// </summary>
        public PipeWriteEnd Duplicate()
        {
            int handle = Peek();
            PipeStatus status = PipeTransport.Duplicate(handle, out int copy);
            if (status != PipeStatus.Ok) throw new PipeException(status, "a copy of the pipe end: " + Pipe.Explain(status));
            return new PipeWriteEnd(copy);
        }

        /// <summary>A writer of <typeparamref name="T"/> on this end; the type is checked against the reader's, if it declared one.</summary>
        public PipeWriter<T> Write<T>() where T : class
        {
            PipeWriter<T> writer = Pipe.OpenWriter<T>(Take("the pipe end is already open"), "the pipe");
            writer.Closed = Closed;
            Closed = null;
            return writer;
        }
    }

    /// <summary>The reading end of a pair: open it with <see cref="Read"/> or <see cref="Read{T}"/>, or hand it to a process.</summary>
    public sealed partial class PipeReadEnd : PipeEnd
    {
        internal PipeReadEnd(int handle) : base(handle) { }

        /// <summary>A reader of <typeparamref name="T"/> on this end; the type is checked against the writer's.</summary>
        public PipeReader<T> Read<T>() where T : class
        {
            PipeReader<T> reader = Pipe.OpenReader<T>(Take("the pipe end is already open"), "the pipe");
            reader.Closed = Closed;
            Closed = null;
            return reader;
        }

        /// <summary>A reader of whatever the writer sends, read through views.</summary>
        public RawPipeReader Read()
        {
            var reader = new RawPipeReader(Take("the pipe end is already open"));
            reader.Closed = Closed;
            Closed = null;
            return reader;
        }
    }

    public static partial class Pipe
    {
        /// <summary>
        /// Messages a pipe made by <see cref="Create"/> holds unless told
        /// otherwise: 256 (step208). One CPU switches between writer and reader
        /// each time the queue fills, and a process switch measured ~27 µs: at
        /// 16 a small message between two processes cost 3.7 µs, at 256 0.53, at
        /// 4096 0.30 (0.26 within one process). Bytes stay bounded by the
        /// kernel's 4 MiB a queue; an app writer at the limit waits.
        /// </summary>
        public const uint DefaultCapacity = 256;

        /// <summary>
        /// The same for a pipe connected by name: 16, as before. There the room
        /// is part of the meaning: a writer that closes before any reader came
        /// loses what it wrote, and one with more to write than fits waits for
        /// the reader instead (PROCTEST 3) - at 256, forty messages fitted, the
        /// writer closed, and the reader waited for good.
        /// </summary>
        public const uint NamedCapacity = 16;

        /// <summary>A new pipe without a type: its write end and read end (pipe spec Р27).</summary>
        public static PipePair Create(uint capacity = Pipe.DefaultCapacity, PipeOverflow overflow = PipeOverflow.DropOldest)
        {
            PipeStatus status = PipeTransport.Create(capacity, overflow, out int writer, out int reader);
            if (status != PipeStatus.Ok) throw new PipeException(status, "a new pipe: " + Explain(status));
            return new PipePair(writer, reader);
        }

        /// <summary>
        /// A writing end whose messages go nowhere (step212): what `n>&amp;-`
        /// hands a program. Writes succeed; nobody reads.
        /// </summary>
        public static PipeWriteEnd Nowhere()
        {
            PipeStatus status = PipeTransport.Create(1, PipeOverflow.Discard, out int writer, out int reader);
            if (status != PipeStatus.Ok) throw new PipeException(status, "a pipe to nowhere: " + Explain(status));
            PipeTransport.Close(reader);
            return new PipeWriteEnd(writer);
        }

        // ---- the standard ends ----

        private static bool s_inputOpened;
        private static bool s_outputOpened;

        /// <summary>The standard input as a reader of <typeparamref name="T"/>; an empty stream when none was handed over.</summary>
        public static PipeReader<T> Read<T>() where T : class
            => OpenReader<T>(TakeInput(), "the standard input");

        /// <summary>The standard input read through views; an empty stream when none was handed over.</summary>
        public static RawPipeReader Read() => new RawPipeReader(TakeInput());

        /// <summary>
        /// The standard output as a writer of <typeparamref name="T"/>. When no
        /// output was handed over, every message is printed on the screen instead.
        /// </summary>
        public static PipeWriter<T> Write<T>() where T : class
        {
            int handle = TakeOutput(OutputName<T>());
            return handle != 0 ? OpenWriter<T>(handle, "the standard output") : PipeWriter<T>.ToScreen();
        }

        /// <summary>
        /// The standard output as System.Console's text (step212): a pipe of
        /// strings, a message a line; with no output handed over, the screen,
        /// written through as it comes so a prompt shows before its input.
        /// One type a pipe: once this is open, Write&lt;T&gt;() is refused, and
        /// the other way round.
        /// </summary>
        public static TextWriter ConsoleText()
        {
            int handle = TakeOutput("text (System.Console)");
            return handle != 0
                ? new PipeTextWriter(OpenWriter<string>(handle, "the standard output"))
                : new TerminalTextWriter();
        }

        /// <summary>The end number stdtext is handed under (step212), as `3>` names it.</summary>
        public const int TextEnd = 3;

        /// <summary>The end number the error stream is handed under, as `2>` names it.</summary>
        public const int ErrorEnd = 2;

        private static TextWriter s_text;

        /// <summary>
        /// stdtext (step212): text for whoever reads the screen, beside the
        /// data on the standard output — a panel, a report, a summary. A pipe
        /// of strings when an end was handed under <see cref="TextEnd"/>
        /// (`3> file`); the terminal otherwise.
        /// </summary>
        public static TextWriter Text => s_text ??= HandedText(TextEnd, "stdtext") ?? new TerminalTextWriter();

        /// <summary>stdtext's unfinished line, sent at the program's end (AppRuntime.Finish).</summary>
        internal static void FlushText() => s_text?.Flush();

        /// <summary>
        /// The error stream as a pipe of strings when an end was handed under
        /// <see cref="ErrorEnd"/> (`2> file`); null when none was, and the
        /// error stream is the kernel's.
        /// </summary>
        public static TextWriter ErrorText() => HandedText(ErrorEnd, "the error stream");

        private static TextWriter HandedText(int number, string what)
        {
            int handle = PipeTransport.StandardEnd((uint)number);
            return handle == 0 ? null : new PipeTextWriter(OpenWriter<string>(handle, what));
        }

        // What the standard output is a pipe of, for the refusal to open it twice.
        private static string OutputName<T>()
        {
            ulong key = MessageCatalog.KeyOf(typeof(T));
            SharpOS.Std.Exchange.TypeKeys.Description d = key == 0 ? null : SharpOS.Std.Exchange.TypeKeys.DescriptionOf(key);
            return d != null ? d.Name : "a typed writer";
        }

        /// <summary>
        /// The standard input as an end to hand to a process (a shell passing
        /// its own input to a pipeline's first stage, step197); null when none
        /// was handed over. Spends the standard input like Read does.
        /// </summary>
        public static PipeReadEnd InputEnd()
        {
            if (s_inputOpened) throw new InvalidOperationException("the standard input is already open");
            s_inputOpened = true;
            int handle = PipeTransport.StandardEnd(0);
            return handle == 0 ? null : new PipeReadEnd(handle);
        }

        /// <summary>The standard output as an end to hand to a process; null when there is none (the screen).</summary>
        public static PipeWriteEnd OutputEnd()
        {
            int handle = TakeOutput("an end handed over");
            return handle == 0 ? null : new PipeWriteEnd(handle);
        }

        private static string s_outputAs;

        /// <summary>
        /// The standard output's handle, or 0 for the screen. Opened once, as
        /// one thing: a pipe has one type (step212).
        /// </summary>
        internal static int TakeOutput(string asWhat)
        {
            if (s_outputOpened)
                throw new InvalidOperationException("the standard output is already open as " + s_outputAs
                    + ": a pipe has one type; text beside data goes to Pipe.Text or Console.Error");
            s_outputOpened = true;
            s_outputAs = asWhat;
            return PipeTransport.StandardEnd(1);
        }

        // The standard input's handle; without one, the read end of a pipe
        // whose writer is closed already: a stream that ends at once.
        private static int TakeInput()
        {
            if (s_inputOpened) throw new InvalidOperationException("the standard input is already open");
            s_inputOpened = true;
            int handle = PipeTransport.StandardEnd(0);
            if (handle != 0) return handle;
            PipeStatus status = PipeTransport.Create(1, PipeOverflow.DropOldest, out int writer, out int reader);
            if (status != PipeStatus.Ok) throw new PipeException(status, "the standard input: " + Explain(status));
            PipeTransport.Close(writer);
            return reader;
        }

        internal static PipeReader<T> OpenReader<T>(int handle, string what) where T : class
        {
            ulong key = MessageCatalog.KeyOf(typeof(T));
            if (key == 0) throw Refused(handle, PipeStatus.Refused, what, "the type is not in the catalog");
            PipeStatus status = PipeTransport.OpenEnd(handle, MessageCatalog.SchemaOf(key), key, out string error);
            if (status != PipeStatus.Ok) throw Refused(handle, status, what, error);
            return new PipeReader<T>(handle, key);
        }

        internal static PipeWriter<T> OpenWriter<T>(int handle, string what) where T : class
        {
            ulong key = MessageCatalog.KeyOf(typeof(T));
            if (key == 0) throw Refused(handle, PipeStatus.Refused, what, "the type is not in the catalog");
            PipeStatus status = PipeTransport.OpenEnd(handle, MessageCatalog.WriterSchema(key), key, out string error);
            if (status != PipeStatus.Ok) throw Refused(handle, status, what, error);
            return new PipeWriter<T>(handle, key) { Throws = true };
        }

        // An end that could not be opened is closed: it was opened once, and
        // nothing else holds its handle. A writer gone this way breaks the
        // stream; the queue of a reader gone this way goes back.
        private static PipeException Refused(int handle, PipeStatus status, string what, string error)
        {
            PipeTransport.Close(handle);
            return new PipeException(status, what + ": " + (error ?? Explain(status)));
        }
    }
}
