// System.IO.MemoryStream — minimal port from dotnet/runtime v8.0.27
//   src/libraries/System.Private.CoreLib/src/System/IO/MemoryStream.cs (MIT)
// Read-only view over a caller-supplied byte[] — the `new MemoryStream(data)`
// shape ManagedDoom's DeHackEd/lump readers use.
//
// Cuts vs original: expandable (capacity-growing) mode and the ctor()/
// ctor(int) family, writable ctors, GetBuffer/TryGetBuffer/ToArray,
// publiclyVisible, async surface. Field names kept (_buffer/_position/
// _length/_isOpen).

namespace System.IO
{
    public class MemoryStream : Stream
    {
        private readonly byte[] _buffer;
        private int _position;
        private readonly int _length;
        private bool _isOpen;

        public MemoryStream(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            _buffer = buffer;
            _length = buffer.Length;
            _isOpen = true;
        }

        public override bool CanRead => _isOpen;
        public override bool CanSeek => _isOpen;
        public override bool CanWrite => false;

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
            => throw new NotSupportedException("Stream does not support writing.");

        public override void SetLength(long value)
            => throw new NotSupportedException("Stream does not support writing.");

        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            _isOpen = false;
        }
    }
}
