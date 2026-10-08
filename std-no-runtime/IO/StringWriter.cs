// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/runtime
//   src/libraries/System.Private.CoreLib/src/System/IO/StringWriter.cs
//
// Cuts vs original:
//   - The "Task based Async APIs" region.
//   - Encoding: upstream caches `static volatile UnicodeEncoding? s_encoding`
//     (new UnicodeEncoding(false, false)); here a fresh UnicodeEncoding(false)
//     per call, the std convention for stateless encodings (no static field,
//     see the cctor trap in CLAUDE.md). Our UnicodeEncoding has no BOM flag.
//   - Write(ReadOnlySpan<char>) / Write(StringBuilder) / WriteLine(...):
//     upstream defers to the base when GetType() != typeof(StringWriter);
//     there is no object.GetType here, the direct path is taken always.
//   - SR.* -> literal messages.

using System.Globalization;
using System.Text;

namespace System.IO
{
    // This class implements a text writer that writes to a string buffer and allows
    // the resulting sequence of characters to be presented as a string.
    public class StringWriter : TextWriter
    {
        private readonly StringBuilder _sb;
        private bool _isOpen;

        // Constructs a new StringWriter. A new StringBuilder is automatically
        // created and associated with the new StringWriter.
        public StringWriter()
            : this(new StringBuilder(), CultureInfo.CurrentCulture)
        {
        }

        public StringWriter(IFormatProvider? formatProvider)
            : this(new StringBuilder(), formatProvider)
        {
        }

        // Constructs a new StringWriter that writes to the given StringBuilder.
        //
        public StringWriter(StringBuilder sb) : this(sb, CultureInfo.CurrentCulture)
        {
        }

        public StringWriter(StringBuilder sb, IFormatProvider? formatProvider) : base(formatProvider)
        {
            ArgumentNullException.ThrowIfNull(sb, nameof(sb));

            _sb = sb;
            _isOpen = true;
        }

        public override void Close()
        {
            Dispose(true);
        }

        protected override void Dispose(bool disposing)
        {
            // Do not destroy _sb, so that we can extract this after we are
            // done writing (similar to MemoryStream's GetBuffer & ToArray methods)
            _isOpen = false;
            base.Dispose(disposing);
        }

        public override Encoding Encoding => new UnicodeEncoding(false);

        // Returns the underlying StringBuilder. This is either the StringBuilder
        // that was passed to the constructor, or the StringBuilder that was
        // automatically created.
        //
        public virtual StringBuilder GetStringBuilder()
        {
            return _sb;
        }

        // Writes a character to the underlying string buffer.
        //
        public override void Write(char value)
        {
            if (!_isOpen)
            {
                throw new ObjectDisposedException(null, WriterClosed);
            }

            _sb.Append(value);
        }

        // Writes a range of a character array to the underlying string buffer.
        // This method will write count characters of data into this
        // StringWriter from the buffer character array starting at position
        // index.
        //
        public override void Write(char[] buffer, int index, int count)
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
            if (!_isOpen)
            {
                throw new ObjectDisposedException(null, WriterClosed);
            }

            _sb.Append(buffer, index, count);
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            // SharpOS cut: no `GetType() != typeof(StringWriter)` fallback to base.

            if (!_isOpen)
            {
                throw new ObjectDisposedException(null, WriterClosed);
            }

            _sb.Append(buffer);
        }

        // Writes a string to the underlying string buffer. If the given string is
        // null, nothing is written.
        //
        public override void Write(string? value)
        {
            if (!_isOpen)
            {
                throw new ObjectDisposedException(null, WriterClosed);
            }

            if (value != null)
            {
                _sb.Append(value);
            }
        }

        public override void Write(StringBuilder? value)
        {
            // SharpOS cut: no `GetType() != typeof(StringWriter)` fallback to base.

            if (!_isOpen)
            {
                throw new ObjectDisposedException(null, WriterClosed);
            }

            if (value != null)
            {
                _sb.Append(value);
            }
        }

        public override void WriteLine(ReadOnlySpan<char> buffer)
        {
            // SharpOS cut: no `GetType() != typeof(StringWriter)` fallback to base.

            if (!_isOpen)
            {
                throw new ObjectDisposedException(null, WriterClosed);
            }

            _sb.Append(buffer);
            WriteLine();
        }

        public override void WriteLine(StringBuilder? value)
        {
            // SharpOS cut: no `GetType() != typeof(StringWriter)` fallback to base.

            if (!_isOpen)
            {
                throw new ObjectDisposedException(null, WriterClosed);
            }

            if (value != null)
            {
                _sb.Append(value);
            }
            WriteLine();
        }

        // Returns a string containing the characters written to this TextWriter so far.
        public override string ToString()
        {
            return _sb.ToString();
        }

        private const string WriterClosed = "Cannot write to a closed TextWriter.";
    }
}
