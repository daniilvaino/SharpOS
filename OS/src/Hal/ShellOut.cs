namespace OS.Hal
{
    // Phase B#3 sub-step 4 — shell output indirection. The engine always
    // writes to the serial Console (so the log/headless oracle is
    // unchanged); when the interactive REPL runs under SHARPOS_GUI=1 it
    // sets ToFb=true and the same calls also render to FbTty. No
    // reference-type statics (no cctor trap) — FbTty is itself static.
    internal static class ShellOut
    {
        public static bool ToFb;

        public static void WriteChar(char c)
        {
            Console.WriteChar(c);
            if (ToFb) FbTty.Putc(c);
        }

        public static void Write(string s)
        {
            Console.Write(s);
            if (ToFb) FbTty.Puts(s);
        }

        public static void WriteLine(string s)
        {
            Console.WriteLine(s);
            if (ToFb) { FbTty.Puts(s); FbTty.Putc('\n'); }
        }

        public static void WriteUInt(uint value)
        {
            Console.WriteUInt(value);
            if (!ToFb) return;
            if (value == 0) { FbTty.Putc('0'); return; }
            int n = 0;
            uint v = value;
            char[] buf = s_digits;                 // 10 digits max for uint
            while (v != 0 && n < 10) { buf[n++] = (char)('0' + (int)(v % 10)); v /= 10; }
            for (int i = n - 1; i >= 0; i--) FbTty.Putc(buf[i]);
        }

        private static readonly char[] s_digits = new char[10];
    }
}
