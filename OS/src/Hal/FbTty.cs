namespace OS.Hal
{
    // Phase B#3 sub-step 4 — minimal text terminal over FbConsole: a
    // pixel cursor with newline, right-edge wrap, and clear-on-overflow
    // (no scroll-by-copy in v1 — a boot shell rarely fills the screen,
    // and a full clear is correct and cheap). Value-typed statics only
    // (no cctor trap). No-ops when the FB is unavailable, so callers
    // need no guard.
    internal static class FbTty
    {
        private const int Scale = 2;                 // 8x8 -> 16x16 cell
        private const int Margin = 8;

        private static int s_cx, s_cy;
        private static uint s_fg;
        private static byte s_br, s_bg, s_bb;        // background channels
        private static bool s_ready;

        public static void Init(byte fr, byte fgc, byte fbc, byte br, byte bg, byte bb)
        {
            if (!Framebuffer.IsAvailable) { s_ready = false; return; }
            s_fg = FbConsole.Pack(fr, fgc, fbc);
            s_br = br; s_bg = bg; s_bb = bb;
            FbConsole.Clear(br, bg, bb);
            s_cx = Margin;
            s_cy = Margin;
            s_ready = true;
        }

        private static int CellW => Font8x8.CharWidth * Scale;
        private static int CellH => Font8x8.CharHeight * Scale + 2;

        public static void NewLine()
        {
            if (!s_ready) return;
            s_cx = Margin;
            s_cy += CellH;
            if (s_cy + CellH > (int)Framebuffer.Height)
            {
                FbConsole.Clear(s_br, s_bg, s_bb);   // overflow: wipe + home
                s_cx = Margin;
                s_cy = Margin;
            }
        }

        public static void Putc(char ch)
        {
            if (!s_ready) return;
            if (ch == '\n') { NewLine(); return; }
            if (ch == '\r') return;
            if (ch < (char)0x20 || ch >= (char)0x7F) return;

            if (s_cx + CellW > (int)Framebuffer.Width - Margin) NewLine();
            FbConsole.DrawChar(s_cx, s_cy, ch, s_fg,
                (long)FbConsole.Pack(s_br, s_bg, s_bb), Scale);
            s_cx += CellW;
        }

        // Erase the previous cell and step the cursor back (Backspace).
        public static void Backspace()
        {
            if (!s_ready) return;
            if (s_cx <= Margin) return;              // don't cross line start
            s_cx -= CellW;
            FbConsole.FillRect(s_cx, s_cy, CellW, CellH, s_br, s_bg, s_bb);
        }

        public static void Puts(string s)
        {
            if (!s_ready || s == null) return;
            for (int i = 0; i < s.Length; i++) Putc(s[i]);
        }
    }
}
