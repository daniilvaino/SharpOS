using System;
using System.IO;
using SharpOS.Std.Exchange;

namespace SharpOS.Std.Pipes
{
    // Bytes over a pipe (step197). The pipe still carries objects: bytes go as
    // byte[] messages, text as string messages. A byte reader takes both — a
    // string is its UTF-8 and a line feed — and does not show where one message
    // ends and the next begins; a byte writer cuts what it is given into
    // messages of up to 64 KiB, and Flush sends what has gathered.

    /// <summary>Reads the bytes of byte[] and string messages, one after another, as a stream.</summary>
    internal sealed unsafe class PipeByteReader : Stream
    {
        private readonly RawPipeReader _reader;
        private RawRegion _region;      // the message being read; reused for the next
        private byte* _data;            // its bytes: a byte[]'s in place, a string's encoded into _text
        private int _length;
        private int _position;
        private byte[] _text = new byte[256];
        private bool _ended;

        internal PipeByteReader(RawPipeReader reader) => _reader = reader;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("a pipe has no length");
        public override long Position
        {
            get => throw new NotSupportedException("a pipe does not seek");
            set => throw new NotSupportedException("a pipe does not seek");
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(new Span<byte>(buffer, offset, count));

        public override int Read(Span<byte> buffer)
        {
            int copied = 0;
            while (copied < buffer.Length)
            {
                if (_position == _length)
                {
                    if (copied > 0 || !NextMessage()) break;
                    continue;
                }
                int n = _length - _position;
                if (n > buffer.Length - copied) n = buffer.Length - copied;
                new ReadOnlySpan<byte>(_data + _position, n).CopyTo(buffer.Slice(copied));
                _position += n;
                copied += n;
            }
            return copied;
        }

        public override int ReadByte()
        {
            byte b;
            return Read(new Span<byte>(&b, 1)) == 1 ? b : -1;
        }

        // The next message's bytes; false at the end of the stream. A message
        // of any other type is an error naming it.
        private bool NextMessage()
        {
            if (_ended) return false;
            while (true)
            {
                // The message read so far goes back first; its wrapper takes the next.
                RawRegion reuse = _region;
                reuse?.Dispose();
                _region = null;
                _region = _reader.Next(reuse);
                if (_region == null)
                {
                    _ended = true;
                    _data = null;
                    _length = _position = 0;
                    return false;
                }
                View root = _region.Root;
                if (root.TryGetBytes(out byte* bytes, out int count))
                {
                    _data = bytes;
                    _length = count;
                }
                else if (root.TryGetChars(out char* chars, out int length))
                {
                    int max = length * 3 + 1;
                    if (_text.Length < max) _text = new byte[max];
                    int n;
                    fixed (byte* t = _text)
                    {
                        n = System.Text.Encoding.UTF8.GetBytes(new ReadOnlySpan<char>(chars, length), new Span<byte>(t, max));
                        t[n] = (byte)'\n';
                        _data = t;   // _text is pinned by the non-moving collector
                    }
                    _length = n + 1;
                }
                else if (root.IsNull)
                {
                    continue;
                }
                else
                {
                    throw new InvalidDataException("a byte stream received a message of " + root.TypeName
                                                   + ": only System.Byte[] and System.String carry bytes");
                }
                _position = 0;
                if (_length > 0) return true;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("a pipe does not seek");
        public override void SetLength(long value) => throw new NotSupportedException("a pipe does not seek");
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("a reading stream");
        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            _region?.Dispose();
            _region = null;
            _reader.Dispose();
        }
    }

    /// <summary>Writes bytes as byte[] messages of up to 64 KiB; Flush sends what has gathered.</summary>
    internal sealed class PipeByteWriter : Stream
    {
        internal const int ChunkBytes = 64 * 1024;

        private readonly PipeWriter<byte[]> _writer;
        private readonly byte[] _chunk = new byte[ChunkBytes];
        private int _fill;
        private bool _disposed;

        internal PipeByteWriter(PipeWriter<byte[]> writer) => _writer = writer;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException("a pipe has no length");
        public override long Position
        {
            get => throw new NotSupportedException("a pipe does not seek");
            set => throw new NotSupportedException("a pipe does not seek");
        }

        public override void Write(byte[] buffer, int offset, int count)
            => Write(new ReadOnlySpan<byte>(buffer, offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_disposed) throw new ObjectDisposedException("PipeByteWriter");
            while (buffer.Length > 0)
            {
                int n = ChunkBytes - _fill;
                if (n > buffer.Length) n = buffer.Length;
                buffer.Slice(0, n).CopyTo(new Span<byte>(_chunk, _fill, n));
                _fill += n;
                buffer = buffer.Slice(n);
                if (_fill == ChunkBytes)
                {
                    _writer.Copy(_chunk);
                    _fill = 0;
                }
            }
        }

        public override void WriteByte(byte value)
        {
            _chunk[_fill++] = value;
            if (_fill == ChunkBytes)
            {
                _writer.Copy(_chunk);
                _fill = 0;
            }
        }

        /// <summary>Sends what has gathered as one message.</summary>
        public override void Flush()
        {
            if (_fill == 0 || _disposed) return;
            var part = new byte[_fill];
            Array.Copy(_chunk, part, _fill);
            _fill = 0;
            _writer.Copy(part);
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            try { Flush(); }
            finally
            {
                _disposed = true;
                _writer.Dispose();
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("a writing stream");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("a pipe does not seek");
        public override void SetLength(long value) => throw new NotSupportedException("a pipe does not seek");
    }

    /// <summary>
    /// Text as string messages, a line each (step197): WriteLine sends its
    /// line, Write gathers until a line feed, Flush sends what is gathered.
    /// </summary>
    internal sealed class PipeTextWriter : TextWriter
    {
        private readonly PipeWriter<string> _writer;
        private readonly System.Text.StringBuilder _line = new System.Text.StringBuilder();
        private bool _disposed;

        internal PipeTextWriter(PipeWriter<string> writer) => _writer = writer;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                SendLine();
                return;
            }
            _line.Append(value);
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\n') continue;
                _line.Append(value, start, i - start);
                SendLine();
                start = i + 1;
            }
            _line.Append(value, start, value.Length - start);
        }

        public override void WriteLine(string? value)
        {
            if (_line.Length == 0 && value != null && value.IndexOf('\n') < 0)
            {
                _writer.Copy(StripReturn(value));
                return;
            }
            Write(value);
            SendLine();
        }

        public override void WriteLine() => SendLine();

        private void SendLine()
        {
            string line = StripReturn(_line.ToString());
            _line.Clear();
            _writer.Copy(line);
        }

        // A CRLF line ends at its LF; the CR is not part of the text.
        private static string StripReturn(string s)
            => s.Length > 0 && s[s.Length - 1] == '\r' ? s.Substring(0, s.Length - 1) : s;

        /// <summary>What is gathered goes as a message of its own.</summary>
        public override void Flush()
        {
            if (_line.Length > 0) SendLine();
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            try { Flush(); }
            finally
            {
                _disposed = true;
                _writer.Dispose();
            }
        }
    }

    public static partial class Pipe
    {
        /// <summary>The standard input as text: string messages a line each, byte[] ones decoded as UTF-8.</summary>
        public static TextReader ReadText() => new StreamReader(ReadBytes());

        /// <summary>The standard output as text: a string message per line.</summary>
        public static TextWriter WriteText() => new PipeTextWriter(Write<string>());

        /// <summary>The standard input as bytes: byte[] messages as they are, string ones as UTF-8 lines.</summary>
        public static Stream ReadBytes() => new PipeByteReader(Read());

        /// <summary>The standard output as bytes: byte[] messages of up to 64 KiB (on the screen as text when it is text).</summary>
        public static Stream WriteBytes() => new PipeByteWriter(Write<byte[]>());
    }

    public sealed partial class PipeReadEnd
    {
        /// <summary>This end as bytes, as <see cref="Pipe.ReadBytes"/>.</summary>
        public Stream ReadBytes() => new PipeByteReader(Read());

        /// <summary>This end as text, as <see cref="Pipe.ReadText"/>.</summary>
        public TextReader ReadText() => new StreamReader(ReadBytes());
    }

    public sealed partial class PipeWriteEnd
    {
        /// <summary>This end as bytes, as <see cref="Pipe.WriteBytes"/>.</summary>
        public Stream WriteBytes() => new PipeByteWriter(Write<byte[]>());

        /// <summary>This end as text, as <see cref="Pipe.WriteText"/>.</summary>
        public TextWriter WriteText() => new PipeTextWriter(Write<string>());
    }
}
