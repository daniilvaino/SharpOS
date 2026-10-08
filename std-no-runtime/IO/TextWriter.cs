// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/runtime
//   src/libraries/System.Private.CoreLib/src/System/IO/TextWriter.cs
//
// Cuts vs original:
//   - MarshalByRefObject base, IAsyncDisposable / DisposeAsync.
//   - The whole "Task based Async APIs" region (WriteAsync, WriteLineAsync,
//     FlushAsync).
//   - Synchronized / SyncTextWriter.
//   - Write(decimal) / WriteLine(decimal): no System.Decimal in this std.
//   - Write(StringBuilder) writes value.ToString(): no StringBuilder.GetChunks
//     here. Same output, one intermediate string.
//   - Format overloads call string.Format(format, ...) without the provider:
//     our string.Format has no IFormatProvider overloads (StringFormat.cs),
//     and the only culture is the invariant one anyway.
//   - [StringSyntax] / [CLSCompliant] attributes dropped; SR.* -> literals.
//   - Static state: `public static readonly TextWriter Null` became a property
//     returning a fresh NullTextWriter, and the shared `s_coreNewLine` array
//     became a per-instance `Environment.NewLine.ToCharArray()`. std is also
//     compiled into the kernel, where GC statics are materialised late
//     (CLAUDE.md, cctor trap); stateless singletons in std are properties
//     (Encoding.UTF8, ArrayPool<T>.Shared). Callers read `TextWriter.Null`
//     unchanged; only reference identity between two reads differs.
//     Environment.NewLineConst does not exist here; Environment.NewLine is
//     the same constant ("\r\n").

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace System.IO
{
    // This abstract base class represents a writer that can write a sequential
    // stream of characters. A subclass must minimally implement the
    // Write(char) method.
    //
    // This class is intended for character output, not bytes.
    // There are methods on the Stream class for writing bytes.
    public abstract partial class TextWriter : IDisposable
    {
        public static TextWriter Null => new NullTextWriter();

        /// <summary>
        /// This is the 'NewLine' property expressed as a char[].
        /// It is exposed to subclasses as a protected field for read-only
        /// purposes.  You should only modify it by using the 'NewLine' property.
        /// </summary>
        protected char[] CoreNewLine = Environment.NewLine.ToCharArray();
        private string CoreNewLineStr = Environment.NewLine;

        // Can be null - if so, ask for the Thread's CurrentCulture every time.
        private readonly IFormatProvider? _internalFormatProvider;

        protected TextWriter()
        {
        }

        protected TextWriter(IFormatProvider? formatProvider)
        {
            _internalFormatProvider = formatProvider;
        }

        public virtual IFormatProvider FormatProvider
            => _internalFormatProvider ?? CultureInfo.CurrentCulture;

        public virtual void Close()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Clears all buffers for this TextWriter and causes any buffered data to be
        // written to the underlying device. This default method is empty, but
        // descendant classes can override the method to provide the appropriate
        // functionality.
        public virtual void Flush()
        {
        }

        public abstract Encoding Encoding
        {
            get;
        }

        /// <summary>
        /// Returns the line terminator string used by this TextWriter. The default line
        /// terminator string is Environment.NewLine.
        /// </summary>
        [AllowNull]
        public virtual string NewLine
        {
            get => CoreNewLineStr;
            set
            {
                value ??= Environment.NewLine;

                CoreNewLineStr = value;
                CoreNewLine = value.ToCharArray();
            }
        }

        // Writes a character to the text stream. This default method is empty,
        // but descendant classes can override the method to provide the
        // appropriate functionality.
        //
        public virtual void Write(char value)
        {
        }

        // Writes a character array to the text stream. This default method calls
        // Write(char) for each of the characters in the character array.
        // If the character array is null, nothing is written.
        //
        public virtual void Write(char[]? buffer)
        {
            if (buffer != null)
            {
                Write(buffer, 0, buffer.Length);
            }
        }

        // Writes a range of a character array to the text stream. This method will
        // write count characters of data into this TextWriter from the
        // buffer character array starting at position index.
        //
        public virtual void Write(char[] buffer, int index, int count)
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

            for (int i = 0; i < count; i++) Write(buffer[index + i]);
        }

        // Writes a span of characters to the text stream.
        //
        public virtual void Write(ReadOnlySpan<char> buffer)
        {
            char[] array = ArrayPool<char>.Shared.Rent(buffer.Length);

            try
            {
                buffer.CopyTo(new Span<char>(array));
                Write(array, 0, buffer.Length);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(array);
            }
        }

        // Writes the text representation of a boolean to the text stream. This
        // method outputs either bool.TrueString or bool.FalseString.
        //
        public virtual void Write(bool value)
        {
            Write(value ? "True" : "False");
        }

        // Writes the text representation of an integer to the text stream. The
        // text representation of the given value is produced by calling the
        // int.ToString() method.
        //
        public virtual void Write(int value)
        {
            Write(value.ToString(FormatProvider));
        }

        // Writes the text representation of an integer to the text stream. The
        // text representation of the given value is produced by calling the
        // uint.ToString() method.
        //
        public virtual void Write(uint value)
        {
            Write(value.ToString(FormatProvider));
        }

        // Writes the text representation of a long to the text stream. The
        // text representation of the given value is produced by calling the
        // long.ToString() method.
        //
        public virtual void Write(long value)
        {
            Write(value.ToString(FormatProvider));
        }

        // Writes the text representation of an unsigned long to the text
        // stream. The text representation of the given value is produced
        // by calling the ulong.ToString() method.
        //
        public virtual void Write(ulong value)
        {
            Write(value.ToString(FormatProvider));
        }

        // Writes the text representation of a float to the text stream. The
        // text representation of the given value is produced by calling the
        // float.ToString(float) method.
        //
        public virtual void Write(float value)
        {
            Write(value.ToString(FormatProvider));
        }

        // Writes the text representation of a double to the text stream. The
        // text representation of the given value is produced by calling the
        // double.ToString(double) method.
        //
        public virtual void Write(double value)
        {
            Write(value.ToString(FormatProvider));
        }

        // Writes a string to the text stream. If the given string is null, nothing
        // is written to the text stream.
        //
        public virtual void Write(string? value)
        {
            if (value != null)
            {
                Write(value.ToCharArray());
            }
        }

        // Writes the text representation of an object to the text stream. If the
        // given object is null, nothing is written to the text stream.
        // Otherwise, the object's ToString method is called to produce the
        // string representation, and the resulting string is then written to the
        // output stream.
        //
        public virtual void Write(object? value)
        {
            if (value != null)
            {
                if (value is IFormattable f)
                {
                    Write(f.ToString(null, FormatProvider));
                }
                else
                    Write(value.ToString());
            }
        }

        /// <summary>
        /// Equivalent to Write(stringBuilder.ToString()).
        /// </summary>
        public virtual void Write(StringBuilder? value)
        {
            if (value != null)
            {
                // SharpOS cut: upstream walks value.GetChunks(); we have none.
                Write(value.ToString());
            }
        }

        // Writes out a formatted string.  Uses the same semantics as
        // string.Format.
        //
        public virtual void Write(string format, object? arg0)
        {
            Write(string.Format(format, arg0));
        }

        // Writes out a formatted string.  Uses the same semantics as
        // string.Format.
        //
        public virtual void Write(string format, object? arg0, object? arg1)
        {
            Write(string.Format(format, arg0, arg1));
        }

        // Writes out a formatted string.  Uses the same semantics as
        // string.Format.
        //
        public virtual void Write(string format, object? arg0, object? arg1, object? arg2)
        {
            Write(string.Format(format, arg0, arg1, arg2));
        }

        // Writes out a formatted string.  Uses the same semantics as
        // string.Format.
        //
        public virtual void Write(string format, params object?[] arg)
        {
            Write(string.Format(format, arg));
        }

        // Writes a line terminator to the text stream. The default line terminator
        // is Environment.NewLine, but this value can be changed by setting the NewLine property.
        //
        public virtual void WriteLine()
        {
            Write(CoreNewLine);
        }

        // Writes a character followed by a line terminator to the text stream.
        //
        public virtual void WriteLine(char value)
        {
            Write(value);
            WriteLine();
        }

        // Writes an array of characters followed by a line terminator to the text
        // stream.
        //
        public virtual void WriteLine(char[]? buffer)
        {
            Write(buffer);
            WriteLine();
        }

        // Writes a range of a character array followed by a line terminator to the
        // text stream.
        //
        public virtual void WriteLine(char[] buffer, int index, int count)
        {
            Write(buffer, index, count);
            WriteLine();
        }

        public virtual void WriteLine(ReadOnlySpan<char> buffer)
        {
            char[] array = ArrayPool<char>.Shared.Rent(buffer.Length);

            try
            {
                buffer.CopyTo(new Span<char>(array));
                WriteLine(array, 0, buffer.Length);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(array);
            }
        }

        // Writes the text representation of a boolean followed by a line
        // terminator to the text stream.
        //
        public virtual void WriteLine(bool value)
        {
            Write(value);
            WriteLine();
        }

        // Writes the text representation of an integer followed by a line
        // terminator to the text stream.
        //
        public virtual void WriteLine(int value)
        {
            Write(value);
            WriteLine();
        }

        // Writes the text representation of an unsigned integer followed by
        // a line terminator to the text stream.
        //
        public virtual void WriteLine(uint value)
        {
            Write(value);
            WriteLine();
        }

        // Writes the text representation of a long followed by a line terminator
        // to the text stream.
        //
        public virtual void WriteLine(long value)
        {
            Write(value);
            WriteLine();
        }

        // Writes the text representation of an unsigned long followed by
        // a line terminator to the text stream.
        //
        public virtual void WriteLine(ulong value)
        {
            Write(value);
            WriteLine();
        }

        // Writes the text representation of a float followed by a line terminator
        // to the text stream.
        //
        public virtual void WriteLine(float value)
        {
            Write(value);
            WriteLine();
        }

        // Writes the text representation of a double followed by a line terminator
        // to the text stream.
        //
        public virtual void WriteLine(double value)
        {
            Write(value);
            WriteLine();
        }

        // Writes a string followed by a line terminator to the text stream.
        //
        public virtual void WriteLine(string? value)
        {
            if (value != null)
            {
                Write(value);
            }
            Write(CoreNewLineStr);
        }

        /// <summary>
        /// Equivalent to WriteLine(stringBuilder.ToString()).
        /// </summary>
        public virtual void WriteLine(StringBuilder? value)
        {
            Write(value);
            WriteLine();
        }

        // Writes the text representation of an object followed by a line
        // terminator to the text stream.
        //
        public virtual void WriteLine(object? value)
        {
            if (value == null)
            {
                WriteLine();
            }
            else
            {
                // Call WriteLine(value.ToString), not Write(Object), WriteLine().
                // This makes calls to WriteLine(Object) atomic.
                if (value is IFormattable f)
                {
                    WriteLine(f.ToString(null, FormatProvider));
                }
                else
                {
                    WriteLine(value.ToString());
                }
            }
        }

        // Writes out a formatted string and a new line.  Uses the same
        // semantics as string.Format.
        //
        public virtual void WriteLine(string format, object? arg0)
        {
            WriteLine(string.Format(format, arg0));
        }

        // Writes out a formatted string and a new line.  Uses the same
        // semantics as string.Format.
        //
        public virtual void WriteLine(string format, object? arg0, object? arg1)
        {
            WriteLine(string.Format(format, arg0, arg1));
        }

        // Writes out a formatted string and a new line.  Uses the same
        // semantics as string.Format.
        //
        public virtual void WriteLine(string format, object? arg0, object? arg1, object? arg2)
        {
            WriteLine(string.Format(format, arg0, arg1, arg2));
        }

        // Writes out a formatted string and a new line.  Uses the same
        // semantics as string.Format.
        //
        public virtual void WriteLine(string format, params object?[] arg)
        {
            WriteLine(string.Format(format, arg));
        }

        private sealed class NullTextWriter : TextWriter
        {
            internal NullTextWriter()
            {
            }

            public override IFormatProvider FormatProvider => CultureInfo.InvariantCulture;

            public override Encoding Encoding => Encoding.Unicode;

            public override void Write(char[] buffer, int index, int count)
            {
            }

            public override void Write(string? value)
            {
            }

            // Not strictly necessary, but for perf reasons
            public override void WriteLine()
            {
            }

            // Not strictly necessary, but for perf reasons
            public override void WriteLine(string? value)
            {
            }

            public override void WriteLine(object? value)
            {
            }

            public override void Write(char value)
            {
            }
        }
    }
}
