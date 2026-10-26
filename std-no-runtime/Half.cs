// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime main (.NET 10, the fork dotnet-runtime-sharpos):
//   src/libraries/System.Private.CoreLib/src/System/Half.cs
// The type up to its generic-math section, verbatim: bit layout, Is*,
// comparison, every conversion (the Berkeley SoftFloat rounding of
// float/double to Half included), Equals/GetHashCode/CompareTo. Cuts, each
// marked "SharpOS":
//   - the generic-math interfaces (IBinaryFloatingPointIeee754<Half> and the
//     rest, IBinaryFloatParseAndFormatInfo<Half>): std's numeric types have no
//     generic math. Their arithmetic operators are kept, at the end, as the
//     BCL computes them (through float).
//   - Parse/TryParse go through double and then round to Half — the BCL
//     parses straight to Half; a decimal exactly between two Halfs after
//     double rounding can land on the other one.
//   - ToString/TryFormat print the value as float: Number.FormatFloat<Half>
//     (shortest round-trip for Half) is not ported; 0.1 as Half prints
//     "0.099975586", not "0.1".
//   - Int128/UInt128 conversions: std has no Int128.
//   - float.X / double.X bit-layout constants: local constants (below).

using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    // Portions of the code implemented below are based on the 'Berkeley SoftFloat Release 3e' algorithms.

    /// <summary>
    /// Represents a half-precision floating-point number.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Half
        : IComparable,
          ISpanFormattable,
          IComparable<Half>,
          IEquatable<Half>,
          // SharpOS cut: IBinaryFloatingPointIeee754<Half>, IMinMaxValue<Half>
          IUtf8SpanFormattable
          // SharpOS cut: IBinaryFloatParseAndFormatInfo<Half>
    {
        // SharpOS: IAdditiveIdentity's Zero, which Parse below uses.
        private static Half Zero => new Half(PositiveZeroBits);

        // SharpOS: float's and double's internal bit-layout constants (Single.cs,
        // Double.cs), which std's Single and Double do not declare; "float.X"
        // and "double.X" in the bodies below read these.
        private const uint SingleSignMask = 0x8000_0000;
        private const int SingleSignShift = 31;
        private const uint SingleBiasedExponentMask = 0x7F80_0000;
        private const int SingleBiasedExponentShift = 23;
        private const ulong DoubleSignMask = 0x8000_0000_0000_0000;
        private const int DoubleSignShift = 63;
        private const ulong DoubleBiasedExponentMask = 0x7FF0_0000_0000_0000;
        private const int DoubleBiasedExponentShift = 52;
        private const ulong DoubleTrailingSignificandMask = 0x000F_FFFF_FFFF_FFFF;

        private const NumberStyles DefaultParseStyle = NumberStyles.Float | NumberStyles.AllowThousands;

        // Constants for manipulating the private bit-representation

        internal const ushort SignMask = 0x8000;
        internal const int SignShift = 15;
        internal const byte ShiftedSignMask = SignMask >> SignShift;

        internal const ushort BiasedExponentMask = 0x7C00;
        internal const int BiasedExponentShift = 10;
        internal const int BiasedExponentLength = 5;
        internal const byte ShiftedBiasedExponentMask = BiasedExponentMask >> BiasedExponentShift;

        internal const ushort TrailingSignificandMask = 0x03FF;

        internal const byte MinSign = 0;
        internal const byte MaxSign = 1;

        internal const byte MinBiasedExponent = 0x00;
        internal const byte MaxBiasedExponent = 0x1F;

        internal const byte ExponentBias = 15;

        internal const sbyte MinExponent = -14;
        internal const sbyte MaxExponent = +15;

        internal const ushort MinTrailingSignificand = 0x0000;
        internal const ushort MaxTrailingSignificand = 0x03FF;

        internal const int TrailingSignificandLength = 10;
        internal const int SignificandLength = TrailingSignificandLength + 1;

        // Constants representing the private bit-representation for various default values

        private const ushort PositiveZeroBits = 0x0000;
        private const ushort NegativeZeroBits = 0x8000;

        private const ushort EpsilonBits = 0x0001;

        private const ushort PositiveInfinityBits = 0x7C00;
        private const ushort NegativeInfinityBits = 0xFC00;

        private const ushort PositiveQNaNBits = 0x7E00;
        private const ushort NegativeQNaNBits = 0xFE00;

        private const ushort MinValueBits = 0xFBFF;
        private const ushort MaxValueBits = 0x7BFF;

        private const ushort PositiveOneBits = 0x3C00;
        private const ushort NegativeOneBits = 0xBC00;

        private const ushort SmallestNormalBits = 0x0400;

        private const ushort EBits = 0x4170;
        private const ushort PiBits = 0x4248;
        private const ushort TauBits = 0x4648;

        // Well-defined and commonly used values

        public static Half Epsilon => new Half(EpsilonBits);                        //  5.9604645E-08

        public static Half PositiveInfinity => new Half(PositiveInfinityBits);      //  1.0 / 0.0;

        public static Half NegativeInfinity => new Half(NegativeInfinityBits);      // -1.0 / 0.0

        public static Half NaN => new Half(NegativeQNaNBits);                       //  0.0 / 0.0

        /// <inheritdoc cref="IMinMaxValue{TSelf}.MinValue" />
        public static Half MinValue => new Half(MinValueBits);                      // -65504

        /// <inheritdoc cref="IMinMaxValue{TSelf}.MaxValue" />
        public static Half MaxValue => new Half(MaxValueBits);                      //  65504

        internal readonly ushort _value;

        internal Half(ushort value)
        {
            _value = value;
        }

        private Half(bool sign, ushort exp, ushort sig) => _value = (ushort)(((sign ? 1 : 0) << SignShift) + (exp << BiasedExponentShift) + sig);

        internal byte BiasedExponent
        {
            get
            {
                ushort bits = _value;
                return ExtractBiasedExponentFromBits(bits);
            }
        }

        internal sbyte Exponent
        {
            get
            {
                return (sbyte)(BiasedExponent - ExponentBias);
            }
        }

        internal ushort Significand
        {
            get
            {
                return (ushort)(TrailingSignificand | ((BiasedExponent != 0) ? (1U << BiasedExponentShift) : 0U));
            }
        }

        internal ushort TrailingSignificand
        {
            get
            {
                ushort bits = _value;
                return ExtractTrailingSignificandFromBits(bits);
            }
        }

        internal static byte ExtractBiasedExponentFromBits(ushort bits)
        {
            return (byte)((bits >> BiasedExponentShift) & ShiftedBiasedExponentMask);
        }

        internal static ushort ExtractTrailingSignificandFromBits(ushort bits)
        {
            return (ushort)(bits & TrailingSignificandMask);
        }

        /// <inheritdoc cref="IComparisonOperators{TSelf, TOther, TResult}.op_LessThan(TSelf, TOther)" />
        public static bool operator <(Half left, Half right)
        {
            if (IsNaN(left) || IsNaN(right))
            {
                // IEEE defines that NaN is unordered with respect to everything, including itself.
                return false;
            }

            bool leftIsNegative = IsNegative(left);

            if (leftIsNegative != IsNegative(right))
            {
                // When the signs of left and right differ, we know that left is less than right if it is
                // the negative value. The exception to this is if both values are zero, in which case IEEE
                // says they should be equal, even if the signs differ.
                return leftIsNegative && !AreZero(left, right);
            }

            return (left._value != right._value) && ((left._value < right._value) ^ leftIsNegative);
        }

        /// <inheritdoc cref="IComparisonOperators{TSelf, TOther, TResult}.op_GreaterThan(TSelf, TOther)" />
        public static bool operator >(Half left, Half right)
        {
            return right < left;
        }

        /// <inheritdoc cref="IComparisonOperators{TSelf, TOther, TResult}.op_LessThanOrEqual(TSelf, TOther)" />
        public static bool operator <=(Half left, Half right)
        {
            if (IsNaN(left) || IsNaN(right))
            {
                // IEEE defines that NaN is unordered with respect to everything, including itself.
                return false;
            }

            bool leftIsNegative = IsNegative(left);

            if (leftIsNegative != IsNegative(right))
            {
                // When the signs of left and right differ, we know that left is less than right if it is
                // the negative value. The exception to this is if both values are zero, in which case IEEE
                // says they should be equal, even if the signs differ.
                return leftIsNegative || AreZero(left, right);
            }

            return (left._value == right._value) || ((left._value < right._value) ^ leftIsNegative);
        }

        /// <inheritdoc cref="IComparisonOperators{TSelf, TOther, TResult}.op_GreaterThanOrEqual(TSelf, TOther)" />
        public static bool operator >=(Half left, Half right)
        {
            return right <= left;
        }

        /// <inheritdoc cref="IEqualityOperators{TSelf, TOther, TResult}.op_Equality(TSelf, TOther)" />
        public static bool operator ==(Half left, Half right)
        {
            if (IsNaN(left) || IsNaN(right))
            {
                // IEEE defines that NaN is not equal to anything, including itself.
                return false;
            }

            // IEEE defines that positive and negative zero are equivalent.
            return (left._value == right._value) || AreZero(left, right);
        }

        /// <inheritdoc cref="IEqualityOperators{TSelf, TOther, TResult}.op_Inequality(TSelf, TOther)" />
        public static bool operator !=(Half left, Half right)
        {
            return !(left == right);
        }

        /// <summary>Determines whether the specified value is finite (zero, subnormal, or normal).</summary>
        /// <remarks>This effectively checks the value is not NaN and not infinite.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsFinite(Half value)
        {
            uint bits = value._value;
            return (~bits & PositiveInfinityBits) != 0;
        }

        /// <summary>Determines whether the specified value is infinite.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsInfinity(Half value)
        {
            uint bits = value._value;
            return (bits & ~SignMask) == PositiveInfinityBits;
        }

        /// <summary>Determines whether the specified value is NaN.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNaN(Half value)
        {
            uint bits = value._value;
            return (bits & ~SignMask) > PositiveInfinityBits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsNaNOrZero(Half value)
        {
            uint bits = value._value;
            return ((bits - 1) & ~SignMask) >= PositiveInfinityBits;
        }

        /// <summary>Determines whether the specified value is negative.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNegative(Half value)
        {
            return (short)(value._value) < 0;
        }

        /// <summary>Determines whether the specified value is negative infinity.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNegativeInfinity(Half value)
        {
            return value._value == NegativeInfinityBits;
        }

        /// <summary>Determines whether the specified value is normal (finite, but not zero or subnormal).</summary>
        /// <remarks>This effectively checks the value is not NaN, not infinite, not subnormal, and not zero.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNormal(Half value)
        {
            uint bits = value._value;
            return (ushort)((bits & ~SignMask) - SmallestNormalBits) < (PositiveInfinityBits - SmallestNormalBits);
        }

        /// <summary>Determines whether the specified value is positive infinity.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPositiveInfinity(Half value)
        {
            return value._value == PositiveInfinityBits;
        }

        /// <summary>Determines whether the specified value is subnormal (finite, but not zero or normal).</summary>
        /// <remarks>This effectively checks the value is not NaN, not infinite, not normal, and not zero.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSubnormal(Half value)
        {
            uint bits = value._value;
            return (ushort)((bits & ~SignMask) - 1) < MaxTrailingSignificand;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsZero(Half value)
        {
            uint bits = value._value;
            return (bits & ~SignMask) == 0;
        }

        /// <summary>
        /// Parses a <see cref="Half"/> from a <see cref="string"/> in the default parse style.
        /// </summary>
        /// <param name="s">The input to be parsed.</param>
        /// <returns>The equivalent <see cref="Half"/> value representing the input string. If the input exceeds Half's range, a <see cref="PositiveInfinity"/> or <see cref="NegativeInfinity"/> is returned. </returns>
        public static Half Parse(string s) => Parse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider: null);

        /// <summary>
        /// Parses a <see cref="Half"/> from a <see cref="string"/> in the given <see cref="NumberStyles"/>.
        /// </summary>
        /// <param name="s">The input to be parsed.</param>
        /// <param name="style">The <see cref="NumberStyles"/> used to parse the input.</param>
        /// <returns>The equivalent <see cref="Half"/> value representing the input string. If the input exceeds Half's range, a <see cref="PositiveInfinity"/> or <see cref="NegativeInfinity"/> is returned. </returns>
        public static Half Parse(string s, NumberStyles style) => Parse(s, style, provider: null);

        /// <summary>
        /// Parses a <see cref="Half"/> from a <see cref="string"/> and <see cref="IFormatProvider"/>.
        /// </summary>
        /// <param name="s">The input to be parsed.</param>
        /// <param name="provider">A format provider.</param>
        /// <returns>The equivalent <see cref="Half"/> value representing the input string. If the input exceeds Half's range, a <see cref="PositiveInfinity"/> or <see cref="NegativeInfinity"/> is returned. </returns>
        public static Half Parse(string s, IFormatProvider? provider) => Parse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider);

        /// <summary>
        /// Parses a <see cref="Half"/> from a <see cref="string"/> with the given <see cref="NumberStyles"/> and <see cref="IFormatProvider"/>.
        /// </summary>
        /// <param name="s">The input to be parsed.</param>
        /// <param name="style">The <see cref="NumberStyles"/> used to parse the input.</param>
        /// <param name="provider">A format provider.</param>
        /// <returns>The equivalent <see cref="Half"/> value representing the input string. If the input exceeds Half's range, a <see cref="PositiveInfinity"/> or <see cref="NegativeInfinity"/> is returned. </returns>
        public static Half Parse(string s, NumberStyles style = DefaultParseStyle, IFormatProvider? provider = null)
        {
            if (s is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s);
            }
            return Parse(s.AsSpan(), style, provider);
        }

        /// <summary>
        /// Parses a <see cref="Half"/> from a <see cref="ReadOnlySpan{Char}"/> and <see cref="IFormatProvider"/>.
        /// </summary>
        /// <param name="s">The input to be parsed.</param>
        /// <param name="style">The <see cref="NumberStyles"/> used to parse the input.</param>
        /// <param name="provider">A format provider. </param>
        /// <returns>The equivalent <see cref="Half"/> value representing the input string. If the input exceeds Half's range, a <see cref="PositiveInfinity"/> or <see cref="NegativeInfinity"/> is returned. </returns>
        public static Half Parse(ReadOnlySpan<char> s, NumberStyles style = DefaultParseStyle, IFormatProvider? provider = null)
        {
            // SharpOS: through double (see the header).
            return (Half)double.Parse(s.ToString(), style, provider);
        }

        /// <summary>
        /// Tries to parse a <see cref="Half"/> from a <see cref="string"/> in the default parse style.
        /// </summary>
        /// <param name="s">The input to be parsed.</param>
        /// <param name="result">The equivalent <see cref="Half"/> value representing the input string if the parse was successful. If the input exceeds Half's range, a <see cref="PositiveInfinity"/> or <see cref="NegativeInfinity"/> is returned. If the parse was unsuccessful, a default <see cref="Half"/> value is returned.</param>
        /// <returns><see langword="true" /> if the parse was successful, <see langword="false" /> otherwise.</returns>
        public static bool TryParse([NotNullWhen(true)] string? s, out Half result) => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider: null, out result);

        /// <summary>
        /// Tries to parse a <see cref="Half"/> from a <see cref="ReadOnlySpan{Char}"/> in the default parse style.
        /// </summary>
        /// <param name="s">The input to be parsed.</param>
        /// <param name="result">The equivalent <see cref="Half"/> value representing the input string if the parse was successful. If the input exceeds Half's range, a <see cref="PositiveInfinity"/> or <see cref="NegativeInfinity"/> is returned. If the parse was unsuccessful, a default <see cref="Half"/> value is returned.</param>
        /// <returns><see langword="true" /> if the parse was successful, <see langword="false" /> otherwise.</returns>
        public static bool TryParse(ReadOnlySpan<char> s, out Half result) => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider: null, out result);

        /// <summary>Tries to convert a UTF-8 character span containing the string representation of a number to its half-precision floating-point number equivalent.</summary>
        /// <param name="utf8Text">A read-only UTF-8 character span that contains the number to convert.</param>
        /// <param name="result">When this method returns, contains a half-precision floating-point number equivalent of the numeric value or symbol contained in <paramref name="utf8Text" /> if the conversion succeeded or zero if the conversion failed. The conversion fails if the <paramref name="utf8Text" /> is <see cref="ReadOnlySpan{T}.Empty" /> or is not in a valid format. This parameter is passed uninitialized; any value originally supplied in result will be overwritten.</param>
        /// <returns><c>true</c> if <paramref name="utf8Text" /> was converted successfully; otherwise, false.</returns>
        public static bool TryParse(ReadOnlySpan<byte> utf8Text, out Half result) => TryParse(utf8Text, NumberStyles.Float | NumberStyles.AllowThousands, provider: null, out result);

        /// <summary>
        /// Tries to parse a <see cref="Half"/> from a <see cref="string"/> with the given <see cref="NumberStyles"/> and <see cref="IFormatProvider"/>.
        /// </summary>
        /// <param name="s">The input to be parsed.</param>
        /// <param name="style">The <see cref="NumberStyles"/> used to parse the input.</param>
        /// <param name="provider">A format provider. </param>
        /// <param name="result">The equivalent <see cref="Half"/> value representing the input string if the parse was successful. If the input exceeds Half's range, a <see cref="PositiveInfinity"/> or <see cref="NegativeInfinity"/> is returned. If the parse was unsuccessful, a default <see cref="Half"/> value is returned.</param>
        /// <returns><see langword="true" /> if the parse was successful, <see langword="false" /> otherwise.</returns>
        public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out Half result)
        {
            if (s == null)
            {
                result = Zero;
                return false;
            }
            return TryParse(s.AsSpan(), style, provider, out result);
        }

        /// <summary>
        /// Tries to parse a <see cref="Half"/> from a <see cref="ReadOnlySpan{Char}"/> with the given <see cref="NumberStyles"/> and <see cref="IFormatProvider"/>.
        /// </summary>
        /// <param name="s">The input to be parsed.</param>
        /// <param name="style">The <see cref="NumberStyles"/> used to parse the input.</param>
        /// <param name="provider">A format provider. </param>
        /// <param name="result">The equivalent <see cref="Half"/> value representing the input string if the parse was successful. If the input exceeds Half's range, a <see cref="PositiveInfinity"/> or <see cref="NegativeInfinity"/> is returned. If the parse was unsuccessful, a default <see cref="Half"/> value is returned.</param>
        /// <returns><see langword="true" /> if the parse was successful, <see langword="false" /> otherwise.</returns>
        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out Half result)
        {
            // SharpOS: through double (see the header).
            if (double.TryParse(s, style, provider, out double value))
            {
                result = (Half)value;
                return true;
            }
            result = Zero;
            return false;
        }

        // SharpOS: INumberBase's UTF-8 TryParse, which the short overload above
        // calls — the text decoded, then as the char overload.
        public static bool TryParse(ReadOnlySpan<byte> utf8Text, NumberStyles style, IFormatProvider? provider, out Half result) =>
            TryParse(System.Text.Encoding.UTF8.GetString(utf8Text).AsSpan(), style, provider, out result);

        private static bool AreZero(Half left, Half right)
        {
            // IEEE defines that positive and negative zero are equal, this gives us a quick equality check
            // for two values by or'ing the private bits together and stripping the sign. They are both zero,
            // and therefore equivalent, if the resulting value is still zero.
            return ((left._value | right._value) & ~SignMask) == 0;
        }

        /// <summary>
        /// Compares this object to another object, returning an integer that indicates the relationship.
        /// </summary>
        /// <returns>A value less than zero if this is less than <paramref name="obj"/>, zero if this is equal to <paramref name="obj"/>, or a value greater than zero if this is greater than <paramref name="obj"/>.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="obj"/> is not of type <see cref="Half"/>.</exception>
        public int CompareTo(object? obj)
        {
            if (obj is not Half other)
            {
                return (obj is null) ? 1 : throw new ArgumentException("Object must be of type Half."); // SharpOS: SR.Arg_MustBeHalf's text
            }
            return CompareTo(other);
        }

        /// <summary>
        /// Compares this object to another object, returning an integer that indicates the relationship.
        /// </summary>
        /// <returns>A value less than zero if this is less than <paramref name="other"/>, zero if this is equal to <paramref name="other"/>, or a value greater than zero if this is greater than <paramref name="other"/>.</returns>
        public int CompareTo(Half other)
        {
            if (this < other)
            {
                return -1;
            }

            if (this > other)
            {
                return 1;
            }

            if (this == other)
            {
                return 0;
            }

            if (IsNaN(this))
            {
                return IsNaN(other) ? 0 : -1;
            }

            Debug.Assert(IsNaN(other));
            return 1;
        }

        /// <summary>
        /// Returns a value that indicates whether this instance is equal to a specified <paramref name="obj"/>.
        /// </summary>
        public override bool Equals([NotNullWhen(true)] object? obj)
        {
            return (obj is Half other) && Equals(other);
        }

        /// <summary>
        /// Returns a value that indicates whether this instance is equal to a specified <paramref name="other"/> value.
        /// </summary>
        public bool Equals(Half other)
        {
            return _value == other._value
                || AreZero(this, other)
                || (IsNaN(this) && IsNaN(other));
        }

        /// <summary>
        /// Serves as the default hash function.
        /// </summary>
        public override int GetHashCode()
        {
            uint bits = _value;

            if (IsNaNOrZero(this))
            {
                // Ensure that all NaNs and both zeros have the same hash code
                bits &= PositiveInfinityBits;
            }

            return (int)bits;
        }

        /// <summary>
        /// Returns a string representation of the current value.
        /// </summary>
        public override string ToString()
        {
            return ((float)this).ToString(); // SharpOS: as float (see the header)
        }

        /// <summary>
        /// Returns a string representation of the current value using the specified <paramref name="format"/>.
        /// </summary>
        public string ToString([StringSyntax(StringSyntaxAttribute.NumericFormat)] string? format)
        {
            return ((float)this).ToString(format); // SharpOS: as float
        }

        /// <summary>
        /// Returns a string representation of the current value with the specified <paramref name="provider"/>.
        /// </summary>
        public string ToString(IFormatProvider? provider)
        {
            return ((float)this).ToString(provider); // SharpOS: as float
        }

        /// <summary>
        /// Returns a string representation of the current value using the specified <paramref name="format"/> and <paramref name="provider"/>.
        /// </summary>
        public string ToString([StringSyntax(StringSyntaxAttribute.NumericFormat)] string? format, IFormatProvider? provider)
        {
            return ((float)this).ToString(format, provider); // SharpOS: as float
        }

        /// <summary>
        /// Tries to format the value of the current Half instance into the provided span of characters.
        /// </summary>
        /// <param name="destination">When this method returns, this instance's value formatted as a span of characters.</param>
        /// <param name="charsWritten">When this method returns, the number of characters that were written in <paramref name="destination"/>.</param>
        /// <param name="format">A span containing the characters that represent a standard or custom format string that defines the acceptable format for <paramref name="destination"/>.</param>
        /// <param name="provider">An optional object that supplies culture-specific formatting information for <paramref name="destination"/>.</param>
        /// <returns></returns>
        public bool TryFormat(Span<char> destination, out int charsWritten, [StringSyntax(StringSyntaxAttribute.NumericFormat)] ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return TryCopy(((float)this).ToString(format.ToString(), provider), destination, out charsWritten); // SharpOS: as float
        }

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, [StringSyntax(StringSyntaxAttribute.NumericFormat)] ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return TryCopy(((float)this).ToString(format.ToString(), provider), utf8Destination, out bytesWritten); // SharpOS: as float
        }

        //
        // Explicit Convert To Half
        //

        /// <summary>Explicitly converts a <see cref="char" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        public static explicit operator Half(char value) => (Half)(float)value;

        /// <summary>Explicitly converts a <see cref="decimal" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        public static explicit operator Half(decimal value) => (Half)(float)value;

        /// <summary>Explicitly converts a <see cref="double" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        public static explicit operator Half(double value)
        {
            const int DoubleMaxExponent = 0x7FF;

            ulong doubleInt = BitConverter.DoubleToUInt64Bits(value);
            bool sign = (doubleInt & DoubleSignMask) >> DoubleSignShift != 0;
            int exp = (int)((doubleInt & DoubleBiasedExponentMask) >> DoubleBiasedExponentShift);
            ulong sig = doubleInt & DoubleTrailingSignificandMask;

            if (exp == DoubleMaxExponent)
            {
                if (sig != 0) // NaN
                {
                    return CreateHalfNaN(sign, sig << 12); // Shift the significand bits to the left end
                }
                return sign ? NegativeInfinity : PositiveInfinity;
            }

            uint sigHalf = (uint)ShiftRightJam(sig, 38);
            if ((exp | (int)sigHalf) == 0)
            {
                return new Half(sign, 0, 0);
            }
            return new Half(RoundPackToHalf(sign, (short)(exp - 0x3F1), (ushort)(sigHalf | 0x4000)));
        }

        /// <summary>Explicitly converts a <see cref="short" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        public static explicit operator Half(short value) => (Half)(float)value;

        /// <summary>Explicitly converts a <see cref="int" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        public static explicit operator Half(int value) => (Half)(float)value;

        /// <summary>Explicitly converts a <see cref="long" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        public static explicit operator Half(long value) => (Half)(float)value;

        /// <summary>Explicitly converts a <see cref="nint" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        public static explicit operator Half(nint value) => (Half)(float)value;

        /// <summary>Explicitly converts a <see cref="float" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        public static explicit operator Half(float value)
        {
            #region Explanation of this algorithm
            // This algorithm converts a single-precision floating-point number to a half-precision floating-point number by multiplying it as a floating-point number and rearranging the bit sequence.
            // However, it introduces some tricks to implement rounding correctly, to avoid multiplying denormalized numbers and to deal with exceptions such as infinity and NaN without using branch instructions.
            //
            // The bit sequence of a half-precision floating-point number is as follows
            // seee_eeff_ffff_ffff
            // The bit sequence of a single-precision floating-point number is as follows
            // seee_eeee_efff_ffff_ffff_ffff_ffff_ffff
            // In both cases, "_" is the hexadecimal separator, "s" is the sign, "e" is the exponent part, and "f" is the mantissa part.
            // In half-precision, the exponent part is 5 bits and the mantissa part is 10 bits. In single precision, the exponent is 8 bits and the mantissa is 23 bits.
            // Both formats use an offset binary representation for the exponent part: the exponent part for 1.0 is half of the maximum value for either precision, i.e., 127 for single-precision and 15 for half-precision.
            // The mantissa part is normalized when the exponent part is nonzero, since in binary numbers, 1 appears as the most significant digit for any nonzero number.
            //
            // This conversion algorithm takes advantage of the similarity between the two formats.
            // By isolating the sign part from the single-precision bitstring, limiting the range of absolute value, rounding the lower bits to match the half-precision, and shifting it 13 bits to the right, the boundary between the exponent and mantissa parts matches with that of half-precision.
            // In other words,
            // sEEEeeeeeffffffffffxxxxxxxxxxxxx is rearranged to
            //    seeeeeffffffffff
            // The x is the part that certainly gets rounded.
            //
            // When you operate with floating-point number, rounding occurs after every single floating-point operation.
            // For example, when you add 1.1f with MathF.PI, the internal representation of both value is:
            // 0 01111111 00011001100110011001101 for 1.1f, and
            // 0 10000000 10010010000111111011011 for MathF.PI (3.1415927f).
            // And raw binary representation of both numbers is:
            //   1.00011001100110011001101 for 1.1f, and
            //  11.0010010000111111011011  for 3.1415927f.
            // We matched the point for adding them properly.
            // Adding these numbers results:
            // 100.00111101110110010000011
            // After normalizing the number:
            // 1.0000111101110110010000011 x 2^2
            // But it has 25 bits below the point. So we should round the number to 23bits by the method called "Round to nearest, ties to even"
            // - Round to the nearest value
            // - If the number is at the midway, round it to the nearest value with an even least significant digit.
            // So we apply this:
            // 1.00001111011101100100001 x 2^2
            // And the result is:
            // 0 10000001 00001111011101100100001
            // Which matches the ground truth of `BitConverter.SingleToUInt32Bits(MathF.PI + 1.3f)`:
            // 0 10000001 00001111011101100100001
            //
            // When we want to round the number to a certain precision, we can take advantage of this specification.
            // If we craft a value to add carefully, the result of addition is rounded wherever we expect.
            // For instance, MathF.PI (3.1415927f) is:
            // 0 10000000 10010010000111111011011
            // We craft the adding value to round the MathF.PI into half-precision by adding (exponentOffset0 in the actual code) by:
            // - Making sure that both the exponentOffset0 and the value is smaller than MaxHalfValueBelowInfinity(65520.0f) as larger values goes infinity in Half, while letting NaN be as it is
            // - Making sure that the exponentOffset0 is larger than MinExp (0x3880_0000u) as smaller values goes subnormal in Half
            // - Clearing the fraction bits in exponentOffset0
            // - Adding Exponent13 (0x0680_0000u) to exponentOffset0 with integer ALU, effectively adding 13 to the exponent part of exponentOffset0
            // For 3.1415927f, the exponentOffset0 is:
            // 0 10001101 00000000000000000000000 (16384f)
            // Adding these numbers with floating-point arithmetic unit results:
            // 0 10001101 00000000000011001001000 (16387.14f)
            // You can see the first 11 bits of 11.0010010000111111011011 rounded appears at the bottom of the fraction part of the result.
            // By subtracting the 16384f from this with floating-point arithmetic unit, we get this:
            // 0 10000000 10010010000000000000000 (3.140625f)
            // And here is the `BitConverter.HalfToUInt16Bits((Half)MathF.PI)` in binary:
            // 0 10000 1001001000 (3.14)
            //
            // Now we have to resolve the difference of the exponent parts.
            // We can simply multiply the 1.92593E-34f in the floating-point number multiplication unit, to adjust the exponent part.
            // However, most hardware cannot efficiently handle the multiplication of denormalized numbers.
            // Adding the exponentOffset0 (16384f) to 3.1415927f with floating-point arithmetic unit results:
            // 0 10001101 00000000000011001001000 (16387.14f)
            // Then subtract the Exponent126 (0x3f00_0000u) from it with integer ALU:
            // 0 00001111 00000000000011001001000 (1.9262991E-34f)
            // And here is the `BitConverter.HalfToUInt16Bits((Half)MathF.PI)` in binary:
            // 0 10000 1001001000 (3.14)
            // Note that we left the leading 1 in fraction on top of the 10 lowest significant bits.
            // Now we have to rearrange the bitstring.
            // By shifting the internal representation of 1.9262991E-34f right by 13 bits, we get this:
            // 0 01111 0000000000 ((Half)1.0f)
            // By adding it to the internal representation of 1.9262991E-34f if the value isn't NaN, we get this:
            // (0 11110000 000) 0 10000 1001001000 (3.14 in Half with some garbage on top of it)
            // Now we have to merge the sign bit at the right position, and clear the garbage on top of 16-bit final bitstring:
            // 0 10000 1001001000 (3.14 in Half)
            // And here is the `BitConverter.HalfToUInt16Bits((Half)MathF.PI)` in binary:
            // 0 10000 1001001000 (3.14 in Half)
            //
            // If the value is NaN in Half, we should further modify the exponent part of the intermediate value.
            // For the 0xffbf_ffffu (NaN,
            // 1 11111111 01111111111111111111111 in binary), the exponentOffset0 is:
            // 1 00001100 00000000000000000000000 (-2.4074124E-35f)
            // It doesn't look correct! But don't worry.
            // And the result of `value + exponentOffset0` is:
            // 0 11111111 11111111111111111111111 (NaN)
            // As the sign part is isolated at the beginning, the sign bit is 0 here.
            // The exponent don't seem to be changed at all, and the only difference here from the original value 0xffbf_ffffu is the sign bit and the highest bit of fraction part.
            // Setting the highest bit of fraction part is an expected behavior.
            // After subtracting the Exponent126 from it, we get this:
            // 0 10000001 11111111111111111111111 (7.9999995f)
            // By shifting the internal representation of it right by 13 bits, we get this:
            // 0010 0 00001 1111111111
            // By adding it to the internal representation of 7.9999995f if the original value isn't NaN, we get this:
            // 0010 0 00001 1111111111
            // Here we changed nothing because the original value is NaN, so 7.9999995f is thrown away from scope already.
            // The maskedHalfExponentForNaN was generated before checking for the underflow. The value of maskedHalfExponentForNaN here is:
            // - ExponentMask (0x7c00u) if the value is NaN, 0 otherwise
            // Then the signAndMaskedExponent is also generated by ORing the maskedHalfExponentForNaN and the isolated sign bit shifted 16 bits right (0x8000u in this case):
            // 1 11111 0000000000 (Half.NegativeInfinity)
            // The exponent part here is also a complete gibberish, so we clear them by ANDing the ~maskedHalfExponentForNaN:
            // 0010 0 00000 1111111111 (6.1E-05 in Half with some garbage on top of it)
            // Then merge the signAndMaskedExponent with it, and clear the garbage on top of 16-bit final bitstring:
            // 1 11111 1111111111 (NaN)
            // And here is the `BitConverter.HalfToUInt16Bits((Half)BitConverter.UInt32BitsToSingle(0xffbf_ffffu))` in binary:
            // 1 11111 1111111111 (NaN)
            //
            // This code does all of above steps, without any single branches.
            #endregion
            // Minimum exponent for rounding
            const uint MinExp = 0x3880_0000u;
            // Exponent displacement #1
            const uint Exponent126 = 0x3f00_0000u;
            // Exponent mask
            const uint SingleBiasedExponentMask = 0x7F80_0000; // SharpOS: float.BiasedExponentMask's value
            // Exponent displacement #2
            const uint Exponent13 = 0x0680_0000u;
            // Maximum value that is not Infinity in Half
            const float MaxHalfValueBelowInfinity = 65520.0f;
            // Mask for exponent bits in Half
            const uint ExponentMask = BiasedExponentMask;
            uint bitValue = BitConverter.SingleToUInt32Bits(value);
            // Extract sign bit
            uint sign = (bitValue & SingleSignMask) >> 16;
            // Detecting NaN (~0u if a is not NaN)
            uint realMask = (value != value) /* SharpOS: std float has no IsNaN */ ? 0u : ~0u;
            // Clear sign bit
            value = Math.Abs(value); // SharpOS: float.Abs/Min/Max are Math's here
            // Rectify values that are Infinity in Half. (float.Min now emits vminps instruction if one of two arguments is a constant)
            value = Math.Min(MaxHalfValueBelowInfinity, value);
            // Rectify lower exponent
            uint exponentOffset0 = BitConverter.SingleToUInt32Bits(Math.Max(value, BitConverter.UInt32BitsToSingle(MinExp)));
            // Extract exponent
            exponentOffset0 &= SingleBiasedExponentMask;
            // Add exponent by 13
            exponentOffset0 += Exponent13;
            // Round Single into Half's precision (NaN also gets modified here, just setting the MSB of fraction)
            value += BitConverter.UInt32BitsToSingle(exponentOffset0);
            bitValue = BitConverter.SingleToUInt32Bits(value);
            // Only exponent bits will be modified if NaN
            uint maskedHalfExponentForNaN = ~realMask & ExponentMask;
            // Subtract exponent by 126
            bitValue -= Exponent126;
            // Shift bitValue right by 13 bits to match the boundary of exponent part and fraction part.
            uint newExponent = bitValue >> 13;
            // Clear the fraction parts if the value was NaN.
            bitValue &= realMask;
            // Merge the exponent part with fraction part, and add the exponent part and fraction part's overflow.
            bitValue += newExponent;
            // Clear exponents if value is NaN
            bitValue &= ~maskedHalfExponentForNaN;
            // Merge sign bit with possible NaN exponent
            uint signAndMaskedExponent = maskedHalfExponentForNaN | sign;
            // Merge sign bit and possible NaN exponent
            bitValue |= signAndMaskedExponent;
            // The final result
            return BitConverter.UInt16BitsToHalf((ushort)bitValue);
        }

        /// <summary>Explicitly converts a <see cref="ushort" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        [CLSCompliant(false)]
        public static explicit operator Half(ushort value) => (Half)(float)value;

        /// <summary>Explicitly converts a <see cref="uint" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        [CLSCompliant(false)]
        public static explicit operator Half(uint value) => (Half)(float)value;

        /// <summary>Explicitly converts a <see cref="ulong" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        [CLSCompliant(false)]
        public static explicit operator Half(ulong value) => (Half)(float)value;

        /// <summary>Explicitly converts a <see cref="nuint" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        [CLSCompliant(false)]
        public static explicit operator Half(nuint value) => (Half)(float)value;

        //
        // Explicit Convert From Half
        //

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="byte" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="byte" /> value.</returns>
        public static explicit operator byte(Half value) => (byte)(float)value;

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="byte" /> value, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="byte" /> value.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="byte" />.</exception>
        public static explicit operator checked byte(Half value) => checked((byte)(float)value);

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="char" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="char" /> value.</returns>
        public static explicit operator char(Half value) => (char)(float)value;

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="char" /> value, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="char" /> value.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="char" />.</exception>
        public static explicit operator checked char(Half value) => checked((char)(float)value);

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="decimal" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="decimal" /> value.</returns>
        public static explicit operator decimal(Half value) => (decimal)(float)value;

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="short" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="short" /> value.</returns>
        public static explicit operator short(Half value) => (short)(float)value;

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="short" /> value, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="short" /> value.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="short" />.</exception>
        public static explicit operator checked short(Half value) => checked((short)(float)value);

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="int" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="int" /> value.</returns>
        public static explicit operator int(Half value) => (int)(float)value;

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="int" /> value, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="int" /> value.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="int" />.</exception>
        public static explicit operator checked int(Half value) => checked((int)(float)value);

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="long" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="long" /> value.</returns>
        public static explicit operator long(Half value) => (long)(float)value;

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="long" /> value, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="long" /> value.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="long" />.</exception>
        public static explicit operator checked long(Half value) => checked((long)(float)value);

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="Int128"/>.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to a 128-bit signed integer.</returns>
        // SharpOS cut: public static explicit operator Int128(Half value) => (Int128)(double)(value);

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="Int128"/>, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to a 128-bit signed integer.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="Int128" />.</exception>
        // SharpOS cut: public static explicit operator checked Int128(Half value) => checked((Int128)(double)(value));

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="IntPtr" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="IntPtr" /> value.</returns>
        public static explicit operator nint(Half value) => (nint)(float)value;

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="IntPtr" /> value, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="IntPtr" /> value.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="IntPtr" />.</exception>
        public static explicit operator checked nint(Half value) => checked((nint)(float)value);

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="sbyte" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="sbyte" /> value.</returns>
        [CLSCompliant(false)]
        public static explicit operator sbyte(Half value) => (sbyte)(float)value;

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="sbyte" /> value, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="sbyte" /> value.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="sbyte" />.</exception>
        [CLSCompliant(false)]
        public static explicit operator checked sbyte(Half value) => checked((sbyte)(float)value);

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="ushort" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="ushort" /> value.</returns>
        [CLSCompliant(false)]
        public static explicit operator ushort(Half value) => (ushort)(float)value;

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="ushort" /> value, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="ushort" /> value.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="ushort" />.</exception>
        [CLSCompliant(false)]
        public static explicit operator checked ushort(Half value) => checked((ushort)(float)value);

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="uint" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="uint" /> value.</returns>
        [CLSCompliant(false)]
        public static explicit operator uint(Half value) => (uint)(float)value;

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="uint" /> value, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="uint" /> value.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="uint" />.</exception>
        [CLSCompliant(false)]
        public static explicit operator checked uint(Half value) => checked((uint)(float)value);

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="ulong" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="ulong" /> value.</returns>
        [CLSCompliant(false)]
        public static explicit operator ulong(Half value) => (ulong)(float)value;

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="ulong" /> value, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="ulong" /> value.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="ulong" />.</exception>
        [CLSCompliant(false)]
        public static explicit operator checked ulong(Half value) => checked((ulong)(float)value);

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="UInt128"/>.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to a 128-bit unsigned integer.</returns>
        // SharpOS cut: [CLSCompliant(false)]
        // SharpOS cut: public static explicit operator UInt128(Half value) => (UInt128)(double)(value);

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="UInt128"/>, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to a 128-bit unsigned integer.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="UInt128" />.</exception>
        // SharpOS cut: [CLSCompliant(false)]
        // SharpOS cut: public static explicit operator checked UInt128(Half value) => checked((UInt128)(double)(value));

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="UIntPtr" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="UIntPtr" /> value.</returns>
        [CLSCompliant(false)]
        public static explicit operator nuint(Half value) => (nuint)(float)value;

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="UIntPtr" /> value, throwing an overflow exception for any values that fall outside the representable range.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="UIntPtr" /> value.</returns>
        /// <exception cref="OverflowException"><paramref name="value" /> is not representable by <see cref="UIntPtr" />.</exception>
        [CLSCompliant(false)]
        public static explicit operator checked nuint(Half value) => checked((nuint)(float)value);

        //
        // Implicit Convert To Half
        //

        /// <summary>Implicitly converts a <see cref="byte" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        public static implicit operator Half(byte value) => (Half)(float)value;

        /// <summary>Implicitly converts a <see cref="sbyte" /> value to its nearest representable half-precision floating-point value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable half-precision floating-point value.</returns>
        [CLSCompliant(false)]
        public static implicit operator Half(sbyte value) => (Half)(float)value;

        //
        // Implicit Convert From Half (actually explicit due to back-compat)
        //

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="double" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="double" /> value.</returns>
        public static explicit operator double(Half value)
        {
            bool sign = IsNegative(value);
            int exp = value.BiasedExponent;
            uint sig = value.TrailingSignificand;

            if (exp == MaxBiasedExponent)
            {
                if (sig != 0)
                {
                    return CreateDoubleNaN(sign, (ulong)sig << 54);
                }
                return sign ? double.NegativeInfinity : double.PositiveInfinity;
            }

            if (exp == 0)
            {
                if (sig == 0)
                {
                    return BitConverter.UInt64BitsToDouble(sign ? DoubleSignMask : 0); // Positive / Negative zero
                }
                (exp, sig) = NormSubnormalF16Sig(sig);
                exp -= 1;
            }

            return CreateDouble(sign, (ushort)(exp + 0x3F0), (ulong)sig << 42);
        }

        /// <summary>Explicitly converts a half-precision floating-point value to its nearest representable <see cref="float" /> value.</summary>
        /// <param name="value">The value to convert.</param>
        /// <returns><paramref name="value" /> converted to its nearest representable <see cref="float" /> value.</returns>
        public static explicit operator float(Half value)
        {
            #region Explanation of this algorithm
            // This algorithm converts a half-precision floating-point number to a single-precision floating-point number by rearranging the bit sequence and multiplying it as a floating-point number.
            // However, it introduces some tricks to avoid multiplying denormalized numbers and to deal with exceptions such as infinity and NaN without using branch instructions.
            //
            // The bit sequence of a half-precision floating-point number is as follows
            // seee_eeff_ffff_ffff
            // The bit sequence of a single-precision floating-point number is as follows
            // seee_eeee_efff_ffff_ffff_ffff_ffff_ffff
            // In both cases, "_" is the hexadecimal separator, "s" is the sign, "e" is the exponent part, and "f" is the mantissa part.
            // In half-precision, the exponent part is 5 bits and the mantissa part is 10 bits. In single precision, the exponent is 8 bits and the mantissa is 23 bits.
            // Both formats use an offset binary representation for the exponent part: the exponent part for 1.0 is half of the maximum value for either precision, i.e., 127 for single-precision and 15 for half-precision.
            // The mantissa part is normalized when the exponent part is nonzero, since in binary numbers, 1 appears as the most significant digit for any nonzero number.
            //
            // This conversion algorithm takes advantage of the similarity between the two formats.
            // By isolating the sign part from the half-precision bitstring and shifting it 13 bits to the left, the boundary between the exponent and mantissa parts matches with that of single-precision.
            // In other words,
            //    0eeeeeffffffffff              is rearranged to
            // 0000eeeeeffffffffff0000000000000
            // which matches the boundary between the exponent and mantissa parts of single-precision floating-point number:
            // seeeeeeeefffffffffffffffffffffff
            //
            // After rearrangement, this bit sequence is multiplied by the constant 5.192297E+33f in the floating-point number multiplication unit.
            // However, most hardware cannot efficiently handle the multiplication of denormalized numbers.
            // Denormalized numbers are more common in half-precision than in single-precision, so they cannot be ignored.
            //
            // First, if the value is a denormalized number, the constant 0x3880_0000u is added beforehand in the integer addition unit to make it behave as a normalized number.
            // For Infinity or NaN, the constant 0x7000_0000u is added beforehand in the integer adder.
            // These numbers are then converted to single-precision floating-point numbers as per the IEEE754 specification by the following operations.
            // Next, regardless of whether the value is a denormalized number or not, add the constant 0x3800_0000u to this bit string in the integer addition unit. The constant is chosen to add 112 to the exponent part; 112 is 127 subtracted by 15.
            // Then, if the value is a denormalized number, the constant 6.1035156E-05f is subtracted in the floating-point number subtraction unit.
            // The above operation produces the same result as if the rearranged bit sequence were multiplied by the constant 5.192297E+33f.
            // Finally, merging the isolated sign bits completes the conversion.
            #endregion
            // The smallest positive normal number in Half, converted to Single
            const uint ExponentLowerBound = 0x3880_0000u;
            // BitConverter.SingleToUInt32Bits(1.0f) - ((uint)BitConverter.HalfToUInt16Bits((Half)1.0f) << 13)
            const uint ExponentOffset = 0x3800_0000u;
            // Mask for sign bit in Single
            const uint SingleSignMask = 0x8000_0000; // SharpOS: float.SignMask's value
            // Mask for exponent bits in Half
            const uint HalfExponentMask = BiasedExponentMask;
            // Mask for bits in Single converted from Half
            const int HalfToSingleBitsMask = 0x0FFF_E000;
            // Extract the internal representation of value
            short valueInInt16Bits = BitConverter.HalfToInt16Bits(value);
            // Extract sign bit of value
            uint sign = (uint)(int)valueInInt16Bits & SingleSignMask;
            // Copy sign bit to upper bits
            uint bitValueInProcess = (uint)valueInInt16Bits;
            // Extract exponent bits of value (BiasedExponent is not for here as it performs unnecessary shift)
            uint offsetExponent = bitValueInProcess & HalfExponentMask;
            // ~0u when value is subnormal, 0 otherwise
            uint subnormalMask = offsetExponent == 0u ? ~0u : 0u;
            // 0x3880_0000u if value is subnormal, 0 otherwise
            uint maskedExponentLowerBound = subnormalMask & ExponentLowerBound;
            // 0x3880_0000u if value is subnormal, 0x3800_0000u otherwise
            uint offsetMaskedExponentLowerBound = ExponentOffset | maskedExponentLowerBound;
            // Match the position of the boundary of exponent bits and fraction bits with IEEE 754 Binary32(Single)
            bitValueInProcess <<= 13;
            // Double the offsetMaskedExponentLowerBound if value is either Infinity or NaN
            offsetMaskedExponentLowerBound <<= offsetExponent == HalfExponentMask ? 1 : 0;
            // Extract exponent bits and fraction bits of value
            bitValueInProcess &= HalfToSingleBitsMask;
            // Adjust exponent to match the range of exponent
            bitValueInProcess += offsetMaskedExponentLowerBound;
            // If value is subnormal, remove unnecessary 1 on top of fraction bits.
            uint absoluteValue = BitConverter.SingleToUInt32Bits(BitConverter.UInt32BitsToSingle(bitValueInProcess) - BitConverter.UInt32BitsToSingle(maskedExponentLowerBound));
            // Merge sign bit with rest
            return BitConverter.UInt32BitsToSingle(absoluteValue | sign);
        }

        // IEEE 754 specifies NaNs to be propagated
        internal static Half Negate(Half value)
        {
            return IsNaN(value) ? value : new Half((ushort)(value._value ^ SignMask));
        }

        private static (int Exp, uint Sig) NormSubnormalF16Sig(uint sig)
        {
            int shiftDist = BitOperations.LeadingZeroCount(sig) - 16 - 5;
            return (1 - shiftDist, sig << shiftDist);
        }

        #region Utilities

        // Significand bits should be shifted towards to the left end before calling these methods
        // Creates Quiet NaN if significand == 0
        private static Half CreateHalfNaN(bool sign, ulong significand)
        {
            const uint NaNBits = BiasedExponentMask | 0x200; // Most significant significand bit

            uint signInt = (sign ? 1U : 0U) << SignShift;
            uint sigInt = (uint)(significand >> 54);

            return BitConverter.UInt16BitsToHalf((ushort)(signInt | NaNBits | sigInt));
        }

        private static ushort RoundPackToHalf(bool sign, short exp, ushort sig)
        {
            const int RoundIncrement = 0x8; // Depends on rounding mode but it's always towards closest / ties to even
            int roundBits = sig & 0xF;

            if ((uint)exp >= 0x1D)
            {
                if (exp < 0)
                {
                    sig = (ushort)ShiftRightJam(sig, -exp);
                    exp = 0;
                    roundBits = sig & 0xF;
                }
                else if (exp > 0x1D || sig + RoundIncrement >= 0x8000) // Overflow
                {
                    return sign ? NegativeInfinityBits : PositiveInfinityBits;
                }
            }

            sig = (ushort)((sig + RoundIncrement) >> 4);
            sig &= (ushort)~(((roundBits ^ 8) != 0 ? 0 : 1) & 1);

            if (sig == 0)
            {
                exp = 0;
            }

            return new Half(sign, (ushort)exp, sig)._value;
        }

        // If any bits are lost by shifting, "jam" them into the LSB.
        // if dist > bit count, Will be 1 or 0 depending on i
        // (unlike bitwise operators that masks the lower 5 bits)
        private static uint ShiftRightJam(uint i, int dist) => dist < 31 ? (i >> dist) | (i << (-dist & 31) != 0 ? 1U : 0U) : (i != 0 ? 1U : 0U);

        private static ulong ShiftRightJam(ulong l, int dist) => dist < 63 ? (l >> dist) | (l << (-dist & 63) != 0 ? 1UL : 0UL) : (l != 0 ? 1UL : 0UL);

        private static float CreateSingleNaN(bool sign, ulong significand)
        {
            const uint NaNBits = SingleBiasedExponentMask | 0x400000; // Most significant significand bit

            uint signInt = (sign ? 1U : 0U) << SingleSignShift;
            uint sigInt = (uint)(significand >> 41);

            return BitConverter.UInt32BitsToSingle(signInt | NaNBits | sigInt);
        }

        private static double CreateDoubleNaN(bool sign, ulong significand)
        {
            const ulong NaNBits = DoubleBiasedExponentMask | 0x80000_00000000; // Most significant significand bit

            ulong signInt = (sign ? 1UL : 0UL) << DoubleSignShift;
            ulong sigInt = significand >> 12;

            return BitConverter.UInt64BitsToDouble(signInt | NaNBits | sigInt);
        }

        private static float CreateSingle(bool sign, byte exp, uint sig) => BitConverter.UInt32BitsToSingle(((sign ? 1U : 0U) << SingleSignShift) + ((uint)exp << SingleBiasedExponentShift) + sig);

        private static double CreateDouble(bool sign, ushort exp, ulong sig) => BitConverter.UInt64BitsToDouble(((sign ? 1UL : 0UL) << DoubleSignShift) + ((ulong)exp << DoubleBiasedExponentShift) + sig);

        #endregion

        // SharpOS: the float text into the caller's buffer, for TryFormat.
        private static bool TryCopy(string text, Span<char> destination, out int charsWritten)
        {
            if (text.Length > destination.Length) { charsWritten = 0; return false; }
            text.AsSpan().CopyTo(destination);
            charsWritten = text.Length;
            return true;
        }

        private static bool TryCopy(string text, Span<byte> utf8Destination, out int bytesWritten)
        {
            // The text of a float is ASCII: one byte a char.
            if (text.Length > utf8Destination.Length) { bytesWritten = 0; return false; }
            for (int i = 0; i < text.Length; i++) utf8Destination[i] = (byte)text[i];
            bytesWritten = text.Length;
            return true;
        }

        // SharpOS: the arithmetic operators of the generic-math interfaces cut
        // below (IAdditionOperators ... IUnaryPlusOperators), bodies verbatim:
        // through float, as the BCL computes them.
        public static Half operator +(Half left, Half right) => (Half)((float)left + (float)right);

        public static Half operator -(Half left, Half right) => (Half)((float)left - (float)right);

        public static Half operator *(Half left, Half right) => (Half)((float)left * (float)right);

        public static Half operator /(Half left, Half right) => (Half)((float)left / (float)right);

        public static Half operator %(Half left, Half right) => (Half)((float)left % (float)right);

        public static Half operator -(Half value) => Negate(value);

        public static Half operator +(Half value) => value;

        public static Half operator ++(Half value) => (Half)((float)value + 1.0f);

        public static Half operator --(Half value) => (Half)((float)value - 1.0f);
    }
}
