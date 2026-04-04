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
            DebugLog.Write(level, message);
        }

        public static void Begin(LogLevel level)
        {
            DebugLog.Begin(level);
        }

        public static void EndLine()
        {
            DebugLog.EndLine();
        }
    }
}
