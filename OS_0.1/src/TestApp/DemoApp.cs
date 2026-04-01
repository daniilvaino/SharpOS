using Console = OS.Hal.Console;
using OS.Hal;

namespace OS.TestApp
{
    internal static class DemoApp
    {
        public static void Run()
        {
            Log.Write(LogLevel.Info, "demo start");

            for (int i = 0; i < 8; i++)
            {
                Console.Write("fib(");
                Console.WriteInt(i);
                Console.Write(")=");
                Console.WriteInt(Fib(i));
                Console.WriteLine("");
            }

            Log.Write(LogLevel.Info, "demo done");
        }

        private static int Fib(int n)
        {
            if (n <= 1)
                return n;

            int a = 0;
            int b = 1;
            for (int i = 2; i <= n; i++)
            {
                int next = a + b;
                a = b;
                b = next;
            }

            return b;
        }
    }
}
