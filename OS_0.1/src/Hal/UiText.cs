namespace OS.Hal
{
    internal static class UiText
    {
        public static void Write(string text)
        {
            Console.Write(text);
        }

        public static void WriteLine(string text)
        {
            Console.WriteLine(text);
        }

        public static void WriteChar(char value)
        {
            Console.WriteChar(value);
        }

        public static void WriteInt(int value)
        {
            Console.WriteInt(value);
        }

        public static void WriteUInt(uint value)
        {
            Console.WriteUInt(value);
        }

        public static void WriteULong(ulong value)
        {
            Console.WriteULong(value);
        }

        public static void WriteHex(ulong value)
        {
            Console.WriteHex(value);
        }

        public static void WriteHex(ulong value, int minDigits)
        {
            Console.WriteHex(value, minDigits);
        }
    }
}
