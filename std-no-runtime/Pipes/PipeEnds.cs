using System;

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
    // No input is an empty stream; no output is the screen.

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
        /// <summary>A new pipe without a type: its write end and read end (pipe spec Р27).</summary>
        public static PipePair Create(uint capacity = 16, PipeOverflow overflow = PipeOverflow.DropOldest)
        {
            PipeStatus status = PipeTransport.Create(capacity, overflow, out int writer, out int reader);
            if (status != PipeStatus.Ok) throw new PipeException(status, "a new pipe: " + Explain(status));
            return new PipePair(writer, reader);
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
            int handle = TakeOutput();
            return handle != 0 ? OpenWriter<T>(handle, "the standard output") : PipeWriter<T>.ToScreen();
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
            int handle = TakeOutput();
            return handle == 0 ? null : new PipeWriteEnd(handle);
        }

        /// <summary>The standard output's handle, or 0 for the screen. Opened once.</summary>
        internal static int TakeOutput()
        {
            if (s_outputOpened) throw new InvalidOperationException("the standard output is already open");
            s_outputOpened = true;
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
