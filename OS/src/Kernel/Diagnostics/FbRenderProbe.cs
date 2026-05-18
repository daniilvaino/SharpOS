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

            // Background: dark navy.
            FbConsole.Clear(0, 0, 40);

            // Channel-order swatches across the top: R, G, B, white. If
            // the red block looks blue under GUI, Pack()/PixelFormat is
            // wrong — a defect the headless crc also moves.
            int sw = (int)(w / 4);
            FbConsole.FillRect(0 * sw, 0, sw, 64, 255, 0, 0);
            FbConsole.FillRect(1 * sw, 0, sw, 64, 0, 255, 0);
            FbConsole.FillRect(2 * sw, 0, sw, 64, 0, 0, 255);
            FbConsole.FillRect(3 * sw, 0, sw, 64, 255, 255, 255);

            uint fg = FbConsole.Pack(230, 230, 0);   // amber text
            uint cyan = FbConsole.Pack(0, 220, 220);
            FbConsole.DrawString(40, 110, "SHARPOS GOP", fg, -1, 5);
            FbConsole.DrawString(40, 180, "8x8 FONT RENDERER - PHASE B#2", cyan, -1, 3);

            // Geometry line, numbers rendered glyph-by-glyph (no string
            // allocation): "FB <w>x<h> stride=<s>".
            int cx = 40;
            int gy = 230;
            cx = DrawText(cx, gy, "FB ", cyan, 3);
            cx = DrawUInt(cx, gy, w, cyan, 3);
            cx = DrawText(cx, gy, "x", cyan, 3);
            cx = DrawUInt(cx, gy, h, cyan, 3);
            cx = DrawText(cx, gy, " STRIDE=", cyan, 3);
            cx = DrawUInt(cx, gy, Framebuffer.Stride, cyan, 3);

            // Full printable-ASCII strip — proves the whole glyph table,
            // not just the banner letters.
            FbConsole.DrawString(40, 300,
                "ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz", fg, -1, 2);
            FbConsole.DrawString(40, 330,
                "0123456789 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~", fg, -1, 2);

            // Deterministic fingerprint of the rendered region.
            uint crc = FbConsole.Checksum(0, 0, 512, 360);

            Console.Write("[fbtext] ");
            Console.WriteInt((int)w);
            Console.Write("x");
            Console.WriteInt((int)h);
            Console.Write(" fmt=");
            Console.WriteInt((int)Framebuffer.PixelFormat);
            Console.Write(" crc=0x");
            Console.WriteHex(crc);
            Console.WriteLine("");
        }

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
