// System.Boolean statics — ported from dotnet/runtime v8.0 (MIT),
// src/libraries/System.Private.CoreLib/src/System/Boolean.cs: TrueString,
// FalseString, Parse, TryParse (string and span), with the same trimming of
// white space and NUL. Cuts: the ISpanParsable / IUtf8SpanFormattable
// surface; the instance struct lives in each tier's MinimalRuntime.

namespace System
{
    public partial struct Boolean
    {
        internal const string TrueLiteral = "True";
        internal const string FalseLiteral = "False";

        public static readonly string TrueString = TrueLiteral;
        public static readonly string FalseString = FalseLiteral;

        public static bool Parse(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return Parse(value.AsSpan());
        }

        public static bool Parse(ReadOnlySpan<char> value)
            => TryParse(value, out bool result) ? result : throw new FormatException("String '" + new string(value) + "' was not recognized as a valid Boolean.");

        public static bool TryParse(string value, out bool result)
        {
            if (value == null)
            {
                result = false;
                return false;
            }
            return TryParse(value.AsSpan(), out result);
        }

        public static bool TryParse(ReadOnlySpan<char> value, out bool result)
        {
            if (IsTrueStringIgnoreCase(value))
            {
                result = true;
                return true;
            }
            if (IsFalseStringIgnoreCase(value))
            {
                result = false;
                return true;
            }
            // Special case: Trim whitespace as well as null characters.
            value = TrimWhiteSpaceAndNull(value);
            if (IsTrueStringIgnoreCase(value))
            {
                result = true;
                return true;
            }
            if (IsFalseStringIgnoreCase(value))
            {
                result = false;
                return true;
            }
            result = false;
            return false;
        }

        internal static bool IsTrueStringIgnoreCase(ReadOnlySpan<char> value)
            => value.Length == 4
               && (value[0] | 0x20) == 't' && (value[1] | 0x20) == 'r'
               && (value[2] | 0x20) == 'u' && (value[3] | 0x20) == 'e';

        internal static bool IsFalseStringIgnoreCase(ReadOnlySpan<char> value)
            => value.Length == 5
               && (value[0] | 0x20) == 'f' && (value[1] | 0x20) == 'a'
               && (value[2] | 0x20) == 'l' && (value[3] | 0x20) == 's'
               && (value[4] | 0x20) == 'e';

        private static ReadOnlySpan<char> TrimWhiteSpaceAndNull(ReadOnlySpan<char> value)
        {
            int start = 0;
            while (start < value.Length)
            {
                if (!char.IsWhiteSpace(value[start]) && value[start] != '\0') break;
                start++;
            }
            int end = value.Length - 1;
            while (end >= start)
            {
                if (!char.IsWhiteSpace(value[end]) && value[end] != '\0') break;
                end--;
            }
            return value.Slice(start, end - start + 1);
        }
    }
}
