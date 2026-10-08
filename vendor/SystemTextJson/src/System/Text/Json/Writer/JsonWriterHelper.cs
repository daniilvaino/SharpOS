// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
// SharpOS cut: using System.Text.Encodings.Web (not ported; default escaping is built in, see JsonWriterHelper.Escaping.cs).
// SharpOS cut: using System.Text.Unicode (Utf8.IsValid / Utf8.FromUtf16 are not in std; scalar equivalents below).

namespace System.Text.Json
{
    internal static partial class JsonWriterHelper
    {
        public static void WriteIndentation(Span<byte> buffer, int indent, byte indentByte)
        {
            Debug.Assert(buffer.Length >= indent);

            // Based on perf tests, the break-even point where vectorized Fill is faster
            // than explicitly writing the space in a loop is 8.
            if (indent < 8)
            {
                int i = 0;
                while (i + 1 < indent)
                {
                    buffer[i++] = indentByte;
                    buffer[i++] = indentByte;
                }

                if (i < indent)
                {
                    buffer[i] = indentByte;
                }
            }
            else
            {
                buffer.Slice(0, indent).Fill(indentByte);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidateNewLine(string value)
        {
            if (value is null)
                ThrowHelper.ThrowArgumentNullException(nameof(value));

            if (value is not JsonConstants.NewLineLineFeed and not JsonConstants.NewLineCarriageReturnLineFeed)
                ThrowHelper.ThrowArgumentOutOfRangeException_NewLine(nameof(value));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidateIndentCharacter(char value)
        {
            if (value is not JsonConstants.DefaultIndentCharacter and not JsonConstants.TabIndentCharacter)
                ThrowHelper.ThrowArgumentOutOfRangeException_IndentCharacter(nameof(value));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidateIndentSize(int value)
        {
            if (value is < JsonConstants.MinimumIndentSize or > JsonConstants.MaximumIndentSize)
                ThrowHelper.ThrowArgumentOutOfRangeException_IndentSize(nameof(value), JsonConstants.MinimumIndentSize, JsonConstants.MaximumIndentSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidateProperty(ReadOnlySpan<byte> propertyName)
        {
            if (propertyName.Length > JsonConstants.MaxUnescapedTokenSize)
                ThrowHelper.ThrowArgumentException_PropertyNameTooLarge(propertyName.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidateValue(ReadOnlySpan<byte> value)
        {
            if (value.Length > JsonConstants.MaxUnescapedTokenSize)
                ThrowHelper.ThrowArgumentException_ValueTooLarge(value.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidateDouble(double value)
        {
            if (!JsonHelpers.IsFinite(value))
            {
                ThrowHelper.ThrowArgumentException_ValueNotSupported();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidateSingle(float value)
        {
            if (!JsonHelpers.IsFinite(value))
            {
                ThrowHelper.ThrowArgumentException_ValueNotSupported();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidateProperty(ReadOnlySpan<char> propertyName)
        {
            if (propertyName.Length > JsonConstants.MaxCharacterTokenSize)
                ThrowHelper.ThrowArgumentException_PropertyNameTooLarge(propertyName.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidateValue(ReadOnlySpan<char> value)
        {
            if (value.Length > JsonConstants.MaxCharacterTokenSize)
                ThrowHelper.ThrowArgumentException_ValueTooLarge(value.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidatePropertyAndValue(ReadOnlySpan<char> propertyName, ReadOnlySpan<byte> value)
        {
            if (propertyName.Length > JsonConstants.MaxCharacterTokenSize || value.Length > JsonConstants.MaxUnescapedTokenSize)
                ThrowHelper.ThrowArgumentException(propertyName, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidatePropertyAndValue(ReadOnlySpan<byte> propertyName, ReadOnlySpan<char> value)
        {
            if (propertyName.Length > JsonConstants.MaxUnescapedTokenSize || value.Length > JsonConstants.MaxCharacterTokenSize)
                ThrowHelper.ThrowArgumentException(propertyName, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidatePropertyAndValue(ReadOnlySpan<byte> propertyName, ReadOnlySpan<byte> value)
        {
            if (propertyName.Length > JsonConstants.MaxUnescapedTokenSize || value.Length > JsonConstants.MaxUnescapedTokenSize)
                ThrowHelper.ThrowArgumentException(propertyName, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidatePropertyAndValue(ReadOnlySpan<char> propertyName, ReadOnlySpan<char> value)
        {
            if (propertyName.Length > JsonConstants.MaxCharacterTokenSize || value.Length > JsonConstants.MaxCharacterTokenSize)
                ThrowHelper.ThrowArgumentException(propertyName, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidatePropertyNameLength(ReadOnlySpan<char> propertyName)
        {
            if (propertyName.Length > JsonConstants.MaxCharacterTokenSize)
                ThrowHelper.ThrowPropertyNameTooLargeArgumentException(propertyName.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ValidatePropertyNameLength(ReadOnlySpan<byte> propertyName)
        {
            if (propertyName.Length > JsonConstants.MaxUnescapedTokenSize)
                ThrowHelper.ThrowPropertyNameTooLargeArgumentException(propertyName.Length);
        }

        internal static void ValidateNumber(ReadOnlySpan<byte> utf8FormattedNumber)
        {
            // This is a simplified version of the number reader from Utf8JsonReader.TryGetNumber,
            // because it doesn't need to deal with "NeedsMoreData", or remembering the format.
            //
            // The Debug.Asserts in this method should change to validated ArgumentExceptions if/when
            // writing a formatted number becomes public API.
            Debug.Assert(!utf8FormattedNumber.IsEmpty);

            int i = 0;

            if (utf8FormattedNumber[i] == '-')
            {
                i++;

                if (utf8FormattedNumber.Length <= i)
                {
                    throw new ArgumentException(SR.RequiredDigitNotFoundEndOfData, nameof(utf8FormattedNumber));
                }
            }

            if (utf8FormattedNumber[i] == '0')
            {
                i++;
            }
            else
            {
                while (i < utf8FormattedNumber.Length && JsonHelpers.IsDigit(utf8FormattedNumber[i]))
                {
                    i++;
                }
            }

            if (i == utf8FormattedNumber.Length)
            {
                return;
            }

            // The non digit character inside the number
            byte val = utf8FormattedNumber[i];

            if (val == '.')
            {
                i++;

                if (utf8FormattedNumber.Length <= i)
                {
                    throw new ArgumentException(SR.RequiredDigitNotFoundEndOfData, nameof(utf8FormattedNumber));
                }

                while (i < utf8FormattedNumber.Length && JsonHelpers.IsDigit(utf8FormattedNumber[i]))
                {
                    i++;
                }

                if (i == utf8FormattedNumber.Length)
                {
                    return;
                }

                Debug.Assert(i < utf8FormattedNumber.Length);
                val = utf8FormattedNumber[i];
            }

            if (val == 'e' || val == 'E')
            {
                i++;

                if (utf8FormattedNumber.Length <= i)
                {
                    throw new ArgumentException(SR.RequiredDigitNotFoundEndOfData, nameof(utf8FormattedNumber));
                }

                val = utf8FormattedNumber[i];

                if (val == '+' || val == '-')
                {
                    i++;
                }
            }
            else
            {
                throw new ArgumentException(
                    SR.Format(SR.ExpectedEndOfDigitNotFound, ThrowHelper.GetPrintableString(val)),
                    nameof(utf8FormattedNumber));
            }

            if (utf8FormattedNumber.Length <= i)
            {
                throw new ArgumentException(SR.RequiredDigitNotFoundEndOfData, nameof(utf8FormattedNumber));
            }

            while (i < utf8FormattedNumber.Length && JsonHelpers.IsDigit(utf8FormattedNumber[i]))
            {
                i++;
            }

            if (i != utf8FormattedNumber.Length)
            {
                throw new ArgumentException(
                    SR.Format(SR.ExpectedEndOfDigitNotFound, ThrowHelper.GetPrintableString(utf8FormattedNumber[i])),
                    nameof(utf8FormattedNumber));
            }
        }

#if !NET
        private static readonly UTF8Encoding s_utf8Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
#endif

        public static bool IsValidUtf8String(ReadOnlySpan<byte> bytes)
        {
#if true // SharpOS: was `#if NET` + Utf8.IsValid(bytes); same answer through DecodeUtf8Scalar (JsonWriterHelper.Escaping.cs).
            while (!bytes.IsEmpty)
            {
                if (DecodeUtf8Scalar(bytes, out _, out int consumed) != OperationStatus.Done)
                {
                    return false;
                }

                bytes = bytes.Slice(consumed);
            }

            return true;
#else
            try
            {
#if NET
                s_utf8Encoding.GetCharCount(bytes);
#else
                if (!bytes.IsEmpty)
                {
                    unsafe
                    {
                        fixed (byte* ptr = bytes)
                        {
                            s_utf8Encoding.GetCharCount(ptr, bytes.Length);
                        }
                    }
                }
#endif
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
#endif
        }

        internal static OperationStatus ToUtf8(ReadOnlySpan<char> source, Span<byte> destination, out int written)
        {
#if true // SharpOS: was `#if NET` + Utf8.FromUtf16(..., replaceInvalidSequences: false, isFinalBlock: true).
            OperationStatus status = FromUtf16(source, destination, out int charsRead, out written);
            Debug.Assert(status is OperationStatus.Done or OperationStatus.DestinationTooSmall or OperationStatus.InvalidData);
            Debug.Assert(charsRead == source.Length || status is not OperationStatus.Done);
            return status;
#else
            written = 0;
            try
            {
                if (!source.IsEmpty)
                {
                    unsafe
                    {
                        fixed (char* charPtr = source)
                        fixed (byte* destPtr = destination)
                        {
                            written = s_utf8Encoding.GetBytes(charPtr, source.Length, destPtr, destination.Length);
                        }
                    }
                }

                return OperationStatus.Done;
            }
            catch (EncoderFallbackException)
            {
                return OperationStatus.InvalidData;
            }
            catch (ArgumentException)
            {
                return OperationStatus.DestinationTooSmall;
            }
#endif
        }

        // SharpOS: Utf8.FromUtf16(source, destination, out charsRead, out bytesWritten,
        // replaceInvalidSequences: false, isFinalBlock: true) - std has no System.Text.Unicode.Utf8.
        // Done; DestinationTooSmall with the prefix that fit; InvalidData at an unpaired surrogate.
        private static OperationStatus FromUtf16(ReadOnlySpan<char> source, Span<byte> destination, out int charsRead, out int bytesWritten)
        {
            int src = 0;
            int dst = 0;
            OperationStatus status = OperationStatus.Done;

            while (src < source.Length)
            {
                uint c = source[src];
                int units = 1;
                if (c >= 0xD800 && c <= 0xDFFF)
                {
                    if (c > 0xDBFF || src + 1 >= source.Length || source[src + 1] < 0xDC00 || source[src + 1] > 0xDFFF)
                    {
                        status = OperationStatus.InvalidData;
                        break;
                    }

                    c = 0x10000 + ((c - 0xD800) << 10) + ((uint)source[src + 1] - 0xDC00);
                    units = 2;
                }

                int needed = c < 0x80 ? 1 : c < 0x800 ? 2 : c < 0x10000 ? 3 : 4;
                if (destination.Length - dst < needed)
                {
                    status = OperationStatus.DestinationTooSmall;
                    break;
                }

                switch (needed)
                {
                    case 1:
                        destination[dst] = (byte)c;
                        break;
                    case 2:
                        destination[dst] = (byte)(0xC0 | (c >> 6));
                        destination[dst + 1] = (byte)(0x80 | (c & 0x3F));
                        break;
                    case 3:
                        destination[dst] = (byte)(0xE0 | (c >> 12));
                        destination[dst + 1] = (byte)(0x80 | ((c >> 6) & 0x3F));
                        destination[dst + 2] = (byte)(0x80 | (c & 0x3F));
                        break;
                    default:
                        destination[dst] = (byte)(0xF0 | (c >> 18));
                        destination[dst + 1] = (byte)(0x80 | ((c >> 12) & 0x3F));
                        destination[dst + 2] = (byte)(0x80 | ((c >> 6) & 0x3F));
                        destination[dst + 3] = (byte)(0x80 | (c & 0x3F));
                        break;
                }

                dst += needed;
                src += units;
            }

            charsRead = src;
            bytesWritten = dst;
            return status;
        }

        internal delegate T WriteCallback<T>(ReadOnlySpan<byte> serializedValue);

        internal static T WriteString<T>(ReadOnlySpan<byte> utf8Value, WriteCallback<T> writeCallback)
        {
            int firstByteToEscape = JsonWriterHelper.NeedsEscaping(utf8Value); // SharpOS: no JavaScriptEncoder

            if (firstByteToEscape == -1)
            {
                int quotedLength = utf8Value.Length + 2;
                byte[]? rented = null;

                try
                {
                    Span<byte> quotedValue = quotedLength > JsonConstants.StackallocByteThreshold
                        ? (rented = ArrayPool<byte>.Shared.Rent(quotedLength)).AsSpan(0, quotedLength)
                        : stackalloc byte[JsonConstants.StackallocByteThreshold].Slice(0, quotedLength);

                    quotedValue[0] = JsonConstants.Quote;
                    utf8Value.CopyTo(quotedValue.Slice(1));
                    quotedValue[quotedValue.Length - 1] = JsonConstants.Quote;

                    return writeCallback(quotedValue);
                }
                finally
                {
                    if (rented != null)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }
            else
            {
                Debug.Assert(int.MaxValue / JsonConstants.MaxExpansionFactorWhileEscaping >= utf8Value.Length);

                int length = checked(2 + JsonWriterHelper.GetMaxEscapedLength(utf8Value.Length, firstByteToEscape));
                byte[]? rented = null;

                try
                {
                    scoped Span<byte> escapedValue;

                    if (length > JsonConstants.StackallocByteThreshold)
                    {
                        rented = ArrayPool<byte>.Shared.Rent(length);
                        escapedValue = rented;
                    }
                    else
                    {
                        escapedValue = stackalloc byte[JsonConstants.StackallocByteThreshold];
                    }

                    escapedValue[0] = JsonConstants.Quote;
                    JsonWriterHelper.EscapeString(utf8Value, escapedValue.Slice(1), firstByteToEscape, out int written); // SharpOS: no JavaScriptEncoder
                    escapedValue[1 + written] = JsonConstants.Quote;

                    return writeCallback(escapedValue.Slice(0, written + 2));
                }
                finally
                {
                    if (rented != null)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }
        }
    }
}
