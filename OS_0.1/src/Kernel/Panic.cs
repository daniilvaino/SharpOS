using OS.Hal;

namespace OS.Kernel
{
    internal static class Panic
    {
        public static void Fail(string message)
        {
            Log.Write(LogLevel.Panic, message);
            Console.WriteLine("System halted.");
            Platform.Halt();
            while (true) ;
        }
    }
}
