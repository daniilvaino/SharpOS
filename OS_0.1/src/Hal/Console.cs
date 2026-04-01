namespace OS.Hal
{
    internal static unsafe class Console
    {
        public static void Write(string text) => Platform.Write(text);

        public static void WriteLine(string text) => Platform.WriteLine(text);

        public static void WriteChar(char value) => Platform.WriteChar(value);

        public static void WriteInt(int value)
        {
            if (value == 0)
            {
                WriteChar('0');
                return;
            }

            if (value < 0)
            {
                if (value == -2147483648)
                {
                    Write("-2147483648");
                    return;
                }

                WriteChar('-');
                value = -value;
            }

            WriteUInt((uint)value);
        }

        public static void WriteUInt(uint value)
        {
            if (value == 0)
            {
                WriteChar('0');
                return;
            }

            char* digits = stackalloc char[10];
            int len = 0;

            while (value > 0)
            {
                uint digit = value % 10;
                digits[len++] = (char)('0' + digit);
                value /= 10;
            }

            for (int i = len - 1; i >= 0; i--)
                WriteChar(digits[i]);
        }

        public static void WriteULong(ulong value)
        {
            if (value == 0)
            {
                WriteChar('0');
                return;
            }

            char* digits = stackalloc char[20];
            int len = 0;

            while (value > 0)
            {
                ulong digit = value % 10;
                digits[len++] = (char)('0' + digit);
                value /= 10;
            }

            for (int i = len - 1; i >= 0; i--)
                WriteChar(digits[i]);
        }

        public static void WriteHex(ulong value)
        {
            WriteHex(value, 1);
        }

        public static void WriteHex(ulong value, int minDigits)
        {
            if (minDigits < 1)
                minDigits = 1;
            else if (minDigits > 16)
                minDigits = 16;

            char* digits = stackalloc char[16];
            int len = 0;

            do
            {
                int nibble = (int)(value & 0xFUL);
                digits[len++] = (char)(nibble < 10 ? ('0' + nibble) : ('A' + (nibble - 10)));
                value >>= 4;
            } while (value != 0);

            while (len < minDigits)
                digits[len++] = '0';

            for (int i = len - 1; i >= 0; i--)
                WriteChar(digits[i]);
        }
    }
}
