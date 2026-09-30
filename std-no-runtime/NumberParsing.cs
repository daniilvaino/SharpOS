// Integer parsing core for the std primitives' Parse/TryParse statics
// (System.Int32.Parse lives on the struct in MinimalRuntime — both tiers
// forward here, same split as NumberFormatting). BCL semantics for the
// default NumberStyles.Integer: leading/trailing whitespace, optional
// sign, decimal digits only. Cuts: NumberStyles/IFormatProvider overloads,
// thousands separators, hex parsing.

namespace SharpOS.Std.NoRuntime
{
    public static class NumberParsing
    {
        public static bool TryParseInt32(string s, out int result)
        {
            result = 0;
            if (!TryParseInt64(s, out long value)) return false;
            if (value < int.MinValue || value > int.MaxValue) return false;
            result = (int)value;
            return true;
        }

        public static bool TryParseInt64(string s, out long result)
        {
            result = 0;
            if (s == null) return false;

            int i = 0;
            int n = s.Length;
            while (i < n && IsWhite(s[i])) i++;

            bool negative = false;
            if (i < n && (s[i] == '-' || s[i] == '+'))
            {
                negative = s[i] == '-';
                i++;
            }

            int digits = 0;
            ulong acc = 0;
            for (; i < n; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') break;
                ulong next = acc * 10 + (ulong)(c - '0');
                // Overflow gate: long.MaxValue + 1 covers long.MinValue's magnitude.
                if (next > (ulong)long.MaxValue + 1) return false;
                acc = next;
                digits++;
            }

            while (i < n && IsWhite(s[i])) i++;
            if (digits == 0 || i != n) return false;

            if (negative)
            {
                if (acc > (ulong)long.MaxValue + 1) return false;
                result = unchecked(-(long)acc);
                return true;
            }

            if (acc > (ulong)long.MaxValue) return false;
            result = (long)acc;
            return true;
        }

        private static bool IsWhite(char c)
            => c == ' ' || c == '\t' || c == '\r' || c == '\n';
    }
}
