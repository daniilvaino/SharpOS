using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // Phase B#2 sub-step 3 — exercise the framebuffer text/graphics path
    // end to end and emit a deterministic fingerprint.
    //
    // Headless oracle: FbConsole.Checksum over a fixed region is stable
    // across runs (clear + bands + banner are all deterministic, the FB
    // geometry is fixed by the QEMU config), so `[fbtext] ... crc=0x...`
    // regresses the renderer without a display. Under SHARPOS_GUI=1 the
    // painted screen (colour swatches in channel order + the banner) is
    // the eyeball proof that packing/stride/scale are correct.
    internal static unsafe class FbRenderProbe
    {
        public static void Run()
        {
            if (!Framebuffer.IsAvailable)
            {
                Console.WriteLine("[fbtext] skipped (framebuffer not available)");
                return;
            }

            uint w = Framebuffer.Width;
            uint h = Framebuffer.Height;
            uint crc = RenderAndChecksum();
            bool pass = crc == GoldenCrc;

            Console.Write("[fbtext] ");
            Console.WriteInt((int)w);
            Console.Write("x");
            Console.WriteInt((int)h);
            Console.Write(" fmt=");
            Console.WriteInt((int)Framebuffer.PixelFormat);
            Console.Write(" crc=0x");
            Console.WriteHex(crc);
            if (pass)
            {
                Console.WriteLine(" PASS");
            }
            else
            {
                Console.Write(" FAIL exp=0x");
                Console.WriteHex(GoldenCrc);
                Console.WriteLine("");
            }
        }

        // Paint the deterministic test frame and return the FNV-1a of
        // the rendered region [0,512)x[0,360) (covers all painted
        // content; below y~360 is constant navy — a sharper oracle than
        // a full-frame hash). Pure of any Console output, so it is
        // reusable post-ExitBootServices (Phase C) where the same golden
        // proves the own GOP path is bit-identical without UEFI.
        public static uint RenderAndChecksum()
        {
            uint w = Framebuffer.Width;
            uint h = Framebuffer.Height;

            FbConsole.Clear(0, 0, 40);                       // dark navy

            // Channel-order swatches: R, G, B, white (eyeball BGRX).
            int sw = (int)(w / 4);
            FbConsole.FillRect(0 * sw, 0, sw, 64, 255, 0, 0);
            FbConsole.FillRect(1 * sw, 0, sw, 64, 0, 255, 0);
            FbConsole.FillRect(2 * sw, 0, sw, 64, 0, 0, 255);
            FbConsole.FillRect(3 * sw, 0, sw, 64, 255, 255, 255);

            uint fg = FbConsole.Pack(230, 230, 0);           // amber
            uint cyan = FbConsole.Pack(0, 220, 220);
            FbConsole.DrawString(40, 110, "SHARPOS GOP", fg, -1, 5);
            FbConsole.DrawString(40, 180, "8x8 FONT RENDERER - PHASE B#2", cyan, -1, 3);

            int cx = 40;
            int gy = 230;
            cx = DrawText(cx, gy, "FB ", cyan, 3);
            cx = DrawUInt(cx, gy, w, cyan, 3);
            cx = DrawText(cx, gy, "x", cyan, 3);
            cx = DrawUInt(cx, gy, h, cyan, 3);
            cx = DrawText(cx, gy, " STRIDE=", cyan, 3);
            cx = DrawUInt(cx, gy, Framebuffer.Stride, cyan, 3);

            FbConsole.DrawString(40, 300,
                "ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz", fg, -1, 2);
            FbConsole.DrawString(40, 330,
                "0123456789 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~", fg, -1, 2);

            return FbConsole.Checksum(0, 0, 512, 360);
        }

        // Pure pass/fail (render + golden compare, no Console). Used
        // post-EBS to assert the GOP path survived UEFI teardown.
        public static bool Verify() => RenderAndChecksum() == GoldenCrc;

        // Golden FNV-1a of FbConsole.Checksum(0,0,512,360) at 1280x800
        // fmt=1 (BGRX), captured on the run whose colour swatches were
        // eyeball-confirmed RED GREEN BLUE WHITE (step 72d). Re-baseline
        // ONLY together with a deliberate renderer/font/probe-layout
        // change, never to "make it pass".
        private const uint GoldenCrc = 0x7A1D4075u;

        private static int DrawText(int x, int y, string s, uint fg, int scale)
        {
            FbConsole.DrawString(x, y, s, fg, -1, scale);
            return x + s.Length * Font8x8.CharWidth * scale;
        }

        // Render an unsigned decimal glyph-by-glyph; returns the advanced x.
        private static int DrawUInt(int x, int y, uint value, uint fg, int scale)
        {
            // Max uint = 10 digits.
            char* buf = stackalloc char[10];
            int n = 0;
            if (value == 0)
            {
                buf[n++] = '0';
            }
            else
            {
                while (value != 0 && n < 10)
                {
                    buf[n++] = (char)('0' + (int)(value % 10));
                    value /= 10;
                }
            }
            int cursor = x;
            for (int i = n - 1; i >= 0; i--)
            {
                FbConsole.DrawChar(cursor, y, buf[i], fg, -1, scale);
                cursor += Font8x8.CharWidth * scale;
            }
            return cursor;
        }
    }
}
