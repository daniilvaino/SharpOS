namespace OS.Hal
{
    // Phase B#3 sub-step 2 — line buffer fed by decoded PS/2 keys.
    //
    // Pure logic only: no FbConsole / hardware dependency, so the
    // headless oracle can drive Feed() with a synthetic key stream and
    // assert the resulting buffer exactly. The interactive echo loop
    // (poll Ps2Keyboard -> Decode -> Feed -> repaint via FbConsole)
    // lives in the shell sub-step, where real keystrokes arrive under
    // SHARPOS_GUI=1. Single-line, no history, no cursor movement —
    // printable insert + Backspace + Enter is all a boot shell needs.
    //
    // Backing store is a static char[] (safe in Phase 4, post
    // GcStaticsMaterializer — same as Font8x8). Single-threaded boot:
    // one shared editor instance is fine.
    internal static class LineEditor
    {
        public const int Capacity = 128;

        private static readonly char[] s_buf = new char[Capacity];
        private static int s_len;

        public static int Length => s_len;
        public static char[] Buffer => s_buf;

        public static void Reset() => s_len = 0;

        public enum Status { None, Changed, Submitted }

        // Apply one decoded key. Printable ASCII inserts (until full);
        // Backspace deletes the last char; Enter submits. Everything
        // else (Escape, Control, unmapped) is ignored here — callers
        // that care about Escape inspect the KeyKind themselves.
        public static Status Feed(Ps2Keyboard.KeyKind kind, char ch)
        {
            switch (kind)
            {
                case Ps2Keyboard.KeyKind.Char:
                    if (s_len < Capacity && ch >= (char)0x20 && ch < (char)0x7F)
                    {
                        s_buf[s_len++] = ch;
                        return Status.Changed;
                    }
                    return Status.None;

                case Ps2Keyboard.KeyKind.Backspace:
                    if (s_len > 0) { s_len--; return Status.Changed; }
                    return Status.None;

                case Ps2Keyboard.KeyKind.Enter:
                    return Status.Submitted;

                default:
                    return Status.None;
            }
        }

        // Exact match of the current buffer against s (no allocation).
        public static bool Matches(string s)
        {
            if (s == null || s.Length != s_len) return false;
            for (int i = 0; i < s_len; i++)
                if (s_buf[i] != s[i]) return false;
            return true;
        }
    }
}
