namespace OS.Hal
{
    internal static class DebugLog
    {
        // Depth counter, balanced across Begin/EndLine.
        //
        // The bug this exists to prevent: number formatting allocates, heap
        // growth logs, so a line can open inside another line. With a plain
        // flag the INNER EndLine closed the OUTER line and cleared the flag,
        // and the outer line then ended without a newline — its verdict ran
        // into whatever printed next and stopped being machine-readable.
        //
        // Deliberately NOT tracking which thread owns the line, and NOT holding
        // preemption off for its duration. Both were tried today and both cost
        // more than the interleaving they prevented: dropping a second
        // thread's writes swallowed a panic message whole, suppressing
        // preemption hung the machine on the first unbalanced Begin, and
        // asking the scheduler who is running trips the static-constructor
        // trap on the earliest boot output. Lines from two threads may
        // interleave. That is cosmetic; silence is not.
        private static int s_depth;

        public static void Write(LogLevel level, string message)
        {
            Begin(level);
            UiText.Write(message);
            EndLine();
        }

        public static void Begin(LogLevel level)
        {
            if (s_depth++ > 0) return;
            UiText.Write("[");
            UiText.Write(LevelName(level));
            UiText.Write("] ");
        }

        public static void EndLine()
        {
            if (s_depth == 0) return;      // unbalanced EndLine — ignore
            if (--s_depth > 0) return;     // inner one: the outer line owns the newline
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
