// IFormattable / ISpanFormattable / IUtf8SpanFormattable for the primitive
// numeric types, the IUtfChar<T> implementations of Char and Byte, and the
// IEEE classification statics the formatter needs. One copy for both tiers:
// the structs are declared `partial` in OS/src/Boot/MinimalRuntime.cs (kernel)
// and apps_native/sdk/MinimalRuntime.cs (apps), which keep layout, equality,
// comparison and parsing; everything here is shared, so the two tiers cannot
// drift apart on formatting.
//
// Member bodies are dotnet/runtime release/8.0 (v8.0.27, MIT) verbatim, from
// Byte.cs, SByte.cs, Int16.cs, UInt16.cs, Int32.cs, UInt32.cs, Int64.cs,
// UInt64.cs, IntPtr.cs, UIntPtr.cs, Single.cs, Double.cs and Char.cs, with
// `m_value` spelled `_value` (the name our MinimalRuntime uses) and nint_t /
// nuint_t spelled long / ulong (x64 only).
//
// SharpOS cuts: [StringSyntax] / [NonVersionable] / [Intrinsic] attributes
// (compile-time metadata this std does not declare); the generic-math
// interfaces these types also implement upstream (INumber<T> and family - not
// in this std, see IUtfChar.cs).

using System.Globalization;
using System.Numerics;

namespace System
{
    public partial struct Char : IUtfChar<char>
    {
        //
        // IUtfChar
        //

        static char IUtfChar<char>.CastFrom(byte value) => (char)value;
        static char IUtfChar<char>.CastFrom(char value) => value;
        static char IUtfChar<char>.CastFrom(int value) => (char)value;
        static char IUtfChar<char>.CastFrom(uint value) => (char)value;
        static char IUtfChar<char>.CastFrom(ulong value) => (char)value;

        static uint IUtfChar<char>.CastToUInt32(char value) => value;
    }

    public partial struct Byte : IFormattable, ISpanFormattable, IUtf8SpanFormattable, IUtfChar<byte>
    {
        //
        // IUtfChar
        //

        static byte IUtfChar<byte>.CastFrom(byte value) => value;
        static byte IUtfChar<byte>.CastFrom(char value) => (byte)value;
        static byte IUtfChar<byte>.CastFrom(int value) => (byte)value;
        static byte IUtfChar<byte>.CastFrom(uint value) => (byte)value;
        static byte IUtfChar<byte>.CastFrom(ulong value) => (byte)value;

        static uint IUtfChar<byte>.CastToUInt32(byte value) => value;

        public override string ToString()
        {
            return Number.UInt32ToDecStr(_value);
        }

        public string ToString(string? format)
        {
            return Number.FormatUInt32(_value, format, null);
        }

        public string ToString(IFormatProvider? provider)
        {
            return Number.UInt32ToDecStr(_value);
        }

        public string ToString(string? format, IFormatProvider? provider)
        {
            return Number.FormatUInt32(_value, format, provider);
        }

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatUInt32(_value, format, provider, destination, out charsWritten);
        }

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatUInt32(_value, format, provider, utf8Destination, out bytesWritten);
        }
    }

    public partial struct SByte : IFormattable, ISpanFormattable, IUtf8SpanFormattable
    {
        public override string ToString()
        {
            return Number.Int32ToDecStr(_value);
        }

        public string ToString(string? format)
        {
            return ToString(format, null);
        }

        public string ToString(IFormatProvider? provider)
        {
            return Number.FormatInt32(_value, 0, null, provider);
        }

        public string ToString(string? format, IFormatProvider? provider)
        {
            return Number.FormatInt32(_value, 0x000000FF, format, provider);
        }

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatInt32(_value, 0x000000FF, format, provider, destination, out charsWritten);
        }

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatInt32(_value, 0x000000FF, format, provider, utf8Destination, out bytesWritten);
        }
    }

    public partial struct Int16 : IFormattable, ISpanFormattable, IUtf8SpanFormattable
    {
        public override string ToString()
        {
            return Number.Int32ToDecStr(_value);
        }

        public string ToString(string? format)
        {
            return ToString(format, null);
        }

        public string ToString(IFormatProvider? provider)
        {
            return Number.FormatInt32(_value, 0, null, provider);
        }

        public string ToString(string? format, IFormatProvider? provider)
        {
            return Number.FormatInt32(_value, 0x0000FFFF, format, provider);
        }

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatInt32(_value, 0x0000FFFF, format, provider, destination, out charsWritten);
        }

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatInt32(_value, 0x0000FFFF, format, provider, utf8Destination, out bytesWritten);
        }
    }

    public partial struct UInt16 : IFormattable, ISpanFormattable, IUtf8SpanFormattable
    {
        public override string ToString()
        {
            return Number.UInt32ToDecStr(_value);
        }

        public string ToString(string? format)
        {
            return Number.FormatUInt32(_value, format, null);
        }

        public string ToString(IFormatProvider? provider)
        {
            return Number.UInt32ToDecStr(_value);
        }

        public string ToString(string? format, IFormatProvider? provider)
        {
            return Number.FormatUInt32(_value, format, provider);
        }

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatUInt32(_value, format, provider, destination, out charsWritten);
        }

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatUInt32(_value, format, provider, utf8Destination, out bytesWritten);
        }
    }

    public partial struct Int32 : IFormattable, ISpanFormattable, IUtf8SpanFormattable
    {
        public override string ToString()
        {
            return Number.Int32ToDecStr(_value);
        }

        public string ToString(string? format)
        {
            return ToString(format, null);
        }

        public string ToString(IFormatProvider? provider)
        {
            return Number.FormatInt32(_value, 0, null, provider);
        }

        public string ToString(string? format, IFormatProvider? provider)
        {
            return Number.FormatInt32(_value, ~0, format, provider);
        }

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatInt32(_value, ~0, format, provider, destination, out charsWritten);
        }

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatInt32(_value, ~0, format, provider, utf8Destination, out bytesWritten);
        }
    }

    public partial struct UInt32 : IFormattable, ISpanFormattable, IUtf8SpanFormattable
    {
        // IBinaryNumber<uint>.Log2 surfaces as a public static (UInt32.cs);
        // FormattingHelpers.CountDigits(uint) calls it.
        public static uint Log2(uint value) => (uint)BitOperations.Log2(value);

        // IBinaryInteger<uint>.LeadingZeroCount, public static as in UInt32.cs;
        // the binary ("B") formatter calls it.
        public static uint LeadingZeroCount(uint value) => (uint)BitOperations.LeadingZeroCount(value);

        public override string ToString()
        {
            return Number.UInt32ToDecStr(_value);
        }

        public string ToString(string? format)
        {
            return Number.FormatUInt32(_value, format, null);
        }

        public string ToString(IFormatProvider? provider)
        {
            return Number.UInt32ToDecStr(_value);
        }

        public string ToString(string? format, IFormatProvider? provider)
        {
            return Number.FormatUInt32(_value, format, provider);
        }

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatUInt32(_value, format, provider, destination, out charsWritten);
        }

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatUInt32(_value, format, provider, utf8Destination, out bytesWritten);
        }
    }

    public partial struct Int64 : IFormattable, ISpanFormattable, IUtf8SpanFormattable
    {
        public override string ToString()
        {
            return Number.Int64ToDecStr(_value);
        }

        public string ToString(string? format)
        {
            return Number.FormatInt64(_value, format, null);
        }

        public string ToString(IFormatProvider? provider)
        {
            return Number.FormatInt64(_value, null, provider);
        }

        public string ToString(string? format, IFormatProvider? provider)
        {
            return Number.FormatInt64(_value, format, provider);
        }

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatInt64(_value, format, provider, destination, out charsWritten);
        }

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatInt64(_value, format, provider, utf8Destination, out bytesWritten);
        }
    }

    public partial struct UInt64 : IFormattable, ISpanFormattable, IUtf8SpanFormattable
    {
        // IBinaryInteger<ulong>.LeadingZeroCount, public static as in UInt64.cs;
        // the binary ("B") formatter calls it.
        public static ulong LeadingZeroCount(ulong value) => (ulong)BitOperations.LeadingZeroCount(value);

        public override string ToString()
        {
            return Number.UInt64ToDecStr(_value);
        }

        public string ToString(string? format)
        {
            return Number.FormatUInt64(_value, format, null);
        }

        public string ToString(IFormatProvider? provider)
        {
            return Number.UInt64ToDecStr(_value);
        }

        public string ToString(string? format, IFormatProvider? provider)
        {
            return Number.FormatUInt64(_value, format, provider);
        }

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatUInt64(_value, format, provider, destination, out charsWritten);
        }

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatUInt64(_value, format, provider, utf8Destination, out bytesWritten);
        }
    }

    public partial struct Single : IFormattable, ISpanFormattable, IUtf8SpanFormattable
    {
        /// <summary>Determines whether the specified value is finite (zero, subnormal, or normal).</summary>
        public static bool IsFinite(float f)
        {
            int bits = BitConverter.SingleToInt32Bits(f);
            return (bits & 0x7FFFFFFF) < 0x7F800000;
        }

        /// <summary>Determines whether the specified value is infinite.</summary>
        public static unsafe bool IsInfinity(float f)
        {
            int bits = BitConverter.SingleToInt32Bits(f);
            return (bits & 0x7FFFFFFF) == 0x7F800000;
        }

        /// <summary>Determines whether the specified value is NaN.</summary>
        public static unsafe bool IsNaN(float f)
        {
            // A NaN will never equal itself so this is an
            // easy and efficient way to check for NaN.

#pragma warning disable CS1718
            return f != f;
#pragma warning restore CS1718
        }

        /// <summary>Determines whether the specified value is negative.</summary>
        public static unsafe bool IsNegative(float f)
        {
            return BitConverter.SingleToInt32Bits(f) < 0;
        }

        /// <summary>Determines whether the specified value is negative infinity.</summary>
        public static unsafe bool IsNegativeInfinity(float f)
        {
            return f == float.NegativeInfinity;
        }

        /// <summary>Determines whether the specified value is normal.</summary>
        public static unsafe bool IsNormal(float f)
        {
            int bits = BitConverter.SingleToInt32Bits(f);
            bits &= 0x7FFFFFFF;
            return (bits < 0x7F800000) && (bits != 0) && ((bits & 0x7F800000) != 0);
        }

        /// <summary>Determines whether the specified value is positive infinity.</summary>
        public static unsafe bool IsPositiveInfinity(float f)
        {
            return f == float.PositiveInfinity;
        }

        /// <summary>Determines whether the specified value is subnormal.</summary>
        public static unsafe bool IsSubnormal(float f)
        {
            int bits = BitConverter.SingleToInt32Bits(f);
            bits &= 0x7FFFFFFF;
            return (bits < 0x7F800000) && (bits != 0) && ((bits & 0x7F800000) == 0);
        }

        public override string ToString()
        {
            return Number.FormatSingle(_value, null, NumberFormatInfo.CurrentInfo);
        }

        public string ToString(string? format)
        {
            return Number.FormatSingle(_value, format, NumberFormatInfo.CurrentInfo);
        }

        public string ToString(IFormatProvider? provider)
        {
            return Number.FormatSingle(_value, null, NumberFormatInfo.GetInstance(provider));
        }

        public string ToString(string? format, IFormatProvider? provider)
        {
            return Number.FormatSingle(_value, format, NumberFormatInfo.GetInstance(provider));
        }

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatSingle(_value, format, NumberFormatInfo.GetInstance(provider), destination, out charsWritten);
        }

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatSingle(_value, format, NumberFormatInfo.GetInstance(provider), utf8Destination, out bytesWritten);
        }
    }

    public partial struct Double : IFormattable, ISpanFormattable, IUtf8SpanFormattable
    {
        // IsNaN and IsInfinity are declared with the struct in MinimalRuntime.cs.

        /// <summary>Determines whether the specified value is finite (zero, subnormal, or normal).</summary>
        public static unsafe bool IsFinite(double d)
        {
            long bits = BitConverter.DoubleToInt64Bits(d);
            return (bits & 0x7FFFFFFFFFFFFFFF) < 0x7FF0000000000000;
        }

        /// <summary>Determines whether the specified value is negative.</summary>
        public static unsafe bool IsNegative(double d)
        {
            return BitConverter.DoubleToInt64Bits(d) < 0;
        }

        /// <summary>Determines whether the specified value is negative infinity.</summary>
        public static bool IsNegativeInfinity(double d)
        {
            return d == double.NegativeInfinity;
        }

        /// <summary>Determines whether the specified value is normal.</summary>
        public static unsafe bool IsNormal(double d)
        {
            long bits = BitConverter.DoubleToInt64Bits(d);
            bits &= 0x7FFFFFFFFFFFFFFF;
            return (bits < 0x7FF0000000000000) && (bits != 0) && ((bits & 0x7FF0000000000000) != 0);
        }

        /// <summary>Determines whether the specified value is positive infinity.</summary>
        public static bool IsPositiveInfinity(double d)
        {
            return d == double.PositiveInfinity;
        }

        /// <summary>Determines whether the specified value is subnormal.</summary>
        public static unsafe bool IsSubnormal(double d)
        {
            long bits = BitConverter.DoubleToInt64Bits(d);
            bits &= 0x7FFFFFFFFFFFFFFF;
            return (bits < 0x7FF0000000000000) && (bits != 0) && ((bits & 0x7FF0000000000000) == 0);
        }

        public override string ToString()
        {
            return Number.FormatDouble(_value, null, NumberFormatInfo.CurrentInfo);
        }

        public string ToString(string? format)
        {
            return Number.FormatDouble(_value, format, NumberFormatInfo.CurrentInfo);
        }

        public string ToString(IFormatProvider? provider)
        {
            return Number.FormatDouble(_value, null, NumberFormatInfo.GetInstance(provider));
        }

        public string ToString(string? format, IFormatProvider? provider)
        {
            return Number.FormatDouble(_value, format, NumberFormatInfo.GetInstance(provider));
        }

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatDouble(_value, format, NumberFormatInfo.GetInstance(provider), destination, out charsWritten);
        }

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        {
            return Number.TryFormatDouble(_value, format, NumberFormatInfo.GetInstance(provider), utf8Destination, out bytesWritten);
        }
    }

    // IntPtr.cs / UIntPtr.cs format through nint_t / nuint_t, which are
    // long / ulong on 64-bit - the only target here.
    public readonly partial struct IntPtr : IFormattable, ISpanFormattable, IUtf8SpanFormattable
    {
        public override string ToString() => ((long)_value).ToString();
        public string ToString(string? format) => ((long)_value).ToString(format);
        public string ToString(IFormatProvider? provider) => ((long)_value).ToString(provider);
        public string ToString(string? format, IFormatProvider? provider) => ((long)_value).ToString(format, provider);

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null) =>
            ((long)_value).TryFormat(destination, out charsWritten, format, provider);

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null) =>
            ((long)_value).TryFormat(utf8Destination, out bytesWritten, format, provider);
    }

    public readonly partial struct UIntPtr : IFormattable, ISpanFormattable, IUtf8SpanFormattable
    {
        public override string ToString() => ((ulong)_value).ToString();
        public string ToString(string? format) => ((ulong)_value).ToString(format);
        public string ToString(IFormatProvider? provider) => ((ulong)_value).ToString(provider);
        public string ToString(string? format, IFormatProvider? provider) => ((ulong)_value).ToString(format, provider);

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null) =>
            ((ulong)_value).TryFormat(destination, out charsWritten, format, provider);

        /// <inheritdoc cref="IUtf8SpanFormattable.TryFormat" />
        public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null) =>
            ((ulong)_value).TryFormat(utf8Destination, out bytesWritten, format, provider);
    }
}
