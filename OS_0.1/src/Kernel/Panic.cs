using OS.Hal;
using OS.Kernel.Diagnostics;

namespace OS.Kernel
{
    internal static class Panic
    {
        private static PanicMode s_mode = PanicMode.Shutdown;

        public static PanicMode Mode
        {
            get => s_mode;
            set => s_mode = value;
        }

        public static void Fail(string message)
        {
            Log.Write(LogLevel.Panic, message);
            Console.WriteLine("System panic.");

            if (s_mode == PanicMode.ReturnToKernel)
                return;

            if (s_mode == PanicMode.Shutdown)
                Platform.Shutdown();

            Platform.Halt();
        }
    }
}
