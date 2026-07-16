namespace SharpOS.Std.NoRuntime
{
    internal static unsafe class NumberFormatting
    {
        private const string Zero = "0";
        private const string IntMinValue = "-2147483648";
        private const string LongMinValue = "-9223372036854775808";

        public static string UIntToString(uint value)
        {
            if (value == 0)
                return Zero;

            int digits = CountDecimalDigits(value);
            string result = StringRuntime.FastAllocateString(digits);
            if (result.Length != digits)
                return result;

            fixed (char* dst = &result.GetPinnableReference())
            {
                for (int i = digits - 1; i >= 0; i--)
                {
                    dst[i] = (char)('0' + (value % 10));
                    value /= 10;
                }
            }

            return result;
        }

        public static string IntToString(int value)
        {
            if (value == 0)
                return Zero;

            if (value == -2147483648)
                return IntMinValue;

            bool negative = value < 0;
            uint abs = negative ? (uint)(-value) : (uint)value;

            int digits = CountDecimalDigits(abs);
            int total = negative ? digits + 1 : digits;

            string result = StringRuntime.FastAllocateString(total);
            if (result.Length != total)
                return result;

            fixed (char* dst = &result.GetPinnableReference())
            {
                if (negative)
                    dst[0] = '-';

                int start = negative ? 1 : 0;
                for (int i = total - 1; i >= start; i--)
                {
                    dst[i] = (char)('0' + (abs % 10));
                    abs /= 10;
                }
            }

            return result;
        }

        public static string ULongToString(ulong value)
        {
            if (value == 0)
                return Zero;

            int digits = CountDecimalDigits(value);
            string result = StringRuntime.FastAllocateString(digits);
            if (result.Length != digits)
                return result;

            fixed (char* dst = &result.GetPinnableReference())
            {
                for (int i = digits - 1; i >= 0; i--)
                {
                    dst[i] = (char)('0' + (value % 10));
                    value /= 10;
                }
            }

            return result;
        }

        public static string LongToString(long value)
        {
            if (value == 0)
                return Zero;

            if (value == unchecked((long)0x8000000000000000UL))
                return LongMinValue;

            bool negative = value < 0;
            ulong abs = negative ? (ulong)(-value) : (ulong)value;

            int digits = CountDecimalDigits(abs);
            int total = negative ? digits + 1 : digits;

            string result = StringRuntime.FastAllocateString(total);
            if (result.Length != total)
                return result;

            fixed (char* dst = &result.GetPinnableReference())
            {
                if (negative)
                    dst[0] = '-';

                int start = negative ? 1 : 0;
                for (int i = total - 1; i >= start; i--)
                {
                    dst[i] = (char)('0' + (abs % 10));
                    abs /= 10;
                }
            }

            return result;
        }

        // ---- format-string subset (step141, ManagedDoom callsites) ----
        // Supported: all-'0' patterns ("00", "000") and "D<n>" — zero-pad to
        // width; "0.0…" via the double path. Unknown formats fall back to the
        // plain decimal form rather than throwing.

        public static string FormatInt64(long value, string format)
        {
            if (format == null || format.Length == 0) return LongToString(value);

            char c0 = format[0];
            if (c0 == 'D' || c0 == 'd')
            {
                int width = format.Length == 1 ? 1 : ParseFormatWidth(format, 1);
                if (width >= 0) return ZeroPad(value, width);
                return LongToString(value);
            }

            bool allZeros = true;
            int dot = -1;
            for (int i = 0; i < format.Length; i++)
            {
                char c = format[i];
                if (c == '.' && dot < 0) { dot = i; continue; }
                if (c != '0') { allZeros = false; break; }
            }
            if (allZeros && dot < 0) return ZeroPad(value, format.Length);
            if (allZeros) return DoubleToString(value, format.Length - dot - 1, false);

            return LongToString(value);
        }

        private static int ParseFormatWidth(string format, int start)
        {
            int width = 0;
            for (int i = start; i < format.Length; i++)
            {
                char c = format[i];
                if (c < '0' || c > '9') return -1;
                width = width * 10 + (c - '0');
            }
            return width;
        }

        private static string ZeroPad(long value, int width)
        {
            string s = LongToString(value);
            bool neg = s[0] == '-';
            int digits = neg ? s.Length - 1 : s.Length;
            if (digits >= width) return s;

            char[] buf = new char[width + (neg ? 1 : 0)];
            int pos = 0;
            if (neg) buf[pos++] = '-';
            for (int i = 0; i < width - digits; i++) buf[pos++] = '0';
            for (int i = neg ? 1 : 0; i < s.Length; i++) buf[pos++] = s[i];
            return new string(buf);
        }

        // ---- double formatting (step141) ----
        // Fixed-point only: scale-round-split. Callers are small values
        // (fps counters, gamma); |value| must stay well inside long range
        // after scaling. Default form trims trailing fraction zeros.

        public static string DoubleToString(double value) => DoubleToString(value, 6, true);

        public static string DoubleToString(double value, string format)
        {
            if (format == null || format.Length == 0) return DoubleToString(value);
            int dot = -1;
            for (int i = 0; i < format.Length; i++)
            {
                if (format[i] == '.') { dot = i; break; }
            }
            int frac = dot < 0 ? 0 : format.Length - dot - 1;
            return DoubleToString(value, frac, false);
        }

        public static string DoubleToString(double value, int fracDigits, bool trimTrailingZeros)
        {
            if (double.IsNaN(value)) return "NaN";
            if (value > double.MaxValue) return "Infinity";
            if (value < double.MinValue) return "-Infinity";

            bool neg = value < 0;
            double v = neg ? -value : value;

            long scale = 1;
            for (int i = 0; i < fracDigits; i++) scale *= 10;

            long scaled = (long)(v * scale + 0.5);
            long intPart = scaled / scale;
            long fracPart = scaled - intPart * scale;

            string ip = LongToString(intPart);
            int fracLen = fracDigits;
            char[] fracBuf = null;
            if (fracDigits > 0)
            {
                fracBuf = new char[fracDigits];
                long f = fracPart;
                for (int i = fracDigits - 1; i >= 0; i--)
                {
                    fracBuf[i] = (char)('0' + (int)(f % 10));
                    f /= 10;
                }
                if (trimTrailingZeros)
                {
                    while (fracLen > 0 && fracBuf[fracLen - 1] == '0') fracLen--;
                }
            }

            int total = (neg ? 1 : 0) + ip.Length + (fracLen > 0 ? 1 + fracLen : 0);
            char[] buf = new char[total];
            int pos = 0;
            if (neg) buf[pos++] = '-';
            for (int i = 0; i < ip.Length; i++) buf[pos++] = ip[i];
            if (fracLen > 0)
            {
                buf[pos++] = '.';
                for (int i = 0; i < fracLen; i++) buf[pos++] = fracBuf[i];
            }
            return new string(buf);
        }

        public static string UIntToHex(uint value, int minDigits)
        {
            return ULongToHex(value, ClampHexDigits(minDigits, 8));
        }

        public static string ULongToHex(ulong value, int minDigits)
        {
            int digits = CountHexDigits(value);
            int clamped = ClampHexDigits(minDigits, 16);
            if (digits < clamped)
                digits = clamped;

            string result = StringRuntime.FastAllocateString(digits);
            if (result.Length != digits)
                return result;

            fixed (char* dst = &result.GetPinnableReference())
            {
                for (int i = digits - 1; i >= 0; i--)
                {
                    int nibble = (int)(value & 0xFUL);
                    dst[i] = (char)(nibble < 10 ? ('0' + nibble) : ('A' + (nibble - 10)));
                    value >>= 4;
                }
            }

            return result;
        }

        private static int CountDecimalDigits(uint value)
        {
            int digits = 0;
            do
            {
                digits++;
                value /= 10;
            } while (value > 0);
            return digits;
        }

        private static int CountDecimalDigits(ulong value)
        {
            int digits = 0;
            do
            {
                digits++;
                value /= 10;
            } while (value > 0);
            return digits;
        }

        private static int CountHexDigits(ulong value)
        {
            int digits = 0;
            do
            {
                digits++;
                value >>= 4;
            } while (value != 0);
            return digits;
        }

        private static int ClampHexDigits(int requested, int max)
        {
            if (requested < 1) return 1;
            if (requested > max) return max;
            return requested;
        }
    }
}
