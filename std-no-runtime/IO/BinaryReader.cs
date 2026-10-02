// System.IO.BinaryReader — ported from dotnet/runtime release/8.0
//   src/libraries/System.Private.CoreLib/src/System/IO/BinaryReader.cs (MIT)
//
// Cuts vs original (each marked "SharpOS cut:" in place):
//   - Character reads keep the API but not the Decoder (our Encoding has
//     none): a char's byte length comes from the encoding — UTF-8 lead byte,
//     UTF-16 code unit plus a surrogate's partner, one byte otherwise — and
//     Encoding.GetChars decodes it. Read() returns a supplementary character
//     as two calls, high surrogate then low, where the original's decoder
//     throws on the second char.
//   - ReadDecimal, ReadHalf — neither type exists here.
//   - The MemoryStream fast path (_isMemoryStream / InternalReadSpan).
//   - SR resource strings, ThrowHelper — literal messages, plain throws.
//
// ReadString still trusts nothing: the length prefix comes from the data, so
// bytes are taken in bounded chunks and the buffer grows only with what the
// stream actually delivered — a forged length of two gigabytes costs one chunk
// and an EndOfStreamException, not an allocation (pipe_plan.md В11).

using System.Buffers.Binary;
using System.Text;

namespace System.IO
{
    public class BinaryReader : IDisposable
    {
        private const int MaxCharBytesSize = 128;

        private readonly Stream _stream;
        private readonly byte[] _buffer;
        private readonly Encoding _encoding;
        private readonly bool _leaveOpen;
        private bool _disposed;
        private int _pendingLowSurrogate = -1;   // second half of a pair Read() split

        public BinaryReader(Stream input) : this(input, Encoding.UTF8, false)
        {
        }

        public BinaryReader(Stream input, Encoding encoding) : this(input, encoding, false)
        {
        }

        public BinaryReader(Stream input, Encoding encoding, bool leaveOpen)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (encoding == null) throw new ArgumentNullException(nameof(encoding));

            if (!input.CanRead)
            {
                throw new ArgumentException("Stream was not readable.");
            }

            _stream = input;
            _encoding = encoding;
            int minBufferSize = encoding.GetMaxByteCount(1);  // max bytes per one char
            if (minBufferSize < 16)
            {
                minBufferSize = 16;
            }

            _buffer = new byte[minBufferSize];
            _leaveOpen = leaveOpen;
        }

        public virtual Stream BaseStream => _stream;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing && !_leaveOpen)
                {
                    _stream.Close();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        /// <remarks>
        /// Override Dispose(bool) instead of Close(). This API exists for compatibility purposes.
        /// </remarks>
        public virtual void Close()
        {
            Dispose(true);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(null, "Cannot access a closed file.");
            }
        }

        public virtual int PeekChar()
        {
            ThrowIfDisposed();

            if (!_stream.CanSeek)
            {
                return -1;
            }

            if (_pendingLowSurrogate >= 0)
            {
                return _pendingLowSurrogate;
            }

            long origPos = _stream.Position;
            int ch = Read();
            _pendingLowSurrogate = -1;
            _stream.Position = origPos;
            return ch;
        }

        public virtual int Read()
        {
            ThrowIfDisposed();

            if (_pendingLowSurrogate >= 0)
            {
                int low = _pendingLowSurrogate;
                _pendingLowSurrogate = -1;
                return low;
            }

            Span<char> one = stackalloc char[2];
            int n = ReadOneScalar(one);
            if (n == 0)
            {
                return -1;
            }
            if (n == 2)
            {
                _pendingLowSurrogate = one[1];
            }
            return one[0];
        }

        public virtual char ReadChar()
        {
            int value = Read();
            if (value == -1)
            {
                throw new EndOfStreamException("Unable to read beyond the end of the stream.");
            }
            return (char)value;
        }

        public virtual int Read(char[] buffer, int index, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Non-negative number required.");
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Non-negative number required.");
            }
            if (buffer.Length - index < count)
            {
                throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
            }
            ThrowIfDisposed();

            return InternalReadChars(new Span<char>(buffer, index, count));
        }

        public virtual int Read(Span<char> buffer)
        {
            ThrowIfDisposed();
            return InternalReadChars(buffer);
        }

        public virtual char[] ReadChars(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Non-negative number required.");
            }
            ThrowIfDisposed();

            if (count == 0)
            {
                return Array.Empty<char>();
            }

            char[] chars = new char[count];
            int n = InternalReadChars(new Span<char>(chars));
            if (n != count)
            {
                char[] copy = new char[n];
                Array.Copy(chars, copy, n);
                chars = copy;
            }

            return chars;
        }

        private int InternalReadChars(Span<char> buffer)
        {
            int total = 0;
            if (total < buffer.Length && _pendingLowSurrogate >= 0)
            {
                buffer[total++] = (char)_pendingLowSurrogate;
                _pendingLowSurrogate = -1;
            }

            Span<char> scalar = stackalloc char[2];
            while (total < buffer.Length)
            {
                int n = ReadOneScalar(scalar);
                if (n == 0) break;
                buffer[total++] = scalar[0];
                if (n == 2)
                {
                    if (total < buffer.Length) buffer[total++] = scalar[1];
                    else _pendingLowSurrogate = scalar[1];
                }
            }
            return total;
        }

        // One encoded character from the stream, decoded into one or two
        // chars; 0 at the end of the stream. The byte length is the
        // encoding's: what a Decoder would have buffered, decided up front.
        private int ReadOneScalar(Span<char> destination)
        {
            Span<byte> bytes = stackalloc byte[4];
            int first = _stream.ReadByte();
            if (first == -1) return 0;
            bytes[0] = (byte)first;
            int length = 1;

            if (_encoding is UTF8Encoding)
            {
                int need = first < 0x80 ? 1 : first >= 0xF0 ? 4 : first >= 0xE0 ? 3 : first >= 0xC0 ? 2 : 1;
                while (length < need)
                {
                    int b = _stream.ReadByte();
                    if (b == -1) break;
                    bytes[length++] = (byte)b;
                }
            }
            else if (_encoding is UnicodeEncoding)
            {
                int b = _stream.ReadByte();
                if (b == -1) return 0;
                bytes[length++] = (byte)b;
                Span<char> unit = stackalloc char[2];
                if (_encoding.GetChars(bytes.Slice(0, 2), unit) == 1 && char.IsHighSurrogate(unit[0]))
                {
                    for (int k = 0; k < 2; k++)
                    {
                        int c = _stream.ReadByte();
                        if (c == -1) break;
                        bytes[length++] = (byte)c;
                    }
                }
            }

            return _encoding.GetChars(bytes.Slice(0, length), destination);
        }

        public virtual byte ReadByte() => InternalReadByte();

        private byte InternalReadByte()
        {
            ThrowIfDisposed();

            int b = _stream.ReadByte();
            if (b == -1)
            {
                throw new EndOfStreamException("Unable to read beyond the end of the stream.");
            }

            return (byte)b;
        }

        public virtual sbyte ReadSByte() => (sbyte)InternalReadByte();
        public virtual bool ReadBoolean() => InternalReadByte() != 0;

        public virtual short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(InternalRead(2));

        public virtual ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(InternalRead(2));

        public virtual int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(InternalRead(4));
        public virtual uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(InternalRead(4));
        public virtual long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(InternalRead(8));
        public virtual ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(InternalRead(8));
        public virtual float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(InternalRead(4));
        public virtual double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(InternalRead(8));

        public virtual string ReadString()
        {
            ThrowIfDisposed();

            // Length of the string in bytes, not chars
            int stringLength = Read7BitEncodedInt();
            if (stringLength < 0)
            {
                throw new IOException("BinaryReader encountered an invalid string length of " + stringLength + " characters.");
            }

            if (stringLength == 0)
            {
                return string.Empty;
            }

            // SharpOS cut: no Decoder to turn each chunk into chars as it
            // arrives. The bytes are gathered in MaxCharBytesSize chunks —
            // never trusting the prefix for an allocation — and decoded once.
            byte[] bytes = new byte[stringLength < MaxCharBytesSize ? stringLength : MaxCharBytesSize];
            int currPos = 0;
            do
            {
                int readLength = ((stringLength - currPos) > MaxCharBytesSize) ? MaxCharBytesSize : (stringLength - currPos);
                if (currPos + readLength > bytes.Length)
                {
                    int grown = bytes.Length * 2;
                    if (grown < currPos + readLength) grown = currPos + readLength;
                    if (grown > stringLength) grown = stringLength;
                    byte[] larger = new byte[grown];
                    Array.Copy(bytes, larger, currPos);
                    bytes = larger;
                }

                int n = _stream.Read(bytes, currPos, readLength);
                if (n == 0)
                {
                    throw new EndOfStreamException("Unable to read beyond the end of the stream.");
                }
                currPos += n;
            } while (currPos < stringLength);

            return _encoding.GetString(bytes, 0, stringLength);
        }

        public virtual int Read(byte[] buffer, int index, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Non-negative number required.");
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Non-negative number required.");
            }
            if (buffer.Length - index < count)
            {
                throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
            }
            ThrowIfDisposed();

            return _stream.Read(buffer, index, count);
        }

        public virtual int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();
            return _stream.Read(buffer);
        }

        public virtual byte[] ReadBytes(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Non-negative number required.");
            }
            ThrowIfDisposed();

            if (count == 0)
            {
                return Array.Empty<byte>();
            }

            // SharpOS cut: no Stream.ReadAtLeast — the same loop inline.
            byte[] result = new byte[count];
            int numRead = 0;
            while (numRead < count)
            {
                int n = _stream.Read(result, numRead, count - numRead);
                if (n == 0) break;
                numRead += n;
            }

            if (numRead != result.Length)
            {
                // Trim array.  This should happen on EOF & possibly net streams.
                byte[] copy = new byte[numRead];
                Array.Copy(result, copy, numRead);
                result = copy;
            }

            return result;
        }

        private ReadOnlySpan<byte> InternalRead(int numBytes)
        {
            // SharpOS cut: the MemoryStream shortcut.
            ThrowIfDisposed();

            _stream.ReadExactly(new Span<byte>(_buffer, 0, numBytes));

            return _buffer;
        }

        // FillBuffer is not performing well when reading from MemoryStreams as it is using the public Stream interface.
        // Kept for compatibility, as the original does.
        protected virtual void FillBuffer(int numBytes)
        {
            if (numBytes < 0 || numBytes > _buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(numBytes), "The number of bytes requested does not fit into BinaryReader's internal buffer.");
            }

            ThrowIfDisposed();

            if (numBytes == 1)
            {
                int n = _stream.ReadByte();
                if (n == -1)
                {
                    throw new EndOfStreamException("Unable to read beyond the end of the stream.");
                }

                _buffer[0] = (byte)n;
                return;
            }

            if (numBytes > 0)
            {
                _stream.ReadExactly(new Span<byte>(_buffer, 0, numBytes));
            }
            else
            {
                // ReadExactly no-ops for empty buffers, so special case numBytes == 0 to preserve existing behavior.
                int n = _stream.Read(_buffer, 0, 0);
                if (n == 0)
                {
                    throw new EndOfStreamException("Unable to read beyond the end of the stream.");
                }
            }
        }

        public int Read7BitEncodedInt()
        {
            // Unlike writing, we can't delegate to the 64-bit read on
            // 64-bit platforms. The reason for this is that we want to
            // stop consuming bytes if we encounter an integer overflow.

            uint result = 0;
            byte byteReadJustNow;

            // Read the integer 7 bits at a time. The high bit
            // of the byte when on means to continue reading more bytes.
            //
            // There are two failure cases: we've read more than 5 bytes,
            // or the fifth byte is about to cause integer overflow.
            // This means that we can read the first 4 bytes without
            // worrying about integer overflow.

            const int MaxBytesWithoutOverflow = 4;
            for (int shift = 0; shift < MaxBytesWithoutOverflow * 7; shift += 7)
            {
                // ReadByte handles end of stream cases for us.
                byteReadJustNow = ReadByte();
                result |= (byteReadJustNow & 0x7Fu) << shift;

                if (byteReadJustNow <= 0x7Fu)
                {
                    return (int)result; // early exit
                }
            }

            // Read the 5th byte. Since we already read 28 bits,
            // the value of this byte must fit within 4 bits (32 - 28),
            // and it must not have the high bit set.

            byteReadJustNow = ReadByte();
            if (byteReadJustNow > 0b_1111u)
            {
                throw new FormatException("Too many bytes in what should have been a 7-bit encoded integer.");
            }

            result |= (uint)byteReadJustNow << (MaxBytesWithoutOverflow * 7);
            return (int)result;
        }

        public long Read7BitEncodedInt64()
        {
            ulong result = 0;
            byte byteReadJustNow;

            // Read the integer 7 bits at a time. The high bit
            // of the byte when on means to continue reading more bytes.
            //
            // There are two failure cases: we've read more than 10 bytes,
            // or the tenth byte is about to cause integer overflow.
            // This means that we can read the first 9 bytes without
            // worrying about integer overflow.

            const int MaxBytesWithoutOverflow = 9;
            for (int shift = 0; shift < MaxBytesWithoutOverflow * 7; shift += 7)
            {
                // ReadByte handles end of stream cases for us.
                byteReadJustNow = ReadByte();
                result |= (byteReadJustNow & 0x7Ful) << shift;

                if (byteReadJustNow <= 0x7Fu)
                {
                    return (long)result; // early exit
                }
            }

            // Read the 10th byte. Since we already read 63 bits,
            // the value of this byte must fit within 1 bit (64 - 63),
            // and it must not have the high bit set.

            byteReadJustNow = ReadByte();
            if (byteReadJustNow > 0b_1u)
            {
                throw new FormatException("Too many bytes in what should have been a 7-bit encoded integer.");
            }

            result |= (ulong)byteReadJustNow << (MaxBytesWithoutOverflow * 7);
            return (long)result;
        }
    }
}
