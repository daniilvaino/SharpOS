// System.IO.BinaryWriter — ported from dotnet/runtime release/8.0
//   src/libraries/System.Private.CoreLib/src/System/IO/BinaryWriter.cs (MIT)
//
// Cuts vs original (each marked "SharpOS cut:" in place):
//   - IAsyncDisposable / DisposeAsync — no ValueTask.
//   - Write(decimal), Write(Half) — neither type exists here.
//   - Write(char) through Rune — surrogates refused with the same exception,
//     the char encoded through the Encoding.
//   - The Encoder path for very large inputs — the whole input is encoded at
//     once instead (GetByteCount + one array).
//   - SR resource strings — literal messages.
//
// Replaces the per-app stand-ins in Fami and TriCNES (pipe_plan.md
// "Подготовить под трубы", item 5): the pipes' file bridge writes this format.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace System.IO
{
    public class BinaryWriter : IDisposable
    {
        private const int MaxArrayPoolRentalSize = 64 * 1024; // try to keep rentals to a reasonable size

        // SharpOS cut: a property returning a fresh writer, not a static
        // readonly field — a static with an initializer in std reaches code
        // that runs before statics exist. Same observable behaviour.
        public static BinaryWriter Null => new BinaryWriter();

        protected Stream OutStream;
        private readonly Encoding _encoding;
        private readonly bool _leaveOpen;
        private readonly bool _useFastUtf8;

        // Protected default constructor that sets the output stream
        // to a null stream (a bit bucket).
        protected BinaryWriter()
        {
            OutStream = Stream.Null;
            _encoding = Encoding.UTF8;
            _useFastUtf8 = true;
        }

        // BinaryWriter never emits a BOM, so can use Encoding.UTF8 fast singleton
        public BinaryWriter(Stream output) : this(output, Encoding.UTF8, false)
        {
        }

        public BinaryWriter(Stream output, Encoding encoding) : this(output, encoding, false)
        {
        }

        public BinaryWriter(Stream output, Encoding encoding, bool leaveOpen)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (encoding == null) throw new ArgumentNullException(nameof(encoding));

            if (!output.CanWrite)
                throw new ArgumentException("Stream was not writable.");

            OutStream = output;
            _encoding = encoding;
            _leaveOpen = leaveOpen;
            // SharpOS cut: no IsUTF8CodePage / EncoderFallback — the type says it.
            _useFastUtf8 = encoding is UTF8Encoding;
        }

        // Closes this writer and releases any system resources associated with the
        // writer. Following a call to Close, any operations on the writer
        // may raise exceptions.
        public virtual void Close()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_leaveOpen)
                    OutStream.Flush();
                else
                    OutStream.Close();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        // Returns the stream associated with the writer. It flushes all pending
        // writes before returning. All subclasses should override Flush to
        // ensure that all buffered data is sent to the stream.
        public virtual Stream BaseStream
        {
            get
            {
                Flush();
                return OutStream;
            }
        }

        // Clears all buffers for this writer and causes any buffered data to be
        // written to the underlying device.
        public virtual void Flush()
        {
            OutStream.Flush();
        }

        public virtual long Seek(int offset, SeekOrigin origin)
        {
            return OutStream.Seek(offset, origin);
        }

        // Writes a boolean to this stream. A single byte is written to the stream
        // with the value 0 representing false or the value 1 representing true.
        //
        public virtual void Write(bool value) => OutStream.WriteByte((byte)(value ? 1 : 0));

        // Writes a byte to this stream. The current position of the stream is
        // advanced by one.
        //
        public virtual void Write(byte value) => OutStream.WriteByte(value);

        // Writes a signed byte to this stream. The current position of the stream
        // is advanced by one.
        //
        public virtual void Write(sbyte value) => OutStream.WriteByte((byte)value);

        // Writes a byte array to this stream.
        //
        // This default implementation calls the Write(Object, int, int)
        // method to write the byte array.
        //
        public virtual void Write(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            OutStream.Write(buffer, 0, buffer.Length);
        }

        // Writes a section of a byte array to this stream.
        //
        // This default implementation calls the Write(Object, int, int)
        // method to write the byte array.
        //
        public virtual void Write(byte[] buffer, int index, int count)
        {
            OutStream.Write(buffer, index, count);
        }

        // Writes a character to this stream. The current position of the stream is
        // advanced by two.
        // Note this method cannot handle surrogates properly in UTF-8.
        //
        public virtual void Write(char ch)
        {
            // SharpOS cut: no Rune — a lone surrogate is refused directly, the
            // same exception Rune.TryCreate leads to.
            if (char.IsSurrogate(ch))
                throw new ArgumentException("Surrogate characters are not allowed as a single char.");

            Span<byte> buffer = stackalloc byte[8]; // reasonable guess for worst-case expansion for any arbitrary encoding
            Span<char> one = stackalloc char[1];
            one[0] = ch;
            int actualByteCount = _encoding.GetBytes(one, buffer);
            OutStream.Write(buffer.Slice(0, actualByteCount));
        }

        // Writes a character array to this stream.
        //
        // This default implementation calls the Write(Object, int, int)
        // method to write the character array.
        //
        public virtual void Write(char[] chars)
        {
            if (chars == null) throw new ArgumentNullException(nameof(chars));

            WriteCharsCommonWithoutLengthPrefix(chars, useThisWriteOverride: false);
        }

        // Writes a section of a character array to this stream.
        //
        // This default implementation calls the Write(Object, int, int)
        // method to write the character array.
        //
        public virtual void Write(char[] chars, int index, int count)
        {
            if (chars == null) throw new ArgumentNullException(nameof(chars));

            // SharpOS cut: release/8.0 calls ArgumentOutOfRangeException.ThrowIfNegative,
            // a generic-math method (INumberBase) this std lacks; the same check inline.
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index), "Non-negative number required.");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Non-negative number required.");
            if (index > chars.Length - count)
                throw new ArgumentOutOfRangeException(nameof(index), "Index and count must refer to a location within the buffer.");

            WriteCharsCommonWithoutLengthPrefix(new ReadOnlySpan<char>(chars, index, count), useThisWriteOverride: false);
        }

        // Writes a double to this stream. The current position of the stream is
        // advanced by eight.
        //
        public virtual void Write(double value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(double)];
            BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
            OutStream.Write(buffer);
        }

        // Writes a two-byte signed integer to this stream. The current position of
        // the stream is advanced by two.
        //
        public virtual void Write(short value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(short)];
            BinaryPrimitives.WriteInt16LittleEndian(buffer, value);
            OutStream.Write(buffer);
        }

        // Writes a two-byte unsigned integer to this stream. The current position
        // of the stream is advanced by two.
        //
        public virtual void Write(ushort value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            OutStream.Write(buffer);
        }

        // Writes a four-byte signed integer to this stream. The current position
        // of the stream is advanced by four.
        //
        public virtual void Write(int value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            OutStream.Write(buffer);
        }

        // Writes a four-byte unsigned integer to this stream. The current position
        // of the stream is advanced by four.
        //
        public virtual void Write(uint value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            OutStream.Write(buffer);
        }

        // Writes an eight-byte signed integer to this stream. The current position
        // of the stream is advanced by eight.
        //
        public virtual void Write(long value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            OutStream.Write(buffer);
        }

        // Writes an eight-byte unsigned integer to this stream. The current
        // position of the stream is advanced by eight.
        //
        public virtual void Write(ulong value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            OutStream.Write(buffer);
        }

        // Writes a float to this stream. The current position of the stream is
        // advanced by four.
        //
        public virtual void Write(float value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(float)];
            BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
            OutStream.Write(buffer);
        }

        // Writes a length-prefixed string to this stream in the BinaryWriter's
        // current Encoding. This method first writes the length of the string as
        // an encoded unsigned integer with variable length, and then writes that many characters
        // to the stream.
        //
        public virtual void Write(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));

            // Common: UTF-8, small string, avoid 2-pass calculation
            // Less common: UTF-8, large string, avoid 2-pass calculation
            // Uncommon: excessively large string or not UTF-8

            if (_useFastUtf8)
            {
                if (value.Length <= 127 / 3)
                {
                    // Max expansion: each char -> 3 bytes, so 127 bytes max of data, +1 for length prefix
                    Span<byte> buffer = stackalloc byte[128];
                    int actualByteCount = _encoding.GetBytes(value.AsSpan(), buffer.Slice(1));
                    buffer[0] = (byte)actualByteCount; // bypass call to Write7BitEncodedInt
                    OutStream.Write(buffer.Slice(0, actualByteCount + 1 /* length prefix */));
                    return;
                }
                else if (value.Length <= MaxArrayPoolRentalSize / 3)
                {
                    byte[] rented = ArrayPool<byte>.Shared.Rent(value.Length * 3); // max expansion: each char -> 3 bytes
                    int actualByteCount = _encoding.GetBytes(value.AsSpan(), rented);
                    Write7BitEncodedInt(actualByteCount);
                    OutStream.Write(rented, 0, actualByteCount);
                    ArrayPool<byte>.Shared.Return(rented);
                    return;
                }
            }

            // Slow path: not fast UTF-8, or data is very large. We need to fall back
            // to a 2-pass mechanism so that we're not renting absurdly large arrays.

            int actualBytecount = _encoding.GetByteCount(value);
            Write7BitEncodedInt(actualBytecount);
            WriteCharsCommonWithoutLengthPrefix(value.AsSpan(), useThisWriteOverride: false);
        }

        public virtual void Write(ReadOnlySpan<byte> buffer)
        {
            // SharpOS cut: the original bounces through this.Write(byte[], ...)
            // when a derived class might override it, detected with
            // GetType() == typeof(BinaryWriter); no Type here. Straight to the
            // stream, as the original does for a plain BinaryWriter.
            OutStream.Write(buffer);
        }

        public virtual void Write(ReadOnlySpan<char> chars)
        {
            // When Write(ROS<char>) was first introduced, it dispatched to the this.Write(byte[], ...)
            // virtual method rather than write directly to the output stream. We maintain that same
            // double-indirection for compat purposes.
            WriteCharsCommonWithoutLengthPrefix(chars, useThisWriteOverride: true);
        }

        private void WriteCharsCommonWithoutLengthPrefix(ReadOnlySpan<char> chars, bool useThisWriteOverride)
        {
            byte[] rented;

            if (chars.Length <= MaxArrayPoolRentalSize)
            {
                // GetByteCount may walk the buffer contents, resulting in 2 passes over the data.
                // We prefer GetMaxByteCount because it's a constant-time operation.

                int maxByteCount = _encoding.GetMaxByteCount(chars.Length);
                if (maxByteCount <= MaxArrayPoolRentalSize)
                {
                    rented = ArrayPool<byte>.Shared.Rent(maxByteCount);
                    int actualByteCount = _encoding.GetBytes(chars, rented);
                    WriteToOutStream(rented, 0, actualByteCount, useThisWriteOverride);
                    ArrayPool<byte>.Shared.Return(rented);
                    return;
                }
            }

            // SharpOS cut: no Encoder to convert in chunks — the whole input is
            // encoded at once. Inputs this large are rare and the cost is the
            // transcoding either way; what is lost is only the bounded buffer.
            byte[] all = new byte[_encoding.GetByteCount(chars)];
            int written = _encoding.GetBytes(chars, all);
            WriteToOutStream(all, 0, written, useThisWriteOverride);
        }

        private void WriteToOutStream(byte[] buffer, int offset, int count, bool useThisWriteOverride)
        {
            if (useThisWriteOverride)
            {
                Write(buffer, offset, count); // bounce through this.Write(...) overridden logic
            }
            else
            {
                OutStream.Write(buffer, offset, count); // ignore this.Write(...) override, go straight to inner stream
            }
        }

        public void Write7BitEncodedInt(int value)
        {
            uint uValue = (uint)value;

            // Write out an int 7 bits at a time. The high bit of the byte,
            // when on, tells reader to continue reading more bytes.
            //
            // Using the constants 0x7F and ~0x7F below offers smaller
            // codegen than using the constant 0x80.

            while (uValue > 0x7Fu)
            {
                Write((byte)(uValue | ~0x7Fu));
                uValue >>= 7;
            }

            Write((byte)uValue);
        }

        public void Write7BitEncodedInt64(long value)
        {
            ulong uValue = (ulong)value;

            // Write out an int 7 bits at a time. The high bit of the byte,
            // when on, tells reader to continue reading more bytes.
            //
            // Using the constants 0x7F and ~0x7F below offers smaller
            // codegen than using the constant 0x80.

            while (uValue > 0x7Fu)
            {
                Write((byte)((uint)uValue | ~0x7Fu));
                uValue >>= 7;
            }

            Write((byte)uValue);
        }
    }
}
