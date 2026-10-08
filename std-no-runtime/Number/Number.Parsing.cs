// Ported from dotnet/runtime release/7.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Number.Parsing.cs
// (the CoreLib snapshot in gc-experiment/dotnet-runtime). The floating-point half only: the
// NumberStyles/NumberFormatInfo scanner (TryParseNumber), TryParseDouble /
// TryParseSingle with their NaN/Infinity symbol handling, and NumberToDouble /
// NumberToSingle over Number.NumberToFloatingPointBits.cs. Bodies verbatim.
//
// SharpOS cuts:
//   * The integer parsers (TryParseInt32IntegerStyle & co., Int128/UInt128): the
//     primitives' Parse/TryParse forward to SharpOS.Std.NoRuntime.NumberParsing (see
//     NumberParsing.cs); porting these is a separate job.
//   * Decimal and Half parsing: no System.Decimal / System.Half in std.
//   * GetOverflowException is reduced to the floating-point needs (TypeCode-specific
//     overflow texts belong to the integer parsers above).

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    internal static partial class Number
    {
        private const int DoubleMaxExponent = 309;
        private const int DoubleMinExponent = -324;

        private const int FloatingPointMaxExponent = DoubleMaxExponent;
        private const int FloatingPointMinExponent = DoubleMinExponent;

        private const int SingleMaxExponent = 39;
        private const int SingleMinExponent = -45;

        private static unsafe bool TryParseNumber(scoped ref char* str, char* strEnd, NumberStyles styles, ref NumberBuffer number, NumberFormatInfo info)
        {
            Debug.Assert(str != null);
            Debug.Assert(strEnd != null);
            Debug.Assert(str <= strEnd);
            Debug.Assert((styles & NumberStyles.AllowHexSpecifier) == 0);

            const int StateSign = 0x0001;
            const int StateParens = 0x0002;
            const int StateDigits = 0x0004;
            const int StateNonZero = 0x0008;
            const int StateDecimal = 0x0010;
            const int StateCurrency = 0x0020;

            Debug.Assert(number.DigitsCount == 0);
            Debug.Assert(number.Scale == 0);
            Debug.Assert(!number.IsNegative);
            Debug.Assert(!number.HasNonZeroTail);

            number.CheckConsistency();

            string decSep;                  // decimal separator from NumberFormatInfo.
            string groupSep;                // group separator from NumberFormatInfo.
            string? currSymbol = null;       // currency symbol from NumberFormatInfo.

            bool parsingCurrency = false;
            if ((styles & NumberStyles.AllowCurrencySymbol) != 0)
            {
                currSymbol = info.CurrencySymbol;

                // The idea here is to match the currency separators and on failure match the number separators to keep the perf of VB's IsNumeric fast.
                // The values of decSep are setup to use the correct relevant separator (currency in the if part and decimal in the else part).
                decSep = info.CurrencyDecimalSeparator;
                groupSep = info.CurrencyGroupSeparator;
                parsingCurrency = true;
            }
            else
            {
                decSep = info.NumberDecimalSeparator;
                groupSep = info.NumberGroupSeparator;
            }

            int state = 0;
            char* p = str;
            char ch = p < strEnd ? *p : '\0';
            char* next;

            while (true)
            {
                // Eat whitespace unless we've found a sign which isn't followed by a currency symbol.
                // "-Kr 1231.47" is legal but "- 1231.47" is not.
                if (!IsWhite(ch) || (styles & NumberStyles.AllowLeadingWhite) == 0 || ((state & StateSign) != 0 && ((state & StateCurrency) == 0 && info.NumberNegativePattern != 2)))
                {
                    if ((((styles & NumberStyles.AllowLeadingSign) != 0) && (state & StateSign) == 0) && ((next = MatchChars(p, strEnd, info.PositiveSign)) != null || ((next = MatchNegativeSignChars(p, strEnd, info)) != null && (number.IsNegative = true))))
                    {
                        state |= StateSign;
                        p = next - 1;
                    }
                    else if (ch == '(' && ((styles & NumberStyles.AllowParentheses) != 0) && ((state & StateSign) == 0))
                    {
                        state |= StateSign | StateParens;
                        number.IsNegative = true;
                    }
                    else if (currSymbol != null && (next = MatchChars(p, strEnd, currSymbol)) != null)
                    {
                        state |= StateCurrency;
                        currSymbol = null;
                        // We already found the currency symbol. There should not be more currency symbols. Set
                        // currSymbol to NULL so that we won't search it again in the later code path.
                        p = next - 1;
                    }
                    else
                    {
                        break;
                    }
                }
                ch = ++p < strEnd ? *p : '\0';
            }

            int digCount = 0;
            int digEnd = 0;
            int maxDigCount = number.Digits.Length - 1;
            int numberOfTrailingZeros = 0;

            while (true)
            {
                if (IsDigit(ch))
                {
                    state |= StateDigits;

                    if (ch != '0' || (state & StateNonZero) != 0)
                    {
                        if (digCount < maxDigCount)
                        {
                            number.Digits[digCount] = (byte)(ch);
                            if ((ch != '0') || (number.Kind != NumberBufferKind.Integer))
                            {
                                digEnd = digCount + 1;
                            }
                        }
                        else if (ch != '0')
                        {
                            // For decimal and binary floating-point numbers, we only
                            // need to store digits up to maxDigCount. However, we still
                            // need to keep track of whether any additional digits past
                            // maxDigCount were non-zero, as that can impact rounding
                            // for an input that falls evenly between two representable
                            // results.

                            number.HasNonZeroTail = true;
                        }

                        if ((state & StateDecimal) == 0)
                        {
                            number.Scale++;
                        }

                        if (digCount < maxDigCount)
                        {
                            // Handle a case like "53.0". We need to ignore trailing zeros in the fractional part for floating point numbers, so we keep a count of the number of trailing zeros and update digCount later
                            if (ch == '0')
                            {
                                numberOfTrailingZeros++;
                            }
                            else
                            {
                                numberOfTrailingZeros = 0;
                            }
                        }
                        digCount++;
                        state |= StateNonZero;
                    }
                    else if ((state & StateDecimal) != 0)
                    {
                        number.Scale--;
                    }
                }
                else if (((styles & NumberStyles.AllowDecimalPoint) != 0) && ((state & StateDecimal) == 0) && ((next = MatchChars(p, strEnd, decSep)) != null || (parsingCurrency && (state & StateCurrency) == 0) && (next = MatchChars(p, strEnd, info.NumberDecimalSeparator)) != null))
                {
                    state |= StateDecimal;
                    p = next - 1;
                }
                else if (((styles & NumberStyles.AllowThousands) != 0) && ((state & StateDigits) != 0) && ((state & StateDecimal) == 0) && ((next = MatchChars(p, strEnd, groupSep)) != null || (parsingCurrency && (state & StateCurrency) == 0) && (next = MatchChars(p, strEnd, info.NumberGroupSeparator)) != null))
                {
                    p = next - 1;
                }
                else
                {
                    break;
                }
                ch = ++p < strEnd ? *p : '\0';
            }

            bool negExp = false;
            number.DigitsCount = digEnd;
            number.Digits[digEnd] = (byte)('\0');
            if ((state & StateDigits) != 0)
            {
                if ((ch == 'E' || ch == 'e') && ((styles & NumberStyles.AllowExponent) != 0))
                {
                    char* temp = p;
                    ch = ++p < strEnd ? *p : '\0';
                    if ((next = MatchChars(p, strEnd, info._positiveSign)) != null)
                    {
                        ch = (p = next) < strEnd ? *p : '\0';
                    }
                    else if ((next = MatchNegativeSignChars(p, strEnd, info)) != null)
                    {
                        ch = (p = next) < strEnd ? *p : '\0';
                        negExp = true;
                    }
                    if (IsDigit(ch))
                    {
                        int exp = 0;
                        do
                        {
                            exp = exp * 10 + (ch - '0');
                            ch = ++p < strEnd ? *p : '\0';
                            if (exp > 1000)
                            {
                                exp = 9999;
                                while (IsDigit(ch))
                                {
                                    ch = ++p < strEnd ? *p : '\0';
                                }
                            }
                        } while (IsDigit(ch));
                        if (negExp)
                        {
                            exp = -exp;
                        }
                        number.Scale += exp;
                    }
                    else
                    {
                        p = temp;
                        ch = p < strEnd ? *p : '\0';
                    }
                }

                if (number.Kind == NumberBufferKind.FloatingPoint && !number.HasNonZeroTail)
                {
                    // Adjust the number buffer for trailing zeros
                    int numberOfFractionalDigits = digEnd - number.Scale;
                    if (numberOfFractionalDigits > 0)
                    {
                        numberOfTrailingZeros = Math.Min(numberOfTrailingZeros, numberOfFractionalDigits);
                        Debug.Assert(numberOfTrailingZeros >= 0);
                        number.DigitsCount = digEnd - numberOfTrailingZeros;
                        number.Digits[number.DigitsCount] = (byte)('\0');
                    }
                }

                while (true)
                {
                    if (!IsWhite(ch) || (styles & NumberStyles.AllowTrailingWhite) == 0)
                    {
                        if ((styles & NumberStyles.AllowTrailingSign) != 0 && ((state & StateSign) == 0) && ((next = MatchChars(p, strEnd, info.PositiveSign)) != null || (((next = MatchNegativeSignChars(p, strEnd, info)) != null) && (number.IsNegative = true))))
                        {
                            state |= StateSign;
                            p = next - 1;
                        }
                        else if (ch == ')' && ((state & StateParens) != 0))
                        {
                            state &= ~StateParens;
                        }
                        else if (currSymbol != null && (next = MatchChars(p, strEnd, currSymbol)) != null)
                        {
                            currSymbol = null;
                            p = next - 1;
                        }
                        else
                        {
                            break;
                        }
                    }
                    ch = ++p < strEnd ? *p : '\0';
                }
                if ((state & StateParens) == 0)
                {
                    if ((state & StateNonZero) == 0)
                    {
                        if (number.Kind != NumberBufferKind.Decimal)
                        {
                            number.Scale = 0;
                        }
                        if ((number.Kind == NumberBufferKind.Integer) && (state & StateDecimal) == 0)
                        {
                            number.IsNegative = false;
                        }
                    }
                    str = p;
                    return true;
                }
            }
            str = p;
            return false;
        }

        // SharpOS cut: TryParseInt32IntegerStyle ... TryParseUInt128HexNumberStyle, ParseDecimal,
        // TryNumberToDecimal (see header).

        internal static double ParseDouble(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info)
        {
            if (!TryParseDouble(value, styles, info, out double result))
            {
                ThrowOverflowOrFormatException(ParsingStatus.Failed, value);
            }

            return result;
        }

        internal static float ParseSingle(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info)
        {
            if (!TryParseSingle(value, styles, info, out float result))
            {
                ThrowOverflowOrFormatException(ParsingStatus.Failed, value);
            }

            return result;
        }

        // SharpOS cut: ParseHalf, TryParseDecimal (no System.Half / System.Decimal).

        internal static bool SpanStartsWith(ReadOnlySpan<char> span, char c) => !span.IsEmpty && span[0] == c;

        internal static unsafe bool TryParseDouble(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out double result)
        {
            NumberBuffer number = new NumberBuffer(NumberBufferKind.FloatingPoint, stackalloc byte[DoubleNumberBufferLength]);

            if (!TryStringToNumber(value, styles, ref number, info))
            {
                ReadOnlySpan<char> valueTrim = value.Trim();

                // This code would be simpler if we only had the concept of `InfinitySymbol`, but
                // we don't so we'll check the existing cases first and then handle `PositiveSign` +
                // `PositiveInfinitySymbol` and `PositiveSign/NegativeSign` + `NaNSymbol` last.

                if (valueTrim.EqualsOrdinalIgnoreCase(info.PositiveInfinitySymbol))
                {
                    result = double.PositiveInfinity;
                }
                else if (valueTrim.EqualsOrdinalIgnoreCase(info.NegativeInfinitySymbol))
                {
                    result = double.NegativeInfinity;
                }
                else if (valueTrim.EqualsOrdinalIgnoreCase(info.NaNSymbol))
                {
                    result = double.NaN;
                }
                else if (valueTrim.StartsWith(info.PositiveSign, StringComparison.OrdinalIgnoreCase))
                {
                    valueTrim = valueTrim.Slice(info.PositiveSign.Length);

                    if (valueTrim.EqualsOrdinalIgnoreCase(info.PositiveInfinitySymbol))
                    {
                        result = double.PositiveInfinity;
                    }
                    else if (valueTrim.EqualsOrdinalIgnoreCase(info.NaNSymbol))
                    {
                        result = double.NaN;
                    }
                    else
                    {
                        result = 0;
                        return false;
                    }
                }
                else if ((valueTrim.StartsWith(info.NegativeSign, StringComparison.OrdinalIgnoreCase) && valueTrim.Slice(info.NegativeSign.Length).EqualsOrdinalIgnoreCase(info.NaNSymbol)) ||
                        (info.AllowHyphenDuringParsing && SpanStartsWith(valueTrim, '-') && valueTrim.Slice(1).EqualsOrdinalIgnoreCase(info.NaNSymbol)))
                {
                    result = double.NaN;
                }
                else
                {
                    result = 0;
                    return false; // We really failed
                }
            }
            else
            {
                result = NumberToDouble(ref number);
            }

            return true;
        }

        // SharpOS cut: TryParseHalf (no System.Half).

        internal static unsafe bool TryParseSingle(ReadOnlySpan<char> value, NumberStyles styles, NumberFormatInfo info, out float result)
        {
            NumberBuffer number = new NumberBuffer(NumberBufferKind.FloatingPoint, stackalloc byte[SingleNumberBufferLength]);

            if (!TryStringToNumber(value, styles, ref number, info))
            {
                ReadOnlySpan<char> valueTrim = value.Trim();

                // This code would be simpler if we only had the concept of `InfinitySymbol`, but
                // we don't so we'll check the existing cases first and then handle `PositiveSign` +
                // `PositiveInfinitySymbol` and `PositiveSign/NegativeSign` + `NaNSymbol` last.
                //
                // Additionally, since some cultures ("wo") actually define `PositiveInfinitySymbol`
                // to include `PositiveSign`, we need to check whether `PositiveInfinitySymbol` fits
                // that case so that we don't start parsing things like `++infini`.

                if (valueTrim.EqualsOrdinalIgnoreCase(info.PositiveInfinitySymbol))
                {
                    result = float.PositiveInfinity;
                }
                else if (valueTrim.EqualsOrdinalIgnoreCase(info.NegativeInfinitySymbol))
                {
                    result = float.NegativeInfinity;
                }
                else if (valueTrim.EqualsOrdinalIgnoreCase(info.NaNSymbol))
                {
                    result = float.NaN;
                }
                else if (valueTrim.StartsWith(info.PositiveSign, StringComparison.OrdinalIgnoreCase))
                {
                    valueTrim = valueTrim.Slice(info.PositiveSign.Length);

                    if (!info.PositiveInfinitySymbol.StartsWith(info.PositiveSign, StringComparison.OrdinalIgnoreCase) && valueTrim.EqualsOrdinalIgnoreCase(info.PositiveInfinitySymbol))
                    {
                        result = float.PositiveInfinity;
                    }
                    else if (!info.NaNSymbol.StartsWith(info.PositiveSign, StringComparison.OrdinalIgnoreCase) && valueTrim.EqualsOrdinalIgnoreCase(info.NaNSymbol))
                    {
                        result = float.NaN;
                    }
                    else
                    {
                        result = 0;
                        return false;
                    }
                }
                else if (valueTrim.StartsWith(info.NegativeSign, StringComparison.OrdinalIgnoreCase) &&
                         !info.NaNSymbol.StartsWith(info.NegativeSign, StringComparison.OrdinalIgnoreCase) &&
                         valueTrim.Slice(info.NegativeSign.Length).EqualsOrdinalIgnoreCase(info.NaNSymbol))
                {
                    result = float.NaN;
                }
                else if (info.AllowHyphenDuringParsing && SpanStartsWith(valueTrim, '-') && !info.NaNSymbol.StartsWith(info.NegativeSign, StringComparison.OrdinalIgnoreCase) &&
                         !info.NaNSymbol.StartsWith('-') && valueTrim.Slice(1).EqualsOrdinalIgnoreCase(info.NaNSymbol))
                {
                    result = float.NaN;
                }
                else
                {
                    result = 0;
                    return false; // We really failed
                }
            }
            else
            {
                result = NumberToSingle(ref number);
            }

            return true;
        }

        internal static unsafe bool TryStringToNumber(ReadOnlySpan<char> value, NumberStyles styles, ref NumberBuffer number, NumberFormatInfo info)
        {
            Debug.Assert(info != null);
            fixed (char* stringPointer = &MemoryMarshal.GetReference(value))
            {
                char* p = stringPointer;
                if (!TryParseNumber(ref p, p + value.Length, styles, ref number, info)
                    || ((int)(p - stringPointer) < value.Length && !TrailingZeros(value, (int)(p - stringPointer))))
                {
                    number.CheckConsistency();
                    return false;
                }
            }

            number.CheckConsistency();
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // rare slow path that shouldn't impact perf of the main use case
        private static bool TrailingZeros(ReadOnlySpan<char> value, int index) =>
            // For compatibility, we need to allow trailing zeros at the end of a number string
            value.Slice(index).IndexOfAnyExcept('\0') < 0;

        private static bool IsSpaceReplacingChar(char c) => c == ' ' || c == ' ';

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe char* MatchNegativeSignChars(char* p, char* pEnd, NumberFormatInfo info)
        {
            char* ret = MatchChars(p, pEnd, info.NegativeSign);
            if (ret == null && info.AllowHyphenDuringParsing && p < pEnd && *p == '-')
            {
                ret = p + 1;
            }

            return ret;
        }

        private static unsafe char* MatchChars(char* p, char* pEnd, string value)
        {
            Debug.Assert(p != null && pEnd != null && p <= pEnd && value != null);
            fixed (char* stringPointer = value)
            {
                char* str = stringPointer;
                if (*str != '\0')
                {
                    // We only hurt the failure case
                    // This fix is for French or Kazakh cultures. Since a user cannot type 0xA0 or 0x202F as a
                    // space character we use 0x20 space character instead to mean the same.
                    while (true)
                    {
                        char cp = p < pEnd ? *p : '\0';
                        if (cp != *str && !(IsSpaceReplacingChar(*str) && cp == ' '))
                        {
                            break;
                        }
                        p++;
                        str++;
                        if (*str == '\0')
                            return p;
                    }
                }
            }

            return null;
        }

        private static bool IsWhite(int ch) => ch == 0x20 || (uint)(ch - 0x09) <= (0x0D - 0x09);

        private static bool IsDigit(int ch) => ((uint)ch - '0') <= 9;

        internal enum ParsingStatus
        {
            OK,
            Failed,
            Overflow
        }

        // SharpOS: the TypeCode parameter of upstream's ThrowOverflowOrFormatException is dropped
        // with the integer parsers it served; the floating-point callers only ever pass Failed.
        [DoesNotReturn]
        internal static void ThrowOverflowOrFormatException(ParsingStatus status, ReadOnlySpan<char> value) => throw GetException(status, value);

        private static Exception GetException(ParsingStatus status, ReadOnlySpan<char> value)
        {
            if (status == ParsingStatus.Failed)
                return new FormatException(SR.Format(SR.Format_InvalidStringWithValue, value.ToString()));

            return new OverflowException(); // SharpOS cut: GetOverflowException(TypeCode) - integer parsers only.
        }

        internal static double NumberToDouble(ref NumberBuffer number)
        {
            number.CheckConsistency();
            double result;

            if ((number.DigitsCount == 0) || (number.Scale < DoubleMinExponent))
            {
                result = 0;
            }
            else if (number.Scale > DoubleMaxExponent)
            {
                result = double.PositiveInfinity;
            }
            else
            {
                ulong bits = NumberToDoubleFloatingPointBits(ref number, in FloatingPointInfo.Double);
                result = BitConverter.UInt64BitsToDouble(bits);
            }

            return number.IsNegative ? -result : result;
        }

        // SharpOS cut: NumberToHalf (no System.Half).

        internal static float NumberToSingle(ref NumberBuffer number)
        {
            number.CheckConsistency();
            float result;

            if ((number.DigitsCount == 0) || (number.Scale < SingleMinExponent))
            {
                result = 0;
            }
            else if (number.Scale > SingleMaxExponent)
            {
                result = float.PositiveInfinity;
            }
            else
            {
                uint bits = NumberToSingleFloatingPointBits(ref number, in FloatingPointInfo.Single);
                result = BitConverter.UInt32BitsToSingle(bits);
            }

            return number.IsNegative ? -result : result;
        }
    }
}
