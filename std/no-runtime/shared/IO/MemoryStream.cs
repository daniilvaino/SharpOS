// System.IO.MemoryStream — minimal port from dotnet/runtime v8.0.27
//   src/libraries/System.Private.CoreLib/src/System/IO/MemoryStream.cs (MIT)
//
// Two shapes, both BCL-faithful:
//   new MemoryStream(data[, writable])  — view over a caller's array
//   new MemoryStream()                  — expandable, owns its buffer
//
// Cuts vs original: capacity ctor family, publiclyVisible distinction (our
// GetBuffer always returns the array), async surface, WriteTo/CopyTo.
// Field names kept (_buffer/_position/_length/_isOpen).

namespace System.IO
{
    public class MemoryStream : Stream
    {
        private byte[] _buffer;
        private int _position;
        private int _length;
        private bool _isOpen;
        private readonly bool _writable;
        private readonly bool _expandable;

        public MemoryStream(byte[] buffer) : this(buffer, false) { }

        public MemoryStream(byte[] buffer, bool writable)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            _buffer = buffer;
            _length = buffer.Length;
            _isOpen = true;
            _writable = writable;
            _expandable = false;
        }

        public MemoryStream()
        {
            _buffer = new byte[256];
            _length = 0;
            _isOpen = true;
            _writable = true;
            _expandable = true;
        }

        public override bool CanRead => _isOpen;
        public override bool CanSeek => _isOpen;
        public override bool CanWrite => _isOpen && _writable;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
                _position = (int)value;
            }
        }

        /// <summary>The backing array itself, length included — callers read
        /// only the first <see cref="Length"/> bytes of it.</summary>
        public virtual byte[] GetBuffer() => _buffer;

        public virtual byte[] ToArray()
        {
            byte[] copy = new byte[_length];
            Array.Copy(_buffer, 0, copy, 0, _length);
            return copy;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_isOpen) throw new InvalidOperationException("Stream is closed.");
            int n = _length - _position;
            if (n > count) n = count;
            if (n <= 0) return 0;

            Array.Copy(_buffer, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!_isOpen) throw new InvalidOperationException("Stream is closed.");
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentException("Invalid seek origin."),
            };
            if (target < 0 || target > int.MaxValue) throw new IOException("Seek out of range.");
            _position = (int)target;
            return target;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_isOpen) throw new InvalidOperationException("Stream is closed.");
            if (!_writable) throw new NotSupportedException("Stream does not support writing.");
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || buffer.Length - offset < count)
                throw new ArgumentOutOfRangeException(nameof(count));

            EnsureCapacity(_position + count);
            Array.Copy(buffer, offset, _buffer, _position, count);
            _position += count;
            if (_position > _length) _length = _position;
        }

        public override void SetLength(long value)
        {
            if (!_writable) throw new NotSupportedException("Stream does not support writing.");
            if (value < 0 || value > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));

            EnsureCapacity((int)value);
            _length = (int)value;
            if (_position > _length) _position = _length;
        }

        // Doubling, not exact fit: a stream written a few bytes at a time would
        // otherwise reallocate and copy on every single Write.
        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length) return;
            if (!_expandable) throw new NotSupportedException("Stream is not expandable.");

            int capacity = _buffer.Length == 0 ? 256 : _buffer.Length;
            while (capacity < required) capacity *= 2;

            byte[] grown = new byte[capacity];
            Array.Copy(_buffer, 0, grown, 0, _length);
            _buffer = grown;
        }

        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            _isOpen = false;
        }
    }
}
