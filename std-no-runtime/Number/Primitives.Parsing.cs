// Ported from dotnet/runtime release/7.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Double.cs, Single.cs
// (the CoreLib snapshot in gc-experiment/dotnet-runtime). The Parse / TryParse statics of double and
// float, over Number.Parsing.cs. Bodies verbatim; the structs are partial and declared in
// OS/src/Boot/MinimalRuntime.cs (kernel) and apps_native/sdk/MinimalRuntime.cs (apps), like
// Primitives.Formatting.cs.
//
// SharpOS changes (marked inline):
//   * ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s) is spelled as a plain
//     throw: std's ThrowHelper has no ExceptionArgument enum.
// SharpOS cuts: the ISpanParsable / INumberBase generic-math interface members - no generic
// math interfaces in this std (see IUtfChar.cs).

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace System
{
    public partial struct Double
    {
        public static double Parse(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseDouble(s, NumberStyles.Float | NumberStyles.AllowThousands, NumberFormatInfo.CurrentInfo);
        }

        public static double Parse(string s, NumberStyles style)
        {
            NumberFormatInfo.ValidateParseStyleFloatingPoint(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: see above
            return Number.ParseDouble(s, style, NumberFormatInfo.CurrentInfo);
        }

        public static double Parse(string s, IFormatProvider? provider)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: see above
            return Number.ParseDouble(s, NumberStyles.Float | NumberStyles.AllowThousands, NumberFormatInfo.GetInstance(provider));
        }

        public static double Parse(string s, NumberStyles style, IFormatProvider? provider)
        {
            NumberFormatInfo.ValidateParseStyleFloatingPoint(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: see above
            return Number.ParseDouble(s, style, NumberFormatInfo.GetInstance(provider));
        }

        // Parses a double from a String in the given style.  If
        // a NumberFormatInfo isn't specified, the current culture's
        // NumberFormatInfo is assumed.
        //
        // This method will not throw an OverflowException, but will return
        // PositiveInfinity or NegativeInfinity for a number that is too
        // large or too small.

        public static double Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Float | NumberStyles.AllowThousands, IFormatProvider? provider = null)
        {
            NumberFormatInfo.ValidateParseStyleFloatingPoint(style);
            return Number.ParseDouble(s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static bool TryParse([NotNullWhen(true)] string? s, out double result)
        {
            if (s == null)
            {
                result = 0;
                return false;
            }

            return TryParse((ReadOnlySpan<char>)s, NumberStyles.Float | NumberStyles.AllowThousands, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse(ReadOnlySpan<char> s, out double result)
        {
            return TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out double result)
        {
            NumberFormatInfo.ValidateParseStyleFloatingPoint(style);

            if (s == null)
            {
                result = 0;
                return false;
            }

            return TryParse((ReadOnlySpan<char>)s, style, NumberFormatInfo.GetInstance(provider), out result);
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out double result)
        {
            NumberFormatInfo.ValidateParseStyleFloatingPoint(style);
            return TryParse(s, style, NumberFormatInfo.GetInstance(provider), out result);
        }

        private static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, NumberFormatInfo info, out double result)
        {
            return Number.TryParseDouble(s, style, info, out result);
        }

        public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out double result) => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider, out result);

        public static double Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider);

        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out double result) => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider, out result);
    }

    public partial struct Single
    {
        public static float Parse(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseSingle(s, NumberStyles.Float | NumberStyles.AllowThousands, NumberFormatInfo.CurrentInfo);
        }

        public static float Parse(string s, NumberStyles style)
        {
            NumberFormatInfo.ValidateParseStyleFloatingPoint(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: see above
            return Number.ParseSingle(s, style, NumberFormatInfo.CurrentInfo);
        }

        public static float Parse(string s, IFormatProvider? provider)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: see above
            return Number.ParseSingle(s, NumberStyles.Float | NumberStyles.AllowThousands, NumberFormatInfo.GetInstance(provider));
        }

        public static float Parse(string s, NumberStyles style, IFormatProvider? provider)
        {
            NumberFormatInfo.ValidateParseStyleFloatingPoint(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: see above
            return Number.ParseSingle(s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static float Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Float | NumberStyles.AllowThousands, IFormatProvider? provider = null)
        {
            NumberFormatInfo.ValidateParseStyleFloatingPoint(style);
            return Number.ParseSingle(s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static bool TryParse([NotNullWhen(true)] string? s, out float result)
        {
            if (s == null)
            {
                result = 0;
                return false;
            }

            return TryParse((ReadOnlySpan<char>)s, NumberStyles.Float | NumberStyles.AllowThousands, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse(ReadOnlySpan<char> s, out float result)
        {
            return TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out float result)
        {
            NumberFormatInfo.ValidateParseStyleFloatingPoint(style);

            if (s == null)
            {
                result = 0;
                return false;
            }

            return TryParse((ReadOnlySpan<char>)s, style, NumberFormatInfo.GetInstance(provider), out result);
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out float result)
        {
            NumberFormatInfo.ValidateParseStyleFloatingPoint(style);
            return TryParse(s, style, NumberFormatInfo.GetInstance(provider), out result);
        }

        private static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, NumberFormatInfo info, out float result)
        {
            return Number.TryParseSingle(s, style, info, out result);
        }

        public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out float result) => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider, out result);

        public static float Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider);

        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out float result) => TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, provider, out result);
    }
}
