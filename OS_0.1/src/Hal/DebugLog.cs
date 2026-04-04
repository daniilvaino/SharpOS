namespace OS.Hal
{
    internal static class DebugLog
    {
        public static void Write(LogLevel level, string message)
        {
            Begin(level);
            UiText.WriteLine(message);
        }

        public static void Begin(LogLevel level)
        {
            UiText.Write("[");
            UiText.Write(LevelName(level));
            UiText.Write("] ");
        }

        public static void EndLine()
        {
            UiText.WriteLine("");
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
