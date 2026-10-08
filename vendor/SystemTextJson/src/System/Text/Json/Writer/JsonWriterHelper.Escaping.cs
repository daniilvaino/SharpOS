// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
// SharpOS cut: using System.Text.Encodings.Web (not ported; default escaping is built in, see JsonWriterHelper.Escaping.cs).

#if !NET
using System.Runtime.CompilerServices;
#endif

namespace System.Text.Json
{
    internal static partial class JsonWriterHelper
    {
        // Only allow ASCII characters between ' ' (0x20) and '~' (0x7E), inclusively,
        // but exclude characters that need to be escaped as hex: '"', '\'', '&', '+', '<', '>', '`'
        // and exclude characters that need to be escaped by adding a backslash: '\n', '\r', '\t', '\\', '\b', '\f'
        //
        // non-zero = allowed, 0 = disallowed
        public const int LastAsciiCharacter = 0x7F;
        private static ReadOnlySpan<byte> AllowList => // byte.MaxValue + 1
        [
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // U+0000..U+000F
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // U+0010..U+001F
            1, 1, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 1, 1, 1, 1, // U+0020..U+002F
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 0, 1, // U+0030..U+003F
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // U+0040..U+004F
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, // U+0050..U+005F
            0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // U+0060..U+006F
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, // U+0070..U+007F

            // Also include the ranges from U+0080 to U+00FF for performance to avoid UTF8 code from checking boundary.
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // U+00F0..U+00FF
        ];

#if NET
        private const string HexFormatString = "X4";
#endif

        private static readonly StandardFormat s_hexStandardFormat = new StandardFormat('X', 4);

        private static bool NeedsEscaping(byte value) => AllowList[value] == 0;

        private static bool NeedsEscapingNoBoundsCheck(char value) => AllowList[value] == 0;

        public static int NeedsEscaping(ReadOnlySpan<byte> value) // SharpOS: no JavaScriptEncoder
        {
            // SharpOS: upstream asks (encoder ?? JavaScriptEncoder.Default).FindFirstCharacterToEncodeUtf8.
            // The default encoder allows Basic Latin only, minus AllowList's zeros, so the first byte
            // to encode is the first disallowed ASCII byte or the first non-ASCII byte.
            for (int i = 0; i < value.Length; i++)
            {
                byte b = value[i];
                if (!IsAsciiValue(b) || NeedsEscaping(b))
                {
                    return i;
                }
            }

            return -1;
        }

        public static int NeedsEscaping(ReadOnlySpan<char> value) // SharpOS: no JavaScriptEncoder
        {
            // SharpOS: upstream calls (encoder ?? JavaScriptEncoder.Default).FindFirstCharacterToEncode;
            // same rule as the UTF-8 overload above.
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!IsAsciiValue(c) || NeedsEscapingNoBoundsCheck(c))
                {
                    return i;
                }
            }

            return -1;
        }

        public static int GetMaxEscapedLength(int textLength, int firstIndexToEscape)
        {
            Debug.Assert(textLength > 0);
            Debug.Assert(firstIndexToEscape >= 0 && firstIndexToEscape < textLength);
            return firstIndexToEscape + JsonConstants.MaxExpansionFactorWhileEscaping * (textLength - firstIndexToEscape);
        }

        // SharpOS cut: EscapeString(..., JavaScriptEncoder encoder, ...) - replaced by EscapeStringDefault below.

        public static void EscapeString(ReadOnlySpan<byte> value, Span<byte> destination, int indexOfFirstByteToEscape, out int written) // SharpOS: no JavaScriptEncoder
            => EscapeString(value, destination, indexOfFirstByteToEscape, out _, out written, isFinalBlock: true); // SharpOS: no JavaScriptEncoder

        public static void EscapeString(ReadOnlySpan<byte> value, Span<byte> destination, int indexOfFirstByteToEscape, out int consumed, out int written, bool isFinalBlock = true) // SharpOS: no JavaScriptEncoder
        {
            Debug.Assert(indexOfFirstByteToEscape >= 0 && indexOfFirstByteToEscape < value.Length);

            value.Slice(0, indexOfFirstByteToEscape).CopyTo(destination);
            written = indexOfFirstByteToEscape;
            consumed = indexOfFirstByteToEscape;

            // SharpOS cut: the custom-encoder branch (if (encoder != null) ...); no JavaScriptEncoder.
            {
                // For performance when no encoder is specified, perform escaping here for Ascii and on the
                // first occurrence of a non-Ascii character, then call into the default encoder.
                while (indexOfFirstByteToEscape < value.Length)
                {
                    byte val = value[indexOfFirstByteToEscape];
                    if (IsAsciiValue(val))
                    {
                        if (NeedsEscaping(val))
                        {
                            EscapeNextBytes(val, destination, ref written);
                            indexOfFirstByteToEscape++;
                            consumed++;
                        }
                        else
                        {
                            destination[written] = val;
                            written++;
                            indexOfFirstByteToEscape++;
                            consumed++;
                        }
                    }
                    else
                    {
                        // Fall back to default encoder.
                        destination = destination.Slice(written);
                        value = value.Slice(indexOfFirstByteToEscape);
                        EscapeStringDefault(value, destination, ref consumed, ref written, isFinalBlock); // SharpOS: was EscapeString(..., JavaScriptEncoder.Default, ...)
                        break;
                    }
                }
            }
        }

        private static void EscapeNextBytes(byte value, Span<byte> destination, ref int written)
        {
            destination[written++] = (byte)'\\';
            switch (value)
            {
                case JsonConstants.Quote:
                    // Optimize for the common quote case.
                    destination[written++] = (byte)'u';
                    destination[written++] = (byte)'0';
                    destination[written++] = (byte)'0';
                    destination[written++] = (byte)'2';
                    destination[written++] = (byte)'2';
                    break;
                case JsonConstants.LineFeed:
                    destination[written++] = (byte)'n';
                    break;
                case JsonConstants.CarriageReturn:
                    destination[written++] = (byte)'r';
                    break;
                case JsonConstants.Tab:
                    destination[written++] = (byte)'t';
                    break;
                case JsonConstants.BackSlash:
                    destination[written++] = (byte)'\\';
                    break;
                case JsonConstants.BackSpace:
                    destination[written++] = (byte)'b';
                    break;
                case JsonConstants.FormFeed:
                    destination[written++] = (byte)'f';
                    break;
                default:
                    destination[written++] = (byte)'u';

                    bool result = Utf8Formatter.TryFormat(value, destination.Slice(written), out int bytesWritten, format: s_hexStandardFormat);
                    Debug.Assert(result);
                    Debug.Assert(bytesWritten == 4);
                    written += bytesWritten;
                    break;
            }
        }

        private static bool IsAsciiValue(byte value) => value <= LastAsciiCharacter;

        private static bool IsAsciiValue(char value) => value <= LastAsciiCharacter;

        // SharpOS cut: EscapeString(..., JavaScriptEncoder encoder, ...) - replaced by EscapeStringDefault below.

        public static void EscapeString(ReadOnlySpan<char> value, Span<char> destination, int indexOfFirstByteToEscape, out int written) // SharpOS: no JavaScriptEncoder
            => EscapeString(value, destination, indexOfFirstByteToEscape, out _, out written, isFinalBlock: true); // SharpOS: no JavaScriptEncoder

        public static void EscapeString(ReadOnlySpan<char> value, Span<char> destination, int indexOfFirstByteToEscape, out int consumed, out int written, bool isFinalBlock = true) // SharpOS: no JavaScriptEncoder
        {
            Debug.Assert(indexOfFirstByteToEscape >= 0 && indexOfFirstByteToEscape < value.Length);

            value.Slice(0, indexOfFirstByteToEscape).CopyTo(destination);
            written = indexOfFirstByteToEscape;
            consumed = indexOfFirstByteToEscape;

            // SharpOS cut: the custom-encoder branch (if (encoder != null) ...); no JavaScriptEncoder.
            {
                // For performance when no encoder is specified, perform escaping here for Ascii and on the
                // first occurrence of a non-Ascii character, then call into the default encoder.
                while (indexOfFirstByteToEscape < value.Length)
                {
                    char val = value[indexOfFirstByteToEscape];
                    if (IsAsciiValue(val))
                    {
                        if (NeedsEscapingNoBoundsCheck(val))
                        {
                            EscapeNextChars(val, destination, ref written);
                            indexOfFirstByteToEscape++;
                            consumed++;
                        }
                        else
                        {
                            destination[written] = val;
                            written++;
                            indexOfFirstByteToEscape++;
                            consumed++;
                        }
                    }
                    else
                    {
                        // Fall back to default encoder.
                        destination = destination.Slice(written);
                        value = value.Slice(indexOfFirstByteToEscape);
                        EscapeStringDefault(value, destination, ref consumed, ref written, isFinalBlock); // SharpOS: was EscapeString(..., JavaScriptEncoder.Default, ...)
                        break;
                    }
                }
            }
        }

        private static void EscapeNextChars(char value, Span<char> destination, ref int written)
        {
            Debug.Assert(IsAsciiValue(value));

            destination[written++] = '\\';
            switch ((byte)value)
            {
                case JsonConstants.Quote:
                    // Optimize for the common quote case.
                    destination[written++] = 'u';
                    destination[written++] = '0';
                    destination[written++] = '0';
                    destination[written++] = '2';
                    destination[written++] = '2';
                    break;
                case JsonConstants.LineFeed:
                    destination[written++] = 'n';
                    break;
                case JsonConstants.CarriageReturn:
                    destination[written++] = 'r';
                    break;
                case JsonConstants.Tab:
                    destination[written++] = 't';
                    break;
                case JsonConstants.BackSlash:
                    destination[written++] = '\\';
                    break;
                case JsonConstants.BackSpace:
                    destination[written++] = 'b';
                    break;
                case JsonConstants.FormFeed:
                    destination[written++] = 'f';
                    break;
                default:
                    destination[written++] = 'u';
#if NET
                    int intChar = value;
                    intChar.TryFormat(destination.Slice(written), out int charsWritten, HexFormatString);
                    Debug.Assert(charsWritten == 4);
                    written += charsWritten;
#else
                    written = WriteHex(value, destination, written);
#endif
                    break;
            }
        }

        // SharpOS: what JavaScriptEncoder.Default does with the rest of the text once the first
        // non-ASCII unit is reached (System.Text.Encodings.Web OptimizedInboxTextEncoder with
        // the Basic Latin allow-list): ASCII through AllowList / EscapeNextBytes, every other
        // scalar as \uXXXX (upper-case hex, astral scalars as a surrogate pair \uXXXX\uYYYY),
        // ill-formed input as \uFFFD. A truncated sequence at the end of a non-final block is
        // left unconsumed (NeedMoreData), as the encoder does.
        private static void EscapeStringDefault(ReadOnlySpan<byte> value, Span<byte> destination, ref int consumed, ref int written, bool isFinalBlock)
        {
            int srcIdx = 0;
            int dstIdx = 0;

            while (srcIdx < value.Length)
            {
                byte b = value[srcIdx];
                if (IsAsciiValue(b))
                {
                    if (NeedsEscaping(b))
                    {
                        EscapeNextBytes(b, destination, ref dstIdx);
                    }
                    else
                    {
                        destination[dstIdx++] = b;
                    }

                    srcIdx++;
                    continue;
                }

                OperationStatus status = DecodeUtf8Scalar(value.Slice(srcIdx), out uint scalar, out int bytesConsumed);
                if (status == OperationStatus.NeedMoreData && !isFinalBlock)
                {
                    break;
                }

                if (status != OperationStatus.Done)
                {
                    scalar = 0xFFFD;
                }

                dstIdx += WriteEscapedScalar(scalar, destination.Slice(dstIdx));
                srcIdx += bytesConsumed;
            }

            written += dstIdx;
            consumed += srcIdx;
        }

        private static void EscapeStringDefault(ReadOnlySpan<char> value, Span<char> destination, ref int consumed, ref int written, bool isFinalBlock)
        {
            int srcIdx = 0;
            int dstIdx = 0;

            while (srcIdx < value.Length)
            {
                char c = value[srcIdx];
                if (IsAsciiValue(c))
                {
                    if (NeedsEscapingNoBoundsCheck(c))
                    {
                        EscapeNextChars(c, destination, ref dstIdx);
                    }
                    else
                    {
                        destination[dstIdx++] = c;
                    }

                    srcIdx++;
                    continue;
                }

                if (char.IsHighSurrogate(c))
                {
                    if (srcIdx + 1 < value.Length && char.IsLowSurrogate(value[srcIdx + 1]))
                    {
                        WriteEscapedUtf16(c, destination, ref dstIdx);
                        WriteEscapedUtf16(value[srcIdx + 1], destination, ref dstIdx);
                        srcIdx += 2;
                        continue;
                    }

                    if (srcIdx + 1 == value.Length && !isFinalBlock)
                    {
                        break;
                    }

                    c = (char)0xFFFD;
                }
                else if (char.IsLowSurrogate(c))
                {
                    c = (char)0xFFFD;
                }

                WriteEscapedUtf16(c, destination, ref dstIdx);
                srcIdx++;
            }

            written += dstIdx;
            consumed += srcIdx;
        }

        private static int WriteEscapedScalar(uint scalar, Span<byte> destination)
        {
            if (scalar < 0x10000)
            {
                WriteEscapedUtf16Unit(scalar, destination);
                return 6;
            }

            uint high = ((scalar - 0x10000) >> 10) + 0xD800;
            uint low = ((scalar - 0x10000) & 0x3FF) + 0xDC00;
            WriteEscapedUtf16Unit(high, destination);
            WriteEscapedUtf16Unit(low, destination.Slice(6));
            return 12;
        }

        private static void WriteEscapedUtf16Unit(uint unit, Span<byte> destination)
        {
            destination[0] = (byte)'\\';
            destination[1] = (byte)'u';
            destination[2] = (byte)HexConverter.ToCharUpper((int)(unit >> 12));
            destination[3] = (byte)HexConverter.ToCharUpper((int)(unit >> 8));
            destination[4] = (byte)HexConverter.ToCharUpper((int)(unit >> 4));
            destination[5] = (byte)HexConverter.ToCharUpper((int)unit);
        }

        private static void WriteEscapedUtf16(char unit, Span<char> destination, ref int written)
        {
            destination[written++] = '\\';
            destination[written++] = 'u';
            destination[written++] = HexConverter.ToCharUpper(unit >> 12);
            destination[written++] = HexConverter.ToCharUpper(unit >> 8);
            destination[written++] = HexConverter.ToCharUpper(unit >> 4);
            destination[written++] = HexConverter.ToCharUpper(unit);
        }

        // SharpOS: Rune.DecodeFromUtf8 semantics (std has no System.Text.Rune): Done with the
        // scalar; InvalidData consuming the maximal invalid subpart (at least one byte);
        // NeedMoreData when the input ends inside an otherwise valid sequence.
        internal static OperationStatus DecodeUtf8Scalar(ReadOnlySpan<byte> source, out uint scalar, out int bytesConsumed)
        {
            scalar = 0;
            bytesConsumed = 0;
            if (source.IsEmpty)
            {
                return OperationStatus.NeedMoreData;
            }

            uint b0 = source[0];
            if (b0 < 0x80)
            {
                scalar = b0;
                bytesConsumed = 1;
                return OperationStatus.Done;
            }

            int needed;
            uint min;
            uint lowerBound = 0x80;
            uint upperBound = 0xBF;
            if (b0 >= 0xC2 && b0 <= 0xDF) { needed = 1; scalar = b0 & 0x1F; min = 0x80; }
            else if (b0 >= 0xE0 && b0 <= 0xEF)
            {
                needed = 2; scalar = b0 & 0x0F; min = 0x800;
                if (b0 == 0xE0) lowerBound = 0xA0;
                if (b0 == 0xED) upperBound = 0x9F;
            }
            else if (b0 >= 0xF0 && b0 <= 0xF4)
            {
                needed = 3; scalar = b0 & 0x07; min = 0x10000;
                if (b0 == 0xF0) lowerBound = 0x90;
                if (b0 == 0xF4) upperBound = 0x8F;
            }
            else
            {
                bytesConsumed = 1;
                return OperationStatus.InvalidData;
            }

            for (int i = 1; i <= needed; i++)
            {
                if (i >= source.Length)
                {
                    bytesConsumed = i;
                    return OperationStatus.NeedMoreData;
                }

                uint b = source[i];
                uint lo = i == 1 ? lowerBound : 0x80;
                uint hi = i == 1 ? upperBound : 0xBF;
                if (b < lo || b > hi)
                {
                    bytesConsumed = i;
                    return OperationStatus.InvalidData;
                }

                scalar = (scalar << 6) | (b & 0x3F);
            }

            System.Diagnostics.Debug.Assert(scalar >= min);
            bytesConsumed = needed + 1;
            return OperationStatus.Done;
        }

#if !NET
        private static int WriteHex(int value, Span<char> destination, int written)
        {
            destination[written++] = HexConverter.ToCharUpper(value >> 12);
            destination[written++] = HexConverter.ToCharUpper(value >> 8);
            destination[written++] = HexConverter.ToCharUpper(value >> 4);
            destination[written++] = HexConverter.ToCharUpper(value);
            return written;
        }
#endif
    }
}
