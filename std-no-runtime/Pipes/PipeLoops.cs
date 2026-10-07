using System;
using System.Runtime.CompilerServices;
using SharpOS.Std.Exchange;

namespace SharpOS.Std.Pipes
{
    // The convenient layer over pipe ends: a reader is a loop, errors are
    // exceptions, the region of a step is let go by the loop itself. The low
    // layer (Connect, Receive, Region, RawRegion, PipeStatus) stays as it was.

    public static partial class Pipe
    {
        /// <summary>A reader of <typeparamref name="T"/> on the pipe called <paramref name="name"/>; PipeException when it cannot connect.</summary>
        public static PipeReader<T> Read<T>(string name) where T : class
        {
            PipeStatus status = PipeReader<T>.Connect(name, out PipeReader<T> reader, out string error);
            if (status != PipeStatus.Ok) throw Failed(name, status, error);
            return reader;
        }

        /// <summary>A reader of whatever the pipe carries, read through views.</summary>
        public static RawPipeReader Read(string name)
        {
            PipeStatus status = RawPipeReader.Connect(name, out RawPipeReader reader, out string error);
            if (status != PipeStatus.Ok) throw Failed(name, status, error);
            return reader;
        }

        /// <summary>A writer of <typeparamref name="T"/> on the pipe called <paramref name="name"/>.</summary>
        public static PipeWriter<T> Write<T>(string name) where T : class
        {
            PipeStatus status = PipeWriter<T>.Connect(name, out PipeWriter<T> writer, out string error);
            if (status != PipeStatus.Ok) throw Failed(name, status, error);
            writer.Throws = true;
            return writer;
        }

        internal static PipeException Failed(string name, PipeStatus status, string error)
            => new PipeException(status, "pipe '" + name + "': " + (error ?? Explain(status)));

        internal static string Explain(PipeStatus status)
        {
            switch (status)
            {
                case PipeStatus.Broken: return "the other end is gone without closing the pipe";
                case PipeStatus.TypeMismatch: return "the two ends declared different types";
                case PipeStatus.NameTaken: return "an end of that role is already waiting on the name";
                case PipeStatus.NoMemory: return "no memory";
                case PipeStatus.Unsupported: return "the kernel offers no pipes";
                case PipeStatus.Refused: return "a message could not be taken";
                default: return status.ToString();
            }
        }
    }

    public sealed unsafe partial class PipeReader<T>
    {
        /// <summary>
        /// The next region for a loop: null at the end of the stream; the break
        /// of the writer and a refused message throw.
        /// </summary>
        internal Region<T> Next(Region<T> reuse = null)
        {
            Region<T> region = Receive(reuse);
            if (region != null || Status == PipeStatus.EndOfStream) return region;
            throw new PipeException(Status, Status == PipeStatus.Refused ? "a message could not be taken: " + LastError
                                                                         : Pipe.Explain(Status));
        }

        /// <summary>
        /// The messages, read in place. Each one lives until the next step; leaving
        /// the loop — by its end, break or an exception — closes this reader.
        /// </summary>
        public Enumerator GetEnumerator() => new Enumerator(this, null);

        /// <summary>The regions themselves: Move or ToHeap on a step; one not moved is let go at the step's end.</summary>
        public RegionLoop Regions => new RegionLoop(this);

        /// <summary>Only the messages <paramref name="predicate"/> keeps.</summary>
        public PipeQuery<T> Where(Func<T, bool> predicate) => new PipeQuery<T>(this, predicate);

        /// <summary>Every message on to the pipe called <paramref name="name"/>, each block as it is.</summary>
        public void WriteTo(string name) => new PipeQuery<T>(this, null).WriteTo(name);

        /// <summary>Every message on to the standard output (the screen when there is none).</summary>
        public void WriteTo() => new PipeQuery<T>(this, null).WriteTo();

        public struct Enumerator : IDisposable
        {
            private readonly PipeReader<T> _reader;
            private readonly Func<T, bool> _filter;
            private Region<T> _region;

            internal Enumerator(PipeReader<T> reader, Func<T, bool> filter)
            {
                _reader = reader;
                _filter = filter;
                _region = null;
            }

            public T Current => _region.Root;

            // One wrapper for the whole loop: outside it only the root is
            // seen, so nothing can hold the wrapper past its step (step195).
            private Region<T> _spare;

            public bool MoveNext()
            {
                _region?.Dispose();
                Region<T> reuse = _region ?? _spare;
                _region = null;
                while (true)
                {
                    Region<T> next = _reader.Next(reuse);
                    if (next == null) return false;
                    if (_filter == null || _filter(next.Root))
                    {
                        _region = next;
                        return true;
                    }
                    next.Dispose();
                    reuse = next;
                    _spare = next;
                }
            }

            public void Dispose()
            {
                _region?.Dispose();
                _region = null;
                _reader.Dispose();
            }
        }

        public readonly struct RegionLoop
        {
            private readonly PipeReader<T> _reader;

            internal RegionLoop(PipeReader<T> reader) => _reader = reader;

            public RegionEnumerator GetEnumerator() => new RegionEnumerator(_reader);
        }

        public struct RegionEnumerator : IDisposable
        {
            private readonly PipeReader<T> _reader;
            private Region<T> _region;

            internal RegionEnumerator(PipeReader<T> reader)
            {
                _reader = reader;
                _region = null;
            }

            public Region<T> Current => _region;

            // A region given on with Move is already gone: Dispose does nothing to it.
            public bool MoveNext()
            {
                _region?.Dispose();
                _region = _reader.Next();
                return _region != null;
            }

            public void Dispose()
            {
                _region?.Dispose();
                _region = null;
                _reader.Dispose();
            }
        }
    }

    /// <summary>A reader with a condition: a loop, more conditions, or a pipe to send what passes to.</summary>
    public sealed class PipeQuery<T> where T : class
    {
        private readonly PipeReader<T> _reader;
        private readonly Func<T, bool> _filter;

        internal PipeQuery(PipeReader<T> reader, Func<T, bool> filter)
        {
            _reader = reader;
            _filter = filter;
        }

        public PipeQuery<T> Where(Func<T, bool> predicate)
        {
            Func<T, bool> first = _filter;
            return new PipeQuery<T>(_reader, first == null ? predicate : x => first(x) && predicate(x));
        }

        public PipeReader<T>.Enumerator GetEnumerator() => new PipeReader<T>.Enumerator(_reader, _filter);

        /// <summary>
        /// What passes goes on to the pipe called <paramref name="name"/> as it
        /// is — the block itself, no copy; what does not is let go. The output
        /// closes when the input ends; a broken input throws.
        /// </summary>
        public void WriteTo(string name)
        {
            PipeWriter<T> output = null;
            try
            {
                output = Pipe.Write<T>(name);
                Pump(output, "pipe '" + name + "'");
            }
            finally
            {
                output?.Dispose();
                _reader.Dispose();
            }
        }

        /// <summary>What passes goes on to the standard output — the screen when there is none.</summary>
        public void WriteTo()
        {
            PipeWriter<T> output = null;
            try
            {
                output = Pipe.Write<T>();
                Pump(output, "the standard output");
            }
            finally
            {
                output?.Dispose();
                _reader.Dispose();
            }
        }

        private void Pump(PipeWriter<T> output, string what)
        {
            // One wrapper, reused: each region is moved on or let go before the next.
            Region<T> region = null;
            while ((region = _reader.Next(region)) != null)
            {
                if (_filter != null && !_filter(region.Root))
                {
                    region.Dispose();
                    continue;
                }
                try
                {
                    output.Move(region);
                }
                catch (PipeException e)
                {
                    region.Dispose();
                    throw new PipeException(e.Status, what + ": " + e.Message);
                }
            }
        }
    }

    public sealed unsafe partial class RawPipeReader
    {
        /// <summary>The next region for a loop: null at the end; a broken writer and a refused message throw.</summary>
        internal RawRegion Next(RawRegion reuse = null)
        {
            RawRegion region = Receive(reuse);
            if (region != null || Status == PipeStatus.EndOfStream) return region;
            throw new PipeException(Status, Status == PipeStatus.Refused ? LastError : Pipe.Explain(Status));
        }

        /// <summary>The messages as views; each lives until the next step. Leaving the loop closes this reader.</summary>
        public Enumerator GetEnumerator() => new Enumerator(this, null);

        /// <summary>Each message laid out by field name into <typeparamref name="T"/>, in this image's heap.</summary>
        public IntoLoop<T> Into<T>() where T : class => new IntoLoop<T>(this);

        public RawQuery Where(Func<View, bool> predicate) => new RawQuery(this, predicate);

        /// <summary>Every message on to the pipe called <paramref name="name"/>, untranslated: no copy, no translation.</summary>
        public void WriteTo(string name) => new RawQuery(this, null).WriteTo(name);

        /// <summary>Every message on to the standard output, untranslated (the screen when there is none).</summary>
        public void WriteTo() => new RawQuery(this, null).WriteTo();

        public struct Enumerator : IDisposable
        {
            private readonly RawPipeReader _reader;
            private readonly Func<View, bool> _filter;
            private RawRegion _region;

            internal Enumerator(RawPipeReader reader, Func<View, bool> filter)
            {
                _reader = reader;
                _filter = filter;
                _region = null;
            }

            public View Current => _region.Root;

            // One wrapper and one scope for the whole loop; a view of an
            // earlier step refuses by the scope's generation (step195).
            private RawRegion _spare;

            public bool MoveNext()
            {
                _region?.Dispose();
                RawRegion reuse = _region ?? _spare;
                _region = null;
                while (true)
                {
                    RawRegion next = _reader.Next(reuse);
                    if (next == null) return false;
                    if (_filter == null || _filter(next.Root))
                    {
                        _region = next;
                        return true;
                    }
                    next.Dispose();
                    reuse = next;
                    _spare = next;
                }
            }

            public void Dispose()
            {
                _region?.Dispose();
                _region = null;
                _reader.Dispose();
            }
        }

        public readonly struct IntoLoop<T> where T : class
        {
            private readonly RawPipeReader _reader;

            internal IntoLoop(RawPipeReader reader) => _reader = reader;

            public IntoEnumerator<T> GetEnumerator() => new IntoEnumerator<T>(_reader);
        }

        public struct IntoEnumerator<T> : IDisposable where T : class
        {
            private readonly RawPipeReader _reader;
            private T _current;

            internal IntoEnumerator(RawPipeReader reader)
            {
                _reader = reader;
                _current = null;
            }

            public T Current => _current;

            private RawRegion _spare;

            // The copy is made and the region let go at once: the objects are this image's own.
            public bool MoveNext()
            {
                RawRegion region = _reader.Next(_spare);
                _spare = region;
                if (region == null) return false;
                try
                {
                    _current = region.Root.Into<T>();
                }
                finally
                {
                    region.Dispose();
                }
                return true;
            }

            public void Dispose() => _reader.Dispose();
        }
    }

    /// <summary>A reader of views with a condition.</summary>
    public sealed unsafe class RawQuery
    {
        private readonly RawPipeReader _reader;
        private readonly Func<View, bool> _filter;

        internal RawQuery(RawPipeReader reader, Func<View, bool> filter)
        {
            _reader = reader;
            _filter = filter;
        }

        public RawQuery Where(Func<View, bool> predicate)
        {
            Func<View, bool> first = _filter;
            return new RawQuery(_reader, first == null ? predicate : v => first(v) && predicate(v));
        }

        public RawPipeReader.Enumerator GetEnumerator() => new RawPipeReader.Enumerator(_reader, _filter);

        /// <summary>
        /// What passes goes on untranslated — the block as it came, values
        /// written through views included; the output pipe gets the input's
        /// description and root type. The output closes when the input ends;
        /// a broken input throws.
        /// </summary>
        public void WriteTo(string name) => Pump(name, 0, false);

        /// <summary>
        /// What passes goes on to the standard output untranslated, under the
        /// input's description; with no output handed over, each message is
        /// printed on the screen instead.
        /// </summary>
        public void WriteTo()
        {
            int handle = Pipe.TakeOutput();
            Pump(null, handle, handle == 0);
        }

        // To the pipe called name (connected at the first message, when the
        // input's description is known), to an end already held, or to the screen.
        private void Pump(string name, int held, bool screen)
        {
            string what = name != null ? "pipe '" + name + "'" : "the standard output";
            string movedOn = "the region went on to " + what;
            int output = 0;
            bool declared = false;
            try
            {
                RawRegion region = null;
                while ((region = _reader.Next(region)) != null)
                {
                    if (_filter != null && !_filter(region.Root))
                    {
                        region.Dispose();
                        continue;
                    }
                    if (screen)
                    {
                        string line = region.Root.ToString();
                        region.Dispose();
                        PipeTransport.Print(line);
                        continue;
                    }
                    if (!declared)
                    {
                        PipeStatus opened;
                        string error;
                        if (name != null)
                        {
                            opened = PipeTransport.Connect(name, PipeRole.Writer, 16, PipeOverflow.DropOldest,
                                                           _reader.Schema, _reader.RootKey, out output, out error);
                        }
                        else
                        {
                            output = held;
                            held = 0;
                            opened = PipeTransport.OpenEnd(output, _reader.Schema, _reader.RootKey, out error);
                        }
                        if (opened != PipeStatus.Ok)
                        {
                            region.Dispose();
                            throw new PipeException(opened, what + ": " + (error ?? Pipe.Explain(opened)));
                        }
                        declared = true;
                    }
                    PipeStatus sent = PipeTransport.Send(output, region.Block, region.Length);
                    if (sent != PipeStatus.Ok)
                    {
                        region.Dispose();
                        throw new PipeException(sent, what + ": " + Pipe.Explain(sent));
                    }
                    region.End(movedOn);
                }
            }
            finally
            {
                if (output != 0) PipeTransport.Close(output);
                if (held != 0) PipeTransport.Close(held);
                _reader.Dispose();
            }
        }
    }

    /// <summary>Copies out of a received message.</summary>
    public static unsafe class MessageObjects
    {
        /// <summary>
        /// The graph under <paramref name="value"/> copied into this image's heap
        /// when it lies in a received region — a root, a nested object, a
        /// string, an array — so it outlives the region; anything else as it is.
        /// </summary>
        public static T ToHeap<T>(this T value) where T : class
        {
            if (value == null) return null;
            ulong address = Unsafe.As<T, ulong>(ref value);
            if (!ExchangeArena.Contains(address)) return value;
            return Unsafe.As<T>(Region.ToHeapFrom(address));
        }
    }
}
