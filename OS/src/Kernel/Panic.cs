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
            // Heard even when the console is muted: a hosted program runs with
            // Console.Quiet (CoreClrHost), and a panic inside it — ExitProcess,
            // FailFast — halted with not a line in the log.
            Console.Quiet = false;
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
