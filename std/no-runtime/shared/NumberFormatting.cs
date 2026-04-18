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
