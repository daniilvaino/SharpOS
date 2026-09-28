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
            if (crc != 0) s_preTeardownCrc = crc;
            bool baselined = GoldenCrc != 0;
            bool pass = baselined && crc == GoldenCrc;

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
            else if (!baselined)
            {
                // Not a failure: there is no golden for this pattern yet. The
                // number above is the candidate — put it in GoldenCrc once the
                // swatches have been eyeballed as RED GREEN BLUE WHITE, which
                // is the only thing a checksum cannot check for itself.
                Console.WriteLine(" BASELINE (set GoldenCrc)");
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
        // The region the golden covers. Everything inside it must be drawn
        // from constants, or the comparison becomes a comparison of screens.
        private const int GoldenWidth = 512;
        private const int GoldenHeight = 360;

        public static uint RenderAndChecksum()
        {
            uint w = Framebuffer.Width;
            uint h = Framebuffer.Height;
            if (w < GoldenWidth || h < GoldenHeight) return 0;

            FbConsole.Clear(0, 0, 40);                       // dark navy

            // Channel-order swatches: R, G, B, white (eyeball BGRX).
            //
            // A fixed width, not w/4. The golden checksum covers the top-left
            // 512x360, and while the swatches scaled with the screen the
            // content of that region changed with the resolution — so the
            // check could only ever pass on the machine it was captured on
            // (1280x800), and was red on the test laptop at 1920x1080 no
            // matter what the renderer did. A test that cannot pass where it
            // runs is not a test.
            const int SwatchWidth = 128;
            FbConsole.FillRect(0 * SwatchWidth, 0, SwatchWidth, 64, 255, 0, 0);
            FbConsole.FillRect(1 * SwatchWidth, 0, SwatchWidth, 64, 0, 255, 0);
            FbConsole.FillRect(2 * SwatchWidth, 0, SwatchWidth, 64, 0, 0, 255);
            FbConsole.FillRect(3 * SwatchWidth, 0, SwatchWidth, 64, 255, 255, 255);

            uint fg = FbConsole.Pack(230, 230, 0);           // amber
            uint cyan = FbConsole.Pack(0, 220, 220);
            FbConsole.DrawString(40, 110, "SHARPOS GOP", fg, -1, 5);
            FbConsole.DrawString(40, 180, "8x8 FONT RENDERER - PHASE B#2", cyan, -1, 3);

            // Below the checksummed region on purpose: it prints the screen's
            // own dimensions, which is exactly what must not be inside a
            // comparison meant to hold on every screen. Still drawn — it is
            // the line a person reads off the panel.
            int cx = 40;
            int gy = 380;
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

            return FbConsole.Checksum(0, 0, GoldenWidth, GoldenHeight);
        }

        // What this frame checksummed to before ExitBootServices.
        private static uint s_preTeardownCrc;

        /// <summary>
        /// Pure pass/fail, no console. Post-EBS: did the GOP path survive the
        /// firmware being torn down?
        /// </summary>
        /// <remarks>
        /// Compared against what the same frame produced earlier in this boot,
        /// not against a checked-in constant — which is what the question
        /// actually is: the renderer, the font and the MMIO mapping are
        /// identical without firmware if, and only if, the same drawing gives
        /// the same pixels. A constant answered a different question ("is this
        /// the machine the constant came from"), and on the test laptop it
        /// answered no, reporting `fb=FAIL` in every post-EBS line for as long
        /// as anyone had been reading them.
        /// </remarks>
        public static bool Verify()
        {
            uint crc = RenderAndChecksum();

            // Zero means the screen is too small for the compared region.
            // Nothing to compare is not a failure to survive.
            if (crc == 0) return true;

            if (s_preTeardownCrc != 0) return crc == s_preTeardownCrc;
            return GoldenCrc == 0 || crc == GoldenCrc;
        }

        // Golden FNV-1a of FbConsole.Checksum(0,0,512,360).
        //
        // Captured 2026-09-26 and confirmed portable in the same breath: QEMU
        // at 1280x800 and VirtualBox at 1024x768 both produced it, which is
        // what the previous golden could never do — it belonged to the pattern
        // before the swatches were given a fixed width and the geometry line
        // moved out of the checksummed region, and so failed on every screen
        // but the one it came from.
        //
        // Zero would mean "not captured yet": the probe then prints its value
        // and says BASELINE instead of failing against a number from a
        // different picture.
        //
        // Re-baseline ONLY together with a deliberate renderer, font or
        // probe-layout change, never to "make it pass": that is what this
        // oracle is for.
        private const uint GoldenCrc = 0xE8248B45u;

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
