// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/runtime
//   src/libraries/System.Private.CoreLib/src/System/IO/StringReader.cs
//
// Cuts vs original:
//   - The "Task based Async APIs" region.
//   - Read(Span<char>): upstream falls back to base.Read(Span) when
//     GetType() != typeof(StringReader), so a subclass overriding
//     Read(char[],int,int) is honoured. There is no object.GetType here; the
//     fast path is taken unconditionally.
//   - string.CopyTo(int, char[], int, int) and span IndexOfAny(char, char) are
//     not in this std: the copy goes through AsSpan().CopyTo, the line-end
//     search is a plain loop (marked `SharpOS cut:` at each spot).
//   - ThrowHelper / SR.* -> direct throws with literal messages.

using System.Diagnostics.CodeAnalysis;

namespace System.IO
{
    // This class implements a text reader that reads from a string.
    public class StringReader : TextReader
    {
        private string? _s;
        private int _pos;

        public StringReader(string s)
        {
            if (s is null)
            {
                throw new ArgumentNullException(nameof(s));
            }

            _s = s;
        }

        public override void Close()
        {
            Dispose(true);
        }

        protected override void Dispose(bool disposing)
        {
            _s = null;
            _pos = 0;
            base.Dispose(disposing);
        }

        // Returns the next available character without actually reading it from
        // the underlying string. The current position of the StringReader is not
        // changed by this operation. The returned value is -1 if no further
        // characters are available.
        //
        public override int Peek()
        {
            string? s = _s;
            if (s == null)
            {
                ThrowObjectDisposedException_ReaderClosed();
            }

            int pos = _pos;
            if ((uint)pos < (uint)s.Length)
            {
                return s[pos];
            }

            return -1;
        }

        // Reads the next character from the underlying string. The returned value
        // is -1 if no further characters are available.
        //
        public override int Read()
        {
            string? s = _s;
            if (s == null)
            {
                ThrowObjectDisposedException_ReaderClosed();
            }

            int pos = _pos;
            if ((uint)pos < (uint)s.Length)
            {
                _pos++;
                return s[pos];
            }

            return -1;
        }

        // Reads a block of characters. This method will read up to count
        // characters from this StringReader into the buffer character
        // array starting at position index. Returns the actual number of
        // characters read, or zero if the end of the string is reached.
        //
        public override int Read(char[] buffer, int index, int count)
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
            if (_s == null)
            {
                ThrowObjectDisposedException_ReaderClosed();
            }

            int n = _s.Length - _pos;
            if (n > 0)
            {
                if (n > count)
                {
                    n = count;
                }

                // SharpOS cut: upstream _s.CopyTo(_pos, buffer, index, n).
                _s.AsSpan(_pos, n).CopyTo(new Span<char>(buffer, index, n));
                _pos += n;
            }
            return n;
        }

        public override int Read(Span<char> buffer)
        {
            // SharpOS cut: no `GetType() != typeof(StringReader)` fallback to base.

            string? s = _s;
            if (s == null)
            {
                ThrowObjectDisposedException_ReaderClosed();
            }

            int n = s.Length - _pos;
            if (n > 0)
            {
                if (n > buffer.Length)
                {
                    n = buffer.Length;
                }

                s.AsSpan(_pos, n).CopyTo(buffer);
                _pos += n;
            }

            return n;
        }

        public override int ReadBlock(Span<char> buffer) => Read(buffer);

        public override string ReadToEnd()
        {
            string? s = _s;
            if (s == null)
            {
                ThrowObjectDisposedException_ReaderClosed();
            }

            int pos = _pos;
            _pos = s.Length;

            if (pos != 0)
            {
                s = s.Substring(pos);
            }

            return s;
        }

        // Reads a line. A line is defined as a sequence of characters followed by
        // a carriage return ('\r'), a line feed ('\n'), or a carriage return
        // immediately followed by a line feed. The resulting string does not
        // contain the terminating carriage return and/or line feed. The returned
        // value is null if the end of the underlying string has been reached.
        //
        public override string? ReadLine()
        {
            string? s = _s;
            if (s == null)
            {
                ThrowObjectDisposedException_ReaderClosed();
            }

            int pos = _pos;
            if ((uint)pos >= (uint)s.Length)
            {
                return null;
            }

            // SharpOS cut: upstream remaining.IndexOfAny('\r', '\n') over s.AsSpan(pos).
            int foundLineLength = -1;
            for (int i = pos; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\r' || c == '\n')
                {
                    foundLineLength = i - pos;
                    break;
                }
            }

            if (foundLineLength >= 0)
            {
                string result = s.Substring(pos, foundLineLength);

                char ch = s[pos + foundLineLength];
                pos += foundLineLength + 1;
                if (ch == '\r')
                {
                    if ((uint)pos < (uint)s.Length && s[pos] == '\n')
                    {
                        pos++;
                    }
                }
                _pos = pos;

                return result;
            }
            else
            {
                string result = s.Substring(pos);
                _pos = s.Length;
                return result;
            }
        }

        [DoesNotReturn]
        private static void ThrowObjectDisposedException_ReaderClosed()
        {
            throw new ObjectDisposedException(null, "Cannot read from a closed TextReader.");
        }
    }
}
