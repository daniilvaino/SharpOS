// Ported from dotnet/runtime release/7.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Int32.cs, Int64.cs, UInt32.cs, UInt64.cs
// (the CoreLib snapshot in gc-experiment/dotnet-runtime). The Parse / TryParse statics
// of int, long, uint and ulong, over Number.Parsing.Integer.cs. Bodies verbatim; the
// structs are partial and declared in OS/src/Boot/MinimalRuntime.cs (kernel) and
// apps_native/sdk/MinimalRuntime.cs (apps), like Primitives.Parsing.cs. Replaces the
// Parse(string)/TryParse(string) pair those files had (over NumberParsing.cs, decimal
// only, style and provider ignored).
//
// SharpOS changes (marked inline):
//   * ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s) is spelled as a plain
//     throw: std's ThrowHelper has no such ExceptionArgument.
// SharpOS cuts: the IParsable / ISpanParsable / INumberBase interface declarations (no
// generic math interfaces in this std); their Parse/TryParse members are kept as plain
// statics.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace System
{
    public partial struct Int32
    {
        public static int Parse(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseInt32(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo);
        }

        public static int Parse(string s, NumberStyles style)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseInt32(s, style, NumberFormatInfo.CurrentInfo);
        }

        // Parses an integer from a String in the given style.  If
        // a NumberFormatInfo isn't specified, the current culture's
        // NumberFormatInfo is assumed.
        //
        public static int Parse(string s, IFormatProvider? provider)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseInt32(s, NumberStyles.Integer, NumberFormatInfo.GetInstance(provider));
        }

        // Parses an integer from a String in the given style.  If
        // a NumberFormatInfo isn't specified, the current culture's
        // NumberFormatInfo is assumed.
        //
        public static int Parse(string s, NumberStyles style, IFormatProvider? provider)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseInt32(s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static int Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider? provider = null)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return Number.ParseInt32(s, style, NumberFormatInfo.GetInstance(provider));
        }

        // Parses an integer from a String. Returns false rather
        // than throwing an exception if input is invalid.
        //
        public static bool TryParse([NotNullWhen(true)] string? s, out int result)
        {
            if (s == null)
            {
                result = 0;
                return false;
            }

            return Number.TryParseInt32IntegerStyle(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result) == Number.ParsingStatus.OK;
        }

        public static bool TryParse(ReadOnlySpan<char> s, out int result)
        {
            return Number.TryParseInt32IntegerStyle(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result) == Number.ParsingStatus.OK;
        }

        // Parses an integer from a String in the given style. Returns false rather
        // than throwing an exception if input is invalid.
        //
        public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out int result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);

            if (s == null)
            {
                result = 0;
                return false;
            }

            return Number.TryParseInt32(s, style, NumberFormatInfo.GetInstance(provider), out result) == Number.ParsingStatus.OK;
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out int result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return Number.TryParseInt32(s, style, NumberFormatInfo.GetInstance(provider), out result) == Number.ParsingStatus.OK;
        }

        // From the IParsable / ISpanParsable implementations (the interfaces are cut).
        public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out int result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static int Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out int result) => TryParse(s, NumberStyles.Integer, provider, out result);
    }

    public partial struct Int64
    {
        public static long Parse(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseInt64(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo);
        }

        public static long Parse(string s, NumberStyles style)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseInt64(s, style, NumberFormatInfo.CurrentInfo);
        }

        public static long Parse(string s, IFormatProvider? provider)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseInt64(s, NumberStyles.Integer, NumberFormatInfo.GetInstance(provider));
        }

        // Parses a long from a String in the given style.  If
        // a NumberFormatInfo isn't specified, the current culture's
        // NumberFormatInfo is assumed.
        //
        public static long Parse(string s, NumberStyles style, IFormatProvider? provider)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseInt64(s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static long Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider? provider = null)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return Number.ParseInt64(s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static bool TryParse([NotNullWhen(true)] string? s, out long result)
        {
            if (s == null)
            {
                result = 0;
                return false;
            }

            return Number.TryParseInt64IntegerStyle(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result) == Number.ParsingStatus.OK;
        }

        public static bool TryParse(ReadOnlySpan<char> s, out long result)
        {
            return Number.TryParseInt64IntegerStyle(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result) == Number.ParsingStatus.OK;
        }

        public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out long result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);

            if (s == null)
            {
                result = 0;
                return false;
            }

            return Number.TryParseInt64(s, style, NumberFormatInfo.GetInstance(provider), out result) == Number.ParsingStatus.OK;
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out long result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return Number.TryParseInt64(s, style, NumberFormatInfo.GetInstance(provider), out result) == Number.ParsingStatus.OK;
        }

        // From the IParsable / ISpanParsable implementations (the interfaces are cut).
        public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out long result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static long Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out long result) => TryParse(s, NumberStyles.Integer, provider, out result);
    }

    public partial struct UInt32
    {
        public static uint Parse(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseUInt32(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo);
        }

        public static uint Parse(string s, NumberStyles style)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseUInt32(s, style, NumberFormatInfo.CurrentInfo);
        }

        public static uint Parse(string s, IFormatProvider? provider)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseUInt32(s, NumberStyles.Integer, NumberFormatInfo.GetInstance(provider));
        }

        public static uint Parse(string s, NumberStyles style, IFormatProvider? provider)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseUInt32(s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static uint Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider? provider = null)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return Number.ParseUInt32(s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static bool TryParse([NotNullWhen(true)] string? s, out uint result)
        {
            if (s == null)
            {
                result = 0;
                return false;
            }

            return Number.TryParseUInt32IntegerStyle(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result) == Number.ParsingStatus.OK;
        }

        public static bool TryParse(ReadOnlySpan<char> s, out uint result)
        {
            return Number.TryParseUInt32IntegerStyle(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result) == Number.ParsingStatus.OK;
        }

        public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out uint result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);

            if (s == null)
            {
                result = 0;
                return false;
            }

            return Number.TryParseUInt32(s, style, NumberFormatInfo.GetInstance(provider), out result) == Number.ParsingStatus.OK;
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out uint result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return Number.TryParseUInt32(s, style, NumberFormatInfo.GetInstance(provider), out result) == Number.ParsingStatus.OK;
        }

        // From the IParsable / ISpanParsable implementations (the interfaces are cut).
        public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out uint result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static uint Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out uint result) => TryParse(s, NumberStyles.Integer, provider, out result);
    }

    public partial struct UInt64
    {
        public static ulong Parse(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseUInt64(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo);
        }

        public static ulong Parse(string s, NumberStyles style)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseUInt64(s, style, NumberFormatInfo.CurrentInfo);
        }

        public static ulong Parse(string s, IFormatProvider? provider)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseUInt64(s, NumberStyles.Integer, NumberFormatInfo.GetInstance(provider));
        }

        public static ulong Parse(string s, NumberStyles style, IFormatProvider? provider)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Number.ParseUInt64(s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static ulong Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider? provider = null)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return Number.ParseUInt64(s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static bool TryParse([NotNullWhen(true)] string? s, out ulong result)
        {
            if (s == null)
            {
                result = 0;
                return false;
            }

            return Number.TryParseUInt64IntegerStyle(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result) == Number.ParsingStatus.OK;
        }

        public static bool TryParse(ReadOnlySpan<char> s, out ulong result)
        {
            return Number.TryParseUInt64IntegerStyle(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result) == Number.ParsingStatus.OK;
        }

        public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out ulong result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);

            if (s == null)
            {
                result = 0;
                return false;
            }

            return Number.TryParseUInt64(s, style, NumberFormatInfo.GetInstance(provider), out result) == Number.ParsingStatus.OK;
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out ulong result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return Number.TryParseUInt64(s, style, NumberFormatInfo.GetInstance(provider), out result) == Number.ParsingStatus.OK;
        }

        // From the IParsable / ISpanParsable implementations (the interfaces are cut).
        public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ulong result) => TryParse(s, NumberStyles.Integer, provider, out result);
        public static ulong Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s, NumberStyles.Integer, provider);
        public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ulong result) => TryParse(s, NumberStyles.Integer, provider, out result);
    }
}
