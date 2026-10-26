// Ported from dotnet/runtime release/7.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Number.Parsing.cs
// (the CoreLib snapshot in gc-experiment/dotnet-runtime). The integer half that
// Number.Parsing.cs left out: TryNumberToInt32/Int64/UInt32/UInt64,
// ParseInt32/Int64/UInt32/UInt64, TryParse{Int32,Int64,UInt32,UInt64}
// with their Number / IntegerStyle / HexNumberStyle paths. Bodies verbatim.
// Behind the NumberStyles/IFormatProvider overloads of int/long/uint/ulong
// Parse and TryParse (Number/Primitives.Parsing.Integer.cs).
//
// SharpOS cuts:
//   * Int128/UInt128 parsers: no Int128 in std.
//   * ThrowOverflowOrFormatException loses its TypeCode argument (as in
//     Number.Parsing.cs): the overflow message is the generic one.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    internal static partial class Number
    {
        // SharpOS: Int32Precision / UInt32Precision / Int64Precision / UInt64Precision live in
        // Number.Formatting.cs here (upstream declares them at this spot).

        private static unsafe bool TryNumberToInt32(ref NumberBuffer number, ref int value)
        {
            number.CheckConsistency();

            int i = number.Scale;
            if (i > Int32Precision || i < number.DigitsCount)
            {
                return false;
            }
            byte* p = number.GetDigitsPointer();
            Debug.Assert(p != null);
            int n = 0;
            while (--i >= 0)
            {
                if ((uint)n > (0x7FFFFFFF / 10))
                {
                    return false;
                }
                n *= 10;
                if (*p != '\0')
                {
                    n += (*p++ - '0');
                }
            }
            if (number.IsNegative)
            {
                n = -n;
                if (n > 0)
                {
                    return false;
                }
            }
            else
            {
                if (n < 0)
                {
                    return false;
                }
            }
            value = n;
            return true;
        }

        private static unsafe bool TryNumberToInt64(ref NumberBuffer number, ref long value)
        {
            number.CheckConsistency();

            int i = number.Scale;
            if (i > Int64Precision || i < number.DigitsCount)
            {
                return false;
            }
            byte* p = number.GetDigitsPointer();
            Debug.Assert(p != null);
            long n = 0;
            while (--i >= 0)
            {
                if ((ulong)n > (0x7FFFFFFFFFFFFFFF / 10))
                {
                    return false;
                }
                n *= 10;
                if (*p != '\0')
                {
                    n += (*p++ - '0');
                }
            }
            if (number.IsNegative)
            {
                n = -n;
                if (n > 0)
                {
                    return false;
                }
            }
            else
            {
                if (n < 0)
                {
                    return false;
                }
            }
            value = n;
            return true;
        }

        private static unsafe bool TryNumberToUInt32(ref NumberBuffer number, ref uint value)
        {
            number.CheckConsistency();

            int i = number.Scale;
            if (i > UInt32Precision || i < number.DigitsCount || number.IsNegative)
            {
                return false;
            }
            byte* p = number.GetDigitsPointer();
            Debug.Assert(p != null);
            uint n = 0;
            while (--i >= 0)
            {
                if (n > (0xFFFFFFFF / 10))
                {
                    return false;
                }
                n *= 10;
                if (*p != '\0')
                {
                    uint newN = n + (uint)(*p++ - '0');
                    // Detect an overflow here...
                    if (newN < n)
                    {
                        return false;
                    }
                    n = newN;
                }
            }
            value = n;
            return true;
        }

        private static unsafe bool TryNumberToUInt64(ref NumberBuffer number, ref ulong value)
        {
            number.CheckConsistency();

            int i = number.Scale;
            if (i > UInt64Precision || i < number.DigitsCount || number.IsNegative)
            {
                return false;
            }
            byte* p = number.GetDigitsPointer();
            Debug.Assert(p != null);
            ulong n = 0;
            while (--i >= 0)
            {
                if (n > (0xFFFFFFFFFFFFFFFF / 10))
                {
                    return false;
                }
                n *= 10;
                if (*p != '\0')
                {
                    ulong newN = n + (ulong)(*p++ - '0');
                    // Detect an overflow here...
                    if (newN < n)
                    {
                        return false;
                    }
                    n = newN;
                }
            }
            value = n;
            return true;
        }

        internal static int ParseInt32(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info)
        {
            ParsingStatus status = TryParseInt32(value, styles, info, out int result);
            if (status != ParsingStatus.OK)
            {
                ThrowOverflowOrFormatException(status, value); // SharpOS: was (status, value, TypeCode.Int32) - see the file header
            }

            return result;
        }

        internal static long ParseInt64(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info)
        {
            ParsingStatus status = TryParseInt64(value, styles, info, out long result);
            if (status != ParsingStatus.OK)
            {
                ThrowOverflowOrFormatException(status, value); // SharpOS: was (status, value, TypeCode.Int64) - see the file header
            }

            return result;
        }

        internal static uint ParseUInt32(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info)
        {
            ParsingStatus status = TryParseUInt32(value, styles, info, out uint result);
            if (status != ParsingStatus.OK)
            {
                ThrowOverflowOrFormatException(status, value); // SharpOS: was (status, value, TypeCode.UInt32) - see the file header
            }

            return result;
        }

        internal static ulong ParseUInt64(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info)
        {
            ParsingStatus status = TryParseUInt64(value, styles, info, out ulong result);
            if (status != ParsingStatus.OK)
            {
                ThrowOverflowOrFormatException(status, value); // SharpOS: was (status, value, TypeCode.UInt64) - see the file header
            }

            return result;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ParsingStatus TryParseInt32(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out int result)
        {
            if ((styles & ~NumberStyles.Integer) == 0)
            {
                // Optimized path for the common case of anything that's allowed for integer style.
                return TryParseInt32IntegerStyle(value, styles, info, out result);
            }

            if ((styles & NumberStyles.AllowHexSpecifier) != 0)
            {
                result = 0;
                return TryParseUInt32HexNumberStyle(value, styles, out Unsafe.As<int, uint>(ref result));
            }

            return TryParseInt32Number(value, styles, info, out result);
        }

        private static unsafe ParsingStatus TryParseInt32Number(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out int result)
        {
            result = 0;
            NumberBuffer number = new NumberBuffer(NumberBufferKind.Integer, stackalloc byte[Int32NumberBufferLength]);

            if (!TryStringToNumber(value, styles, ref number, info))
            {
                return ParsingStatus.Failed;
            }

            if (!TryNumberToInt32(ref number, ref result))
            {
                return ParsingStatus.Overflow;
            }

            return ParsingStatus.OK;
        }

        /// <summary>Parses int limited to styles that make up NumberStyles.Integer.</summary>
        internal static ParsingStatus TryParseInt32IntegerStyle(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out int result)
        {
            Debug.Assert((styles & ~NumberStyles.Integer) == 0, "Only handles subsets of Integer format");

            if (value.IsEmpty)
                goto FalseExit;

            int index = 0;
            int num = value[0];

            // Skip past any whitespace at the beginning.
            if ((styles & NumberStyles.AllowLeadingWhite) != 0 && IsWhite(num))
            {
                do
                {
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto FalseExit;
                    num = value[index];
                }
                while (IsWhite(num));
            }

            // Parse leading sign.
            int sign = 1;
            if ((styles & NumberStyles.AllowLeadingSign) != 0)
            {
                if (info.HasInvariantNumberSigns)
                {
                    if (num == '-')
                    {
                        sign = -1;
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                    else if (num == '+')
                    {
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                }
                else if (info.AllowHyphenDuringParsing && num == '-')
                {
                    sign = -1;
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto FalseExit;
                    num = value[index];
                }
                else
                {
                    value = value.Slice(index);
                    index = 0;
                    string positiveSign = info.PositiveSign, negativeSign = info.NegativeSign;
                    if (!string.IsNullOrEmpty(positiveSign) && value.StartsWith(positiveSign))
                    {
                        index += positiveSign.Length;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                    else if (!string.IsNullOrEmpty(negativeSign) && value.StartsWith(negativeSign))
                    {
                        sign = -1;
                        index += negativeSign.Length;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                }
            }

            bool overflow = false;
            int answer = 0;

            if (IsDigit(num))
            {
                // Skip past leading zeros.
                if (num == '0')
                {
                    do
                    {
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto DoneAtEnd;
                        num = value[index];
                    } while (num == '0');
                    if (!IsDigit(num))
                        goto HasTrailingChars;
                }

                // Parse most digits, up to the potential for overflow, which can't happen until after 9 digits.
                answer = num - '0'; // first digit
                index++;
                for (int i = 0; i < 8; i++) // next 8 digits can't overflow
                {
                    if ((uint)index >= (uint)value.Length)
                        goto DoneAtEnd;
                    num = value[index];
                    if (!IsDigit(num))
                        goto HasTrailingChars;
                    index++;
                    answer = 10 * answer + num - '0';
                }

                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEnd;
                num = value[index];
                if (!IsDigit(num))
                    goto HasTrailingChars;
                index++;
                // Potential overflow now processing the 10th digit.
                overflow = answer > int.MaxValue / 10;
                answer = answer * 10 + num - '0';
                overflow |= (uint)answer > int.MaxValue + (((uint)sign) >> 31);
                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEndButPotentialOverflow;

                // At this point, we're either overflowing or hitting a formatting error.
                // Format errors take precedence for compatibility.
                num = value[index];
                while (IsDigit(num))
                {
                    overflow = true;
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto OverflowExit;
                    num = value[index];
                }
                goto HasTrailingChars;
            }
            goto FalseExit;

        DoneAtEndButPotentialOverflow:
            if (overflow)
            {
                goto OverflowExit;
            }
        DoneAtEnd:
            result = answer * sign;
            ParsingStatus status = ParsingStatus.OK;
        Exit:
            return status;

        FalseExit: // parsing failed
            result = 0;
            status = ParsingStatus.Failed;
            goto Exit;
        OverflowExit:
            result = 0;
            status = ParsingStatus.Overflow;
            goto Exit;

        HasTrailingChars: // we've successfully parsed, but there are still remaining characters in the span
            // Skip past trailing whitespace, then past trailing zeros, and if anything else remains, fail.
            if (IsWhite(num))
            {
                if ((styles & NumberStyles.AllowTrailingWhite) == 0)
                    goto FalseExit;
                for (index++; index < value.Length; index++)
                {
                    if (!IsWhite(value[index]))
                        break;
                }
                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEndButPotentialOverflow;
            }

            if (!TrailingZeros(value, index))
                goto FalseExit;

            goto DoneAtEndButPotentialOverflow;
        }

        /// <summary>Parses long inputs limited to styles that make up NumberStyles.Integer.</summary>
        internal static ParsingStatus TryParseInt64IntegerStyle(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out long result)
        {
            Debug.Assert((styles & ~NumberStyles.Integer) == 0, "Only handles subsets of Integer format");

            if (value.IsEmpty)
                goto FalseExit;

            int index = 0;
            int num = value[0];

            // Skip past any whitespace at the beginning.
            if ((styles & NumberStyles.AllowLeadingWhite) != 0 && IsWhite(num))
            {
                do
                {
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto FalseExit;
                    num = value[index];
                }
                while (IsWhite(num));
            }

            // Parse leading sign.
            int sign = 1;
            if ((styles & NumberStyles.AllowLeadingSign) != 0)
            {
                if (info.HasInvariantNumberSigns)
                {
                    if (num == '-')
                    {
                        sign = -1;
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                    else if (num == '+')
                    {
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                }
                else if (info.AllowHyphenDuringParsing && num == '-')
                {
                    sign = -1;
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto FalseExit;
                    num = value[index];
                }
                else
                {
                    value = value.Slice(index);
                    index = 0;
                    string positiveSign = info.PositiveSign, negativeSign = info.NegativeSign;
                    if (!string.IsNullOrEmpty(positiveSign) && value.StartsWith(positiveSign))
                    {
                        index += positiveSign.Length;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                    else if (!string.IsNullOrEmpty(negativeSign) && value.StartsWith(negativeSign))
                    {
                        sign = -1;
                        index += negativeSign.Length;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                }
            }

            bool overflow = false;
            long answer = 0;

            if (IsDigit(num))
            {
                // Skip past leading zeros.
                if (num == '0')
                {
                    do
                    {
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto DoneAtEnd;
                        num = value[index];
                    } while (num == '0');
                    if (!IsDigit(num))
                        goto HasTrailingChars;
                }

                // Parse most digits, up to the potential for overflow, which can't happen until after 18 digits.
                answer = num - '0'; // first digit
                index++;
                for (int i = 0; i < 17; i++) // next 17 digits can't overflow
                {
                    if ((uint)index >= (uint)value.Length)
                        goto DoneAtEnd;
                    num = value[index];
                    if (!IsDigit(num))
                        goto HasTrailingChars;
                    index++;
                    answer = 10 * answer + num - '0';
                }

                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEnd;
                num = value[index];
                if (!IsDigit(num))
                    goto HasTrailingChars;
                index++;
                // Potential overflow now processing the 19th digit.
                overflow = answer > long.MaxValue / 10;
                answer = answer * 10 + num - '0';
                overflow |= (ulong)answer > (ulong)long.MaxValue + (((uint)sign) >> 31);
                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEndButPotentialOverflow;

                // At this point, we're either overflowing or hitting a formatting error.
                // Format errors take precedence for compatibility.
                num = value[index];
                while (IsDigit(num))
                {
                    overflow = true;
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto OverflowExit;
                    num = value[index];
                }
                goto HasTrailingChars;
            }
            goto FalseExit;

        DoneAtEndButPotentialOverflow:
            if (overflow)
            {
                goto OverflowExit;
            }
        DoneAtEnd:
            result = answer * sign;
            ParsingStatus status = ParsingStatus.OK;
        Exit:
            return status;

        FalseExit: // parsing failed
            result = 0;
            status = ParsingStatus.Failed;
            goto Exit;
        OverflowExit:
            result = 0;
            status = ParsingStatus.Overflow;
            goto Exit;

        HasTrailingChars: // we've successfully parsed, but there are still remaining characters in the span
            // Skip past trailing whitespace, then past trailing zeros, and if anything else remains, fail.
            if (IsWhite(num))
            {
                if ((styles & NumberStyles.AllowTrailingWhite) == 0)
                    goto FalseExit;
                for (index++; index < value.Length; index++)
                {
                    if (!IsWhite(value[index]))
                        break;
                }
                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEndButPotentialOverflow;
            }

            if (!TrailingZeros(value, index))
                goto FalseExit;

            goto DoneAtEndButPotentialOverflow;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ParsingStatus TryParseInt64(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out long result)
        {
            if ((styles & ~NumberStyles.Integer) == 0)
            {
                // Optimized path for the common case of anything that's allowed for integer style.
                return TryParseInt64IntegerStyle(value, styles, info, out result);
            }

            if ((styles & NumberStyles.AllowHexSpecifier) != 0)
            {
                result = 0;
                return TryParseUInt64HexNumberStyle(value, styles, out Unsafe.As<long, ulong>(ref result));
            }

            return TryParseInt64Number(value, styles, info, out result);
        }

        private static unsafe ParsingStatus TryParseInt64Number(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out long result)
        {
            result = 0;
            NumberBuffer number = new NumberBuffer(NumberBufferKind.Integer, stackalloc byte[Int64NumberBufferLength]);

            if (!TryStringToNumber(value, styles, ref number, info))
            {
                return ParsingStatus.Failed;
            }

            if (!TryNumberToInt64(ref number, ref result))
            {
                return ParsingStatus.Overflow;
            }

            return ParsingStatus.OK;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ParsingStatus TryParseUInt32(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out uint result)
        {
            if ((styles & ~NumberStyles.Integer) == 0)
            {
                // Optimized path for the common case of anything that's allowed for integer style.
                return TryParseUInt32IntegerStyle(value, styles, info, out result);
            }

            if ((styles & NumberStyles.AllowHexSpecifier) != 0)
            {
                return TryParseUInt32HexNumberStyle(value, styles, out result);
            }

            return TryParseUInt32Number(value, styles, info, out result);
        }

        private static unsafe ParsingStatus TryParseUInt32Number(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out uint result)
        {
            result = 0;
            NumberBuffer number = new NumberBuffer(NumberBufferKind.Integer, stackalloc byte[UInt32NumberBufferLength]);

            if (!TryStringToNumber(value, styles, ref number, info))
            {
                return ParsingStatus.Failed;
            }

            if (!TryNumberToUInt32(ref number, ref result))
            {
                return ParsingStatus.Overflow;
            }

            return ParsingStatus.OK;
        }

        /// <summary>Parses uint limited to styles that make up NumberStyles.Integer.</summary>
        internal static ParsingStatus TryParseUInt32IntegerStyle(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out uint result)
        {
            Debug.Assert((styles & ~NumberStyles.Integer) == 0, "Only handles subsets of Integer format");

            if (value.IsEmpty)
                goto FalseExit;

            int index = 0;
            int num = value[0];

            // Skip past any whitespace at the beginning.
            if ((styles & NumberStyles.AllowLeadingWhite) != 0 && IsWhite(num))
            {
                do
                {
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto FalseExit;
                    num = value[index];
                }
                while (IsWhite(num));
            }

            // Parse leading sign.
            bool overflow = false;
            if ((styles & NumberStyles.AllowLeadingSign) != 0)
            {
                if (info.HasInvariantNumberSigns)
                {
                    if (num == '+')
                    {
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                    else if (num == '-')
                    {
                        overflow = true;
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                }
                else if (info.AllowHyphenDuringParsing && num == '-')
                {
                    overflow = true;
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto FalseExit;
                    num = value[index];
                }
                else
                {
                    value = value.Slice(index);
                    index = 0;
                    string positiveSign = info.PositiveSign, negativeSign = info.NegativeSign;
                    if (!string.IsNullOrEmpty(positiveSign) && value.StartsWith(positiveSign))
                    {
                        index += positiveSign.Length;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                    else if (!string.IsNullOrEmpty(negativeSign) && value.StartsWith(negativeSign))
                    {
                        overflow = true;
                        index += negativeSign.Length;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                }
            }

            int answer = 0;

            if (IsDigit(num))
            {
                // Skip past leading zeros.
                if (num == '0')
                {
                    do
                    {
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto DoneAtEnd;
                        num = value[index];
                    } while (num == '0');
                    if (!IsDigit(num))
                        goto HasTrailingCharsZero;
                }

                // Parse most digits, up to the potential for overflow, which can't happen until after 9 digits.
                answer = num - '0'; // first digit
                index++;
                for (int i = 0; i < 8; i++) // next 8 digits can't overflow
                {
                    if ((uint)index >= (uint)value.Length)
                        goto DoneAtEndButPotentialOverflow;
                    num = value[index];
                    if (!IsDigit(num))
                        goto HasTrailingChars;
                    index++;
                    answer = 10 * answer + num - '0';
                }

                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEndButPotentialOverflow;
                num = value[index];
                if (!IsDigit(num))
                    goto HasTrailingChars;
                index++;
                // Potential overflow now processing the 10th digit.
                overflow |= (uint)answer > uint.MaxValue / 10 || ((uint)answer == uint.MaxValue / 10 && num > '5');
                answer = answer * 10 + num - '0';
                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEndButPotentialOverflow;

                // At this point, we're either overflowing or hitting a formatting error.
                // Format errors take precedence for compatibility.
                num = value[index];
                while (IsDigit(num))
                {
                    overflow = true;
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto OverflowExit;
                    num = value[index];
                }
                goto HasTrailingChars;
            }
            goto FalseExit;

        DoneAtEndButPotentialOverflow:
            if (overflow)
            {
                goto OverflowExit;
            }
        DoneAtEnd:
            result = (uint)answer;
            ParsingStatus status = ParsingStatus.OK;
        Exit:
            return status;

        FalseExit: // parsing failed
            result = 0;
            status = ParsingStatus.Failed;
            goto Exit;
        OverflowExit:
            result = 0;
            status = ParsingStatus.Overflow;
            goto Exit;

        HasTrailingCharsZero:
            overflow = false;
        HasTrailingChars: // we've successfully parsed, but there are still remaining characters in the span
            // Skip past trailing whitespace, then past trailing zeros, and if anything else remains, fail.
            if (IsWhite(num))
            {
                if ((styles & NumberStyles.AllowTrailingWhite) == 0)
                    goto FalseExit;
                for (index++; index < value.Length; index++)
                {
                    if (!IsWhite(value[index]))
                        break;
                }
                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEndButPotentialOverflow;
            }

            if (!TrailingZeros(value, index))
                goto FalseExit;

            goto DoneAtEndButPotentialOverflow;
        }

        /// <summary>Parses uint limited to styles that make up NumberStyles.HexNumber.</summary>
        internal static ParsingStatus TryParseUInt32HexNumberStyle(ReadOnlySpan<char> value, NumberStyles styles, out uint result)
        {
            Debug.Assert((styles & ~NumberStyles.HexNumber) == 0, "Only handles subsets of HexNumber format");

            if (value.IsEmpty)
                goto FalseExit;

            int index = 0;
            int num = value[0];

            // Skip past any whitespace at the beginning.
            if ((styles & NumberStyles.AllowLeadingWhite) != 0 && IsWhite(num))
            {
                do
                {
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto FalseExit;
                    num = value[index];
                }
                while (IsWhite(num));
            }

            bool overflow = false;
            uint answer = 0;

            if (HexConverter.IsHexChar(num))
            {
                // Skip past leading zeros.
                if (num == '0')
                {
                    do
                    {
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto DoneAtEnd;
                        num = value[index];
                    } while (num == '0');
                    if (!HexConverter.IsHexChar(num))
                        goto HasTrailingChars;
                }

                // Parse up through 8 digits, as no overflow is possible
                answer = (uint)HexConverter.FromChar(num); // first digit
                index++;
                for (int i = 0; i < 7; i++) // next 7 digits can't overflow
                {
                    if ((uint)index >= (uint)value.Length)
                        goto DoneAtEnd;
                    num = value[index];

                    uint numValue = (uint)HexConverter.FromChar(num);
                    if (numValue == 0xFF)
                        goto HasTrailingChars;
                    index++;
                    answer = 16 * answer + numValue;
                }

                // If there's another digit, it's an overflow.
                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEnd;
                num = value[index];
                if (!HexConverter.IsHexChar(num))
                    goto HasTrailingChars;

                // At this point, we're either overflowing or hitting a formatting error.
                // Format errors take precedence for compatibility. Read through any remaining digits.
                do
                {
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto OverflowExit;
                    num = value[index];
                } while (HexConverter.IsHexChar(num));
                overflow = true;
                goto HasTrailingChars;
            }
            goto FalseExit;

        DoneAtEndButPotentialOverflow:
            if (overflow)
            {
                goto OverflowExit;
            }
        DoneAtEnd:
            result = answer;
            ParsingStatus status = ParsingStatus.OK;
        Exit:
            return status;

        FalseExit: // parsing failed
            result = 0;
            status = ParsingStatus.Failed;
            goto Exit;
        OverflowExit:
            result = 0;
            status = ParsingStatus.Overflow;
            goto Exit;

        HasTrailingChars: // we've successfully parsed, but there are still remaining characters in the span
            // Skip past trailing whitespace, then past trailing zeros, and if anything else remains, fail.
            if (IsWhite(num))
            {
                if ((styles & NumberStyles.AllowTrailingWhite) == 0)
                    goto FalseExit;
                for (index++; index < value.Length; index++)
                {
                    if (!IsWhite(value[index]))
                        break;
                }
                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEndButPotentialOverflow;
            }

            if (!TrailingZeros(value, index))
                goto FalseExit;

            goto DoneAtEndButPotentialOverflow;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ParsingStatus TryParseUInt64(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out ulong result)
        {
            if ((styles & ~NumberStyles.Integer) == 0)
            {
                // Optimized path for the common case of anything that's allowed for integer style.
                return TryParseUInt64IntegerStyle(value, styles, info, out result);
            }

            if ((styles & NumberStyles.AllowHexSpecifier) != 0)
            {
                return TryParseUInt64HexNumberStyle(value, styles, out result);
            }

            return TryParseUInt64Number(value, styles, info, out result);
        }

        private static unsafe ParsingStatus TryParseUInt64Number(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out ulong result)
        {
            result = 0;
            NumberBuffer number = new NumberBuffer(NumberBufferKind.Integer, stackalloc byte[UInt64NumberBufferLength]);

            if (!TryStringToNumber(value, styles, ref number, info))
            {
                return ParsingStatus.Failed;
            }

            if (!TryNumberToUInt64(ref number, ref result))
            {
                return ParsingStatus.Overflow;
            }

            return ParsingStatus.OK;
        }

        /// <summary>Parses ulong limited to styles that make up NumberStyles.Integer.</summary>
        internal static ParsingStatus TryParseUInt64IntegerStyle(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out ulong result)
        {
            Debug.Assert((styles & ~NumberStyles.Integer) == 0, "Only handles subsets of Integer format");

            if (value.IsEmpty)
                goto FalseExit;

            int index = 0;
            int num = value[0];

            // Skip past any whitespace at the beginning.
            if ((styles & NumberStyles.AllowLeadingWhite) != 0 && IsWhite(num))
            {
                do
                {
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto FalseExit;
                    num = value[index];
                }
                while (IsWhite(num));
            }

            // Parse leading sign.
            bool overflow = false;
            if ((styles & NumberStyles.AllowLeadingSign) != 0)
            {
                if (info.HasInvariantNumberSigns)
                {
                    if (num == '+')
                    {
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                    else if (num == '-')
                    {
                        overflow = true;
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                }
                else if (info.AllowHyphenDuringParsing && num == '-')
                {
                    overflow = true;
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto FalseExit;
                    num = value[index];
                }
                else
                {
                    value = value.Slice(index);
                    index = 0;
                    string positiveSign = info.PositiveSign, negativeSign = info.NegativeSign;
                    if (!string.IsNullOrEmpty(positiveSign) && value.StartsWith(positiveSign))
                    {
                        index += positiveSign.Length;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                    else if (!string.IsNullOrEmpty(negativeSign) && value.StartsWith(negativeSign))
                    {
                        overflow = true;
                        index += negativeSign.Length;
                        if ((uint)index >= (uint)value.Length)
                            goto FalseExit;
                        num = value[index];
                    }
                }
            }

            long answer = 0;

            if (IsDigit(num))
            {
                // Skip past leading zeros.
                if (num == '0')
                {
                    do
                    {
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto DoneAtEnd;
                        num = value[index];
                    } while (num == '0');
                    if (!IsDigit(num))
                        goto HasTrailingCharsZero;
                }

                // Parse most digits, up to the potential for overflow, which can't happen until after 19 digits.
                answer = num - '0'; // first digit
                index++;
                for (int i = 0; i < 18; i++) // next 18 digits can't overflow
                {
                    if ((uint)index >= (uint)value.Length)
                        goto DoneAtEndButPotentialOverflow;
                    num = value[index];
                    if (!IsDigit(num))
                        goto HasTrailingChars;
                    index++;
                    answer = 10 * answer + num - '0';
                }

                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEndButPotentialOverflow;
                num = value[index];
                if (!IsDigit(num))
                    goto HasTrailingChars;
                index++;
                // Potential overflow now processing the 20th digit.
                overflow |= (ulong)answer > ulong.MaxValue / 10 || ((ulong)answer == ulong.MaxValue / 10 && num > '5');
                answer = answer * 10 + num - '0';
                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEndButPotentialOverflow;

                // At this point, we're either overflowing or hitting a formatting error.
                // Format errors take precedence for compatibility.
                num = value[index];
                while (IsDigit(num))
                {
                    overflow = true;
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto OverflowExit;
                    num = value[index];
                }
                goto HasTrailingChars;
            }
            goto FalseExit;

        DoneAtEndButPotentialOverflow:
            if (overflow)
            {
                goto OverflowExit;
            }
        DoneAtEnd:
            result = (ulong)answer;
            ParsingStatus status = ParsingStatus.OK;
        Exit:
            return status;

        FalseExit: // parsing failed
            result = 0;
            status = ParsingStatus.Failed;
            goto Exit;
        OverflowExit:
            result = 0;
            status = ParsingStatus.Overflow;
            goto Exit;

        HasTrailingCharsZero:
            overflow = false;
        HasTrailingChars: // we've successfully parsed, but there are still remaining characters in the span
            // Skip past trailing whitespace, then past trailing zeros, and if anything else remains, fail.
            if (IsWhite(num))
            {
                if ((styles & NumberStyles.AllowTrailingWhite) == 0)
                    goto FalseExit;
                for (index++; index < value.Length; index++)
                {
                    if (!IsWhite(value[index]))
                        break;
                }
                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEndButPotentialOverflow;
            }

            if (!TrailingZeros(value, index))
                goto FalseExit;

            goto DoneAtEndButPotentialOverflow;
        }

        /// <summary>Parses ulong limited to styles that make up NumberStyles.HexNumber.</summary>
        private static ParsingStatus TryParseUInt64HexNumberStyle(ReadOnlySpan<char> value, NumberStyles styles, out ulong result)
        {
            Debug.Assert((styles & ~NumberStyles.HexNumber) == 0, "Only handles subsets of HexNumber format");

            if (value.IsEmpty)
                goto FalseExit;

            int index = 0;
            int num = value[0];

            // Skip past any whitespace at the beginning.
            if ((styles & NumberStyles.AllowLeadingWhite) != 0 && IsWhite(num))
            {
                do
                {
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto FalseExit;
                    num = value[index];
                }
                while (IsWhite(num));
            }

            bool overflow = false;
            ulong answer = 0;

            if (HexConverter.IsHexChar(num))
            {
                // Skip past leading zeros.
                if (num == '0')
                {
                    do
                    {
                        index++;
                        if ((uint)index >= (uint)value.Length)
                            goto DoneAtEnd;
                        num = value[index];
                    } while (num == '0');
                    if (!HexConverter.IsHexChar(num))
                        goto HasTrailingChars;
                }

                // Parse up through 16 digits, as no overflow is possible
                answer = (uint)HexConverter.FromChar(num); // first digit
                index++;
                for (int i = 0; i < 15; i++) // next 15 digits can't overflow
                {
                    if ((uint)index >= (uint)value.Length)
                        goto DoneAtEnd;
                    num = value[index];

                    uint numValue = (uint)HexConverter.FromChar(num);
                    if (numValue == 0xFF)
                        goto HasTrailingChars;
                    index++;
                    answer = 16 * answer + numValue;
                }

                // If there's another digit, it's an overflow.
                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEnd;
                num = value[index];
                if (!HexConverter.IsHexChar(num))
                    goto HasTrailingChars;

                // At this point, we're either overflowing or hitting a formatting error.
                // Format errors take precedence for compatibility. Read through any remaining digits.
                do
                {
                    index++;
                    if ((uint)index >= (uint)value.Length)
                        goto OverflowExit;
                    num = value[index];
                } while (HexConverter.IsHexChar(num));
                overflow = true;
                goto HasTrailingChars;
            }
            goto FalseExit;

        DoneAtEndButPotentialOverflow:
            if (overflow)
            {
                goto OverflowExit;
            }
        DoneAtEnd:
            result = answer;
            ParsingStatus status = ParsingStatus.OK;
        Exit:
            return status;

        FalseExit: // parsing failed
            result = 0;
            status = ParsingStatus.Failed;
            goto Exit;
        OverflowExit:
            result = 0;
            status = ParsingStatus.Overflow;
            goto Exit;

        HasTrailingChars: // we've successfully parsed, but there are still remaining characters in the span
            // Skip past trailing whitespace, then past trailing zeros, and if anything else remains, fail.
            if (IsWhite(num))
            {
                if ((styles & NumberStyles.AllowTrailingWhite) == 0)
                    goto FalseExit;
                for (index++; index < value.Length; index++)
                {
                    if (!IsWhite(value[index]))
                        break;
                }
                if ((uint)index >= (uint)value.Length)
                    goto DoneAtEndButPotentialOverflow;
            }

            if (!TrailingZeros(value, index))
                goto FalseExit;

            goto DoneAtEndButPotentialOverflow;
        }
    }
}
