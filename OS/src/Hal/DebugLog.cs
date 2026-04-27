namespace OS.Hal
{
    internal static class DebugLog
    {
        // Reentrancy guard. Number formatting (Console.WriteUInt /
        // WriteHex) allocates strings through KernelHeap, which can
        // call Log.Begin from inside its own GrowHeap path while we're
        // already mid-line. Without this guard the inner line tears
        // through the outer one. With the guard the inner Begin/Write/
        // EndLine become no-ops — we lose the diagnostic but the outer
        // line stays clean. Single-thread kernel, no atomicity needed.
        private static bool s_inLine;

        public static void Write(LogLevel level, string message)
        {
            if (s_inLine) return;
            s_inLine = true;
            UiText.Write("[");
            UiText.Write(LevelName(level));
            UiText.Write("] ");
            UiText.WriteLine(message);
            s_inLine = false;
        }

        public static void Begin(LogLevel level)
        {
            if (s_inLine) return;
            s_inLine = true;
            UiText.Write("[");
            UiText.Write(LevelName(level));
            UiText.Write("] ");
        }

        public static void EndLine()
        {
            if (!s_inLine) return;
            UiText.WriteLine("");
            s_inLine = false;
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
