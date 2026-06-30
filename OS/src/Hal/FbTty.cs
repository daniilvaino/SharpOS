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
        private const int Scale = 1;                 // 8x8 pixel-for-pixel
        private const int Margin = 8;

        // Minimal ANSI/VT escape parser state — for SGR colors (\e[31m,
        // \e[1;31m, reset \e[0m, bright 90-97) and cursor cmds (\e[D, \e[K,
        // \e[2J). Anything we don't recognize gets eaten quietly so PS
        // output isn't littered with raw "ESC[..." byte sequences.
        private enum EscState : byte { Normal, Esc, Csi }
        private static EscState s_esc;
        private static int s_paramAccum;
        private static int s_paramCount;             // number of completed params before s_paramAccum
        // 8 SGR params is enough — PS rarely chains more.
        private static int s_param0, s_param1, s_param2, s_param3,
                          s_param4, s_param5, s_param6, s_param7;

        private static int s_cx, s_cy;
        private static uint s_fg;
        private static uint s_fgDefault;             // captured at Init for ESC[0m
        private static bool s_bold;
        private static byte s_br, s_bg, s_bb;        // background channels
        private static bool s_ready;

        public static void Init(byte fr, byte fgc, byte fbc, byte br, byte bg, byte bb)
        {
            if (!Framebuffer.IsAvailable) { s_ready = false; return; }
            s_fg = FbConsole.Pack(fr, fgc, fbc);
            s_fgDefault = s_fg;
            s_br = br; s_bg = bg; s_bb = bb;
            FbConsole.Clear(br, bg, bb);
            s_cx = Margin;
            s_cy = Margin;
            s_ready = true;
            s_esc = EscState.Normal;
            s_paramCount = 0; s_paramAccum = 0;
            s_bold = false;
        }

        // Map SGR code to RGB. Dim variants used when s_bold == false;
        // bold/bright maps either via 1; prefix or 90-97 codes.
        private static uint SgrColor(int code, bool bright)
        {
            // VGA-ish palette tuned to be readable on dark background.
            switch (code)
            {
                case 30: return bright ? FbConsole.Pack(0x55, 0x55, 0x55) : FbConsole.Pack(0x00, 0x00, 0x00);
                case 31: return bright ? FbConsole.Pack(0xFF, 0x55, 0x55) : FbConsole.Pack(0xAA, 0x00, 0x00);
                case 32: return bright ? FbConsole.Pack(0x55, 0xFF, 0x55) : FbConsole.Pack(0x00, 0xAA, 0x00);
                case 33: return bright ? FbConsole.Pack(0xFF, 0xFF, 0x55) : FbConsole.Pack(0xAA, 0x55, 0x00);
                case 34: return bright ? FbConsole.Pack(0x55, 0x55, 0xFF) : FbConsole.Pack(0x00, 0x00, 0xAA);
                case 35: return bright ? FbConsole.Pack(0xFF, 0x55, 0xFF) : FbConsole.Pack(0xAA, 0x00, 0xAA);
                case 36: return bright ? FbConsole.Pack(0x55, 0xFF, 0xFF) : FbConsole.Pack(0x00, 0xAA, 0xAA);
                case 37: return bright ? FbConsole.Pack(0xFF, 0xFF, 0xFF) : FbConsole.Pack(0xAA, 0xAA, 0xAA);
                default: return s_fgDefault;
            }
        }

        private static void ApplySgr()
        {
            // Walk all collected params. ESC[m == ESC[0m.
            int n = s_paramCount + 1;
            for (int i = 0; i < n; i++)
            {
                int p = i == 0 ? s_param0 : (i == 1 ? s_param1 : (i == 2 ? s_param2 :
                        (i == 3 ? s_param3 : (i == 4 ? s_param4 : (i == 5 ? s_param5 :
                        (i == 6 ? s_param6 : s_param7))))));
                if (p == 0) { s_fg = s_fgDefault; s_bold = false; }
                else if (p == 1) s_bold = true;
                else if (p == 22) s_bold = false;
                else if (p >= 30 && p <= 37) s_fg = SgrColor(p, s_bold);
                else if (p >= 90 && p <= 97) s_fg = SgrColor(p - 60, true);
                // Background (40-47, 100-107), 256/24-bit (38;5/2;...) — ignored
                // for now (single-bg-color FbTty).
            }
        }

        private static void ResetCsi()
        {
            s_paramCount = 0;
            s_paramAccum = 0;
            s_param0 = 0; s_param1 = 0; s_param2 = 0; s_param3 = 0;
            s_param4 = 0; s_param5 = 0; s_param6 = 0; s_param7 = 0;
        }

        private static void StoreParam()
        {
            switch (s_paramCount)
            {
                case 0: s_param0 = s_paramAccum; break;
                case 1: s_param1 = s_paramAccum; break;
                case 2: s_param2 = s_paramAccum; break;
                case 3: s_param3 = s_paramAccum; break;
                case 4: s_param4 = s_paramAccum; break;
                case 5: s_param5 = s_paramAccum; break;
                case 6: s_param6 = s_paramAccum; break;
                case 7: s_param7 = s_paramAccum; break;
            }
        }

        private static void HandleCsiFinal(char cmd)
        {
            // Final param (or zero if no digits before the cmd).
            StoreParam();
            if (cmd == 'm') ApplySgr();
            else if (cmd == 'J' && s_param0 == 2) Clear();
            else if (cmd == 'H') { s_cx = Margin; s_cy = Margin; }
            else if (cmd == 'D')
            {
                int n = s_param0 == 0 ? 1 : s_param0;
                while (n-- > 0 && s_cx > Margin) s_cx -= CellW;
            }
            else if (cmd == 'K')
            {
                // Erase to end of line.
                int w = (int)Framebuffer.Width - Margin - s_cx;
                if (w > 0) FbConsole.FillRect(s_cx, s_cy, w, CellH, s_br, s_bg, s_bb);
            }
            // Other CSI cmds (A/B/C/E/F/G/L/M/P/S/T/X/...) — accept-and-ignore.
        }

        private static int CellW => Font8x8.CharWidth * Scale;
        private static int CellH => Font8x8.CharHeight * Scale + 2;

        // Wipe the screen AND home the cursor — what a `clear` command
        // means. FbConsole.Clear alone leaves the cursor where it was,
        // so output continued mid-screen with a blank top. No-op until
        // Init (non-interactive: nothing rendered anyway).
        public static void Clear()
        {
            if (!s_ready) return;
            FbConsole.Clear(s_br, s_bg, s_bb);
            s_cx = Margin;
            s_cy = Margin;
        }

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

            // ANSI escape state machine. ESC `\x1B` starts; '[' enters CSI;
            // digits/';' accumulate parameters; a final byte in @-~ ends the
            // sequence. Anything malformed snaps back to Normal so a single
            // stray byte doesn't permanently swallow output.
            switch (s_esc)
            {
                case EscState.Esc:
                    if (ch == '[') { ResetCsi(); s_esc = EscState.Csi; return; }
                    s_esc = EscState.Normal;          // unrecognized — discard one char
                    return;
                case EscState.Csi:
                    if (ch >= '0' && ch <= '9')
                    {
                        s_paramAccum = s_paramAccum * 10 + (ch - '0');
                        return;
                    }
                    if (ch == ';')
                    {
                        StoreParam();
                        if (s_paramCount < 7) s_paramCount++;
                        s_paramAccum = 0;
                        return;
                    }
                    if (ch >= 0x40 && ch <= 0x7E) { HandleCsiFinal(ch); s_esc = EscState.Normal; return; }
                    // intermediate byte (space, '?'): swallow
                    return;
                default: break;
            }
            if (ch == 0x1B) { s_esc = EscState.Esc; return; }

            if (ch == '\n') { NewLine(); return; }
            if (ch == '\r') return;
            if (ch == 0x08) { Backspace(); return; }
            if (!Font8x8.IsRenderable(ch)) return;

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
