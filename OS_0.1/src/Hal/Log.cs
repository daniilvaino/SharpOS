namespace OS.Hal
{
    internal enum LogLevel
    {
        Trace,
        Info,
        Warn,
        Error,
        Panic,
    }

    internal static class Log
    {
        public static void Write(LogLevel level, string message)
        {
            Begin(level);
            Console.WriteLine(message);
        }

        public static void Begin(LogLevel level)
        {
            Console.Write("[");
            Console.Write(LevelName(level));
            Console.Write("] ");
        }

        public static void EndLine()
        {
            Console.WriteLine("");
        }

        private static string LevelName(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Trace: return "trace";
                case LogLevel.Info: return "info";
                case LogLevel.Warn: return "warn";
                case LogLevel.Error: return "error";
                case LogLevel.Panic: return "PANIC";
                default: return "info";
            }
        }
    }
}
