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
        internal Region<T> Next()
        {
            Region<T> region = Receive();
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

            public bool MoveNext()
            {
                _region?.Dispose();
                _region = null;
                while (true)
                {
                    Region<T> next = _reader.Next();
                    if (next == null) return false;
                    if (_filter == null || _filter(next.Root))
                    {
                        _region = next;
                        return true;
                    }
                    next.Dispose();
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
                Region<T> region;
                while ((region = _reader.Next()) != null)
                {
                    if (_filter != null && !_filter(region.Root))
                    {
                        region.Dispose();
                        continue;
                    }
                    PipeStatus sent = output.Move(region);
                    if (sent != PipeStatus.Ok)
                    {
                        region.Dispose();
                        throw new PipeException(sent, "pipe '" + name + "': " + (output.LastError ?? Pipe.Explain(sent)));
                    }
                }
            }
            finally
            {
                output?.Dispose();
                _reader.Dispose();
            }
        }
    }

    public sealed unsafe partial class RawPipeReader
    {
        /// <summary>The next region for a loop: null at the end; a broken writer and a refused message throw.</summary>
        internal RawRegion Next()
        {
            RawRegion region = Receive();
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

            public bool MoveNext()
            {
                _region?.Dispose();
                _region = null;
                while (true)
                {
                    RawRegion next = _reader.Next();
                    if (next == null) return false;
                    if (_filter == null || _filter(next.Root))
                    {
                        _region = next;
                        return true;
                    }
                    next.Dispose();
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

            // The copy is made and the region let go at once: the objects are this image's own.
            public bool MoveNext()
            {
                RawRegion region = _reader.Next();
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
        public void WriteTo(string name)
        {
            int output = 0;
            try
            {
                RawRegion region;
                while ((region = _reader.Next()) != null)
                {
                    if (_filter != null && !_filter(region.Root))
                    {
                        region.Dispose();
                        continue;
                    }
                    if (output == 0)
                    {
                        PipeStatus connected = PipeTransport.Connect(name, PipeRole.Writer, 16, PipeOverflow.DropOldest,
                                                                     _reader.Schema, _reader.RootKey, out output, out string error);
                        if (connected != PipeStatus.Ok)
                        {
                            region.Dispose();
                            throw Pipe.Failed(name, connected, error);
                        }
                    }
                    PipeStatus sent = PipeTransport.Send(output, region.Block, region.Length);
                    if (sent != PipeStatus.Ok)
                    {
                        region.Dispose();
                        throw new PipeException(sent, "pipe '" + name + "': " + Pipe.Explain(sent));
                    }
                    region.End("the region went on to pipe '" + name + "'");
                }
            }
            finally
            {
                if (output != 0) PipeTransport.Close(output);
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
