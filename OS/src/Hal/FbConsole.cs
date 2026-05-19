namespace OS.Hal
{
    // Phase B#2 sub-step 3 — minimal kernel-tier text/graphics renderer
    // straight to the mapped GOP framebuffer (OS.Hal.Framebuffer). No
    // double buffer: a boot console has no animation and the FB is
    // write-back cached, so direct draw is correct and simplest; a
    // back-buffer is a later optimisation if tearing ever matters.
    //
    // All primitives clip to [0,Width) x [0,Height) — the FB mapping is
    // exactly Width*Height*4 with no slack, so an unclipped blit past the
    // last row would fault. No-ops when the FB isn't available (headless
    // / no-GOP), so callers need no guard.
    //
    // Glyph blit adapted from files (3)/Font8x8Renderer.cs (user-supplied,
    // public domain), retargeted from raw fb*/byte-stride to the
    // Framebuffer abstraction (pixel stride, format-aware packing) and
    // given clipping + an integer scale factor.
    internal static unsafe class FbConsole
    {
        // Pack 8-8-8 into the FB's 32bpp pixel. fmt 0 = RGBX (R in the
        // low byte), anything else (1 BGRX / OVMF std-vga) = BGRX.
        public static uint Pack(byte r, byte g, byte b)
        {
            return Framebuffer.PixelFormat == 0
                ? ((uint)r) | ((uint)g << 8) | ((uint)b << 16)
                : ((uint)b) | ((uint)g << 8) | ((uint)r << 16);
        }

        private static void PutRaw(uint x, uint y, uint packed)
        {
            // Caller guarantees in-bounds; stride is in pixels.
            ((uint*)Framebuffer.BaseAddress)[(ulong)y * Framebuffer.Stride + x] = packed;
        }

        public static void Clear(byte r, byte g, byte b)
        {
            if (!Framebuffer.IsAvailable) return;
            uint packed = Pack(r, g, b);
            uint w = Framebuffer.Width;
            uint h = Framebuffer.Height;
            for (uint y = 0; y < h; y++)
                for (uint x = 0; x < w; x++)
                    PutRaw(x, y, packed);
        }

        // Filled rectangle, clipped. Negative origin / oversize allowed —
        // the visible intersection is drawn.
        public static void FillRect(int x, int y, int w, int h, byte r, byte g, byte b)
        {
            if (!Framebuffer.IsAvailable || w <= 0 || h <= 0) return;
            int fbW = (int)Framebuffer.Width;
            int fbH = (int)Framebuffer.Height;

            int x0 = x < 0 ? 0 : x;
            int y0 = y < 0 ? 0 : y;
            int x1 = x + w; if (x1 > fbW) x1 = fbW;
            int y1 = y + h; if (y1 > fbH) y1 = fbH;
            if (x0 >= x1 || y0 >= y1) return;

            uint packed = Pack(r, g, b);
            for (int yy = y0; yy < y1; yy++)
                for (int xx = x0; xx < x1; xx++)
                    PutRaw((uint)xx, (uint)yy, packed);
        }

        // One glyph at device pixel (px,py), each font pixel an NxN block.
        // bg < 0 means transparent (skip background pixels).
        public static void DrawChar(int px, int py, char ch, uint fg, long bg, int scale)
        {
            if (!Framebuffer.IsAvailable) return;
            if (scale < 1) scale = 1;

            int fbW = (int)Framebuffer.Width;
            int fbH = (int)Framebuffer.Height;

            for (int row = 0; row < Font8x8.CharHeight; row++)
            {
                byte bits = Font8x8.Row(ch, row);
                for (int col = 0; col < Font8x8.CharWidth; col++)
                {
                    bool on = (bits & (1 << col)) != 0;
                    if (!on && bg < 0) continue;
                    uint packed = on ? fg : (uint)bg;
                    int bx = px + col * scale;
                    int by = py + row * scale;
                    for (int sy = 0; sy < scale; sy++)
                    {
                        int dy = by + sy;
                        if (dy < 0 || dy >= fbH) continue;
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int dx = bx + sx;
                            if (dx < 0 || dx >= fbW) continue;
                            PutRaw((uint)dx, (uint)dy, packed);
                        }
                    }
                }
            }
        }

        // Left-to-right string; each cell advances 8*scale px. No wrap
        // (caller positions lines). bg < 0 = transparent.
        public static void DrawString(int px, int py, string text, uint fg, long bg, int scale)
        {
            if (!Framebuffer.IsAvailable || text == null) return;
            if (scale < 1) scale = 1;
            int cursor = px;
            for (int i = 0; i < text.Length; i++)
            {
                DrawChar(cursor, py, text[i], fg, bg, scale);
                cursor += Font8x8.CharWidth * scale;
            }
        }

        // FNV-1a over the packed pixels of a clipped region — a stable,
        // headless-verifiable fingerprint of what was rendered. Same
        // region + same draw calls => same value across runs.
        public static uint Checksum(int x, int y, int w, int h)
        {
            if (!Framebuffer.IsAvailable || w <= 0 || h <= 0) return 0;
            int fbW = (int)Framebuffer.Width;
            int fbH = (int)Framebuffer.Height;
            int x0 = x < 0 ? 0 : x;
            int y0 = y < 0 ? 0 : y;
            int x1 = x + w; if (x1 > fbW) x1 = fbW;
            int y1 = y + h; if (y1 > fbH) y1 = fbH;
            if (x0 >= x1 || y0 >= y1) return 0;

            uint hash = 2166136261u;
            uint* fb = (uint*)Framebuffer.BaseAddress;
            for (int yy = y0; yy < y1; yy++)
            {
                ulong rowBase = (ulong)yy * Framebuffer.Stride;
                for (int xx = x0; xx < x1; xx++)
                {
                    uint p = fb[rowBase + (uint)xx];
                    hash = (hash ^ (p & 0xFF)) * 16777619u;
                    hash = (hash ^ ((p >> 8) & 0xFF)) * 16777619u;
                    hash = (hash ^ ((p >> 16) & 0xFF)) * 16777619u;
                    hash = (hash ^ ((p >> 24) & 0xFF)) * 16777619u;
                }
            }
            return hash;
        }
    }
}
