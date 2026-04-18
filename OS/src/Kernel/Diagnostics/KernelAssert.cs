using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    internal static unsafe class KernelAssert
    {
        public static void True(bool condition, string message)
        {
            if (condition)
                return;

            Fail(message);
        }

        public static void False(bool condition, string message)
        {
            True(!condition, message);
        }

        public static void NotNull(void* pointer, string message)
        {
            True(pointer != null, message);
        }

        public static void Equal(uint expected, uint actual, string message)
        {
            if (expected == actual)
                return;

            WriteExpectedActual((ulong)expected, (ulong)actual);
            Fail(message);
        }

        public static void Equal(ulong expected, ulong actual, string message)
        {
            if (expected == actual)
                return;

            WriteExpectedActual(expected, actual);
            Fail(message);
        }

        public static void Equal(int expected, int actual, string message)
        {
            if (expected == actual)
                return;

            Log.Begin(LogLevel.Error);
            Console.Write("assert expected=");
            Console.WriteInt(expected);
            Console.Write(" actual=");
            Console.WriteInt(actual);
            Log.EndLine();

            Fail(message);
        }

        private static void WriteExpectedActual(ulong expected, ulong actual)
        {
            Log.Begin(LogLevel.Error);
            Console.Write("assert expected=0x");
            Console.WriteHex(expected, 8);
            Console.Write(" actual=0x");
            Console.WriteHex(actual, 8);
            Log.EndLine();
        }

        private static void Fail(string message)
        {
            Log.Begin(LogLevel.Error);
            Console.Write("assert failed: ");
            Console.WriteLine(message);
            Panic.Fail(message);
        }
    }
}
