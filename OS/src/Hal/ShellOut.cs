namespace OS.Hal
{
    // Phase B#3 sub-step 4 — shell output indirection. The engine always
    // writes to the serial Console; when the interactive REPL runs it
    // sets ToFb=true and the same calls also render to FbTty.
    //
    // Post-EBS subtlety: once UEFI is gone, Platform.WriteChar (i.e.
    // Console.*) itself routes to the own UART + FbTty (the reroute).
    // So the FbTty mirror here would draw every glyph TWICE (cursor
    // double-advances → garbled). FbHere therefore suppresses the
    // mirror once the console already owns the substrate — single copy
    // in both eras. No reference-type statics (no cctor trap).
    internal static class ShellOut
    {
        public static bool ToFb;

        // Mirror to FbTty only when requested AND Console isn't already
        // rendering to it (pre-EBS Console = UEFI ConOut, no FB).
        private static bool FbHere => ToFb && !Platform.BootServicesGone;

        public static void WriteChar(char c)
        {
            Console.WriteChar(c);
            if (FbHere) FbTty.Putc(c);
        }

        public static void Write(string s)
        {
            Console.Write(s);
            if (FbHere) FbTty.Puts(s);
        }

        public static void WriteLine(string s)
        {
            Console.WriteLine(s);
            if (FbHere) { FbTty.Puts(s); FbTty.Putc('\n'); }
        }

        public static void WriteUInt(uint value)
        {
            Console.WriteUInt(value);
            if (!FbHere) return;
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
