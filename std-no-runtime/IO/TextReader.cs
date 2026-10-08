// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/runtime
//   src/libraries/System.Private.CoreLib/src/System/IO/TextReader.cs
//
// Cuts vs original:
//   - MarshalByRefObject base (no remoting in this std).
//   - The whole "Task based Async APIs" region (ReadLineAsync, ReadToEndAsync,
//     ReadAsync, ReadBlockAsync, ReadAsyncInternal, ReadBlockAsyncInternal).
//   - Synchronized / SyncTextReader ([MethodImpl(Synchronized)] has no
//     monitor behind it here).
//   - SR.* resource strings -> literal messages.
//   - `Null` is a static property returning a fresh NullTextReader instead of
//     `public static readonly TextReader Null`: std is also compiled into the
//     kernel, where GC statics are materialised late (CLAUDE.md, cctor trap),
//     and the std convention for stateless singletons is a property
//     (Encoding.UTF8, ArrayPool<T>.Shared). Callers read it the same way;
//     only reference identity between two reads differs.
//     The original points at StreamReader.NullStreamReader; ours has no
//     StreamReader-specific state to carry, so a private NullTextReader.

using System.Buffers;
using System.Text;

namespace System.IO
{
    // This abstract base class represents a reader that can read a sequential
    // stream of characters.  This is not intended for reading bytes -
    // there are methods on the Stream class to read bytes.
    // A subclass must minimally implement the Peek() and Read() methods.
    //
    // This class is intended for character input, not bytes.
    // There are methods on the Stream class for reading bytes.
    public abstract partial class TextReader : IDisposable
    {
        public static TextReader Null => new NullTextReader();

        protected TextReader() { }

        public virtual void Close()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }

        // Returns the next available character without actually reading it from
        // the input stream. The current position of the TextReader is not changed by
        // this operation. The returned value is -1 if no further characters are
        // available.
        //
        // This default method simply returns -1.
        //
        public virtual int Peek()
        {
            return -1;
        }

        // Reads the next character from the input stream. The returned value is
        // -1 if no further characters are available.
        //
        // This default method simply returns -1.
        //
        public virtual int Read()
        {
            return -1;
        }

        // Reads a block of characters. This method will read up to
        // count characters from this TextReader into the
        // buffer character array starting at position
        // index. Returns the actual number of characters read.
        //
        public virtual int Read(char[] buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer, nameof(buffer));

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

            int n;
            for (n = 0; n < count; n++)
            {
                int ch = Read();
                if (ch == -1) break;
                buffer[index + n] = (char)ch;
            }

            return n;
        }

        // Reads a span of characters. This method will read up to
        // count characters from this TextReader into the
        // span of characters Returns the actual number of characters read.
        //
        public virtual int Read(Span<char> buffer)
        {
            char[] array = ArrayPool<char>.Shared.Rent(buffer.Length);

            try
            {
                int numRead = Read(array, 0, buffer.Length);
                if ((uint)numRead > (uint)buffer.Length)
                {
                    throw new IOException("The read operation returned an invalid length.");
                }
                new Span<char>(array, 0, numRead).CopyTo(buffer);
                return numRead;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(array);
            }
        }

        // Reads all characters from the current position to the end of the
        // TextReader, and returns them as one string.
        public virtual string ReadToEnd()
        {
            char[] chars = new char[4096];
            int len;
            StringBuilder sb = new StringBuilder(4096);
            while ((len = Read(chars, 0, chars.Length)) != 0)
            {
                sb.Append(chars, 0, len);
            }
            return sb.ToString();
        }

        // Blocking version of read.  Returns only when count
        // characters have been read or the end of the file was reached.
        //
        public virtual int ReadBlock(char[] buffer, int index, int count)
        {
            int i, n = 0;
            do
            {
                n += (i = Read(buffer, index + n, count - n));
            } while (i > 0 && n < count);
            return n;
        }

        // Blocking version of read for span of characters.  Returns only when count
        // characters have been read or the end of the file was reached.
        //
        public virtual int ReadBlock(Span<char> buffer)
        {
            char[] array = ArrayPool<char>.Shared.Rent(buffer.Length);

            try
            {
                int numRead = ReadBlock(array, 0, buffer.Length);
                if ((uint)numRead > (uint)buffer.Length)
                {
                    throw new IOException("The read operation returned an invalid length.");
                }
                new Span<char>(array, 0, numRead).CopyTo(buffer);
                return numRead;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(array);
            }
        }

        // Reads a line. A line is defined as a sequence of characters followed by
        // a carriage return ('\r'), a line feed ('\n'), or a carriage return
        // immediately followed by a line feed. The resulting string does not
        // contain the terminating carriage return and/or line feed. The returned
        // value is null if the end of the input stream has been reached.
        //
        public virtual string? ReadLine()
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                int ch = Read();
                if (ch == -1) break;
                if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && Peek() == '\n')
                    {
                        Read();
                    }

                    return sb.ToString();
                }
                sb.Append((char)ch);
            }
            if (sb.Length > 0)
            {
                return sb.ToString();
            }

            return null;
        }

        // Upstream: StreamReader.NullStreamReader. Everything reads as end of input.
        private sealed class NullTextReader : TextReader
        {
            public override int Peek() => -1;

            public override int Read() => -1;

            public override int Read(char[] buffer, int index, int count) => 0;

            public override int Read(Span<char> buffer) => 0;

            public override string? ReadLine() => null;

            public override string ReadToEnd() => string.Empty;
        }
    }
}
