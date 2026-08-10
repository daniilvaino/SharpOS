using SharpOS.AppSdk;

namespace Fami
{
    // Blits the emulator's 256x240 frame to the GOP framebuffer.
    //
    // Same design as the TriCNES blitter, and for the same reasons: integer
    // scaling with nearest-neighbour sampling (the NES picture has hard pixel
    // edges, and smoothing them would be a lie about what the console drew),
    // rows staged in ordinary memory, and no reads of video memory ever — on
    // machines where firmware maps the framebuffer uncacheable a read costs
    // ~50x a write.
    //
    // One difference worth stating, because it is invisible and produces a
    // picture that looks plausible while being wrong: Fami's palette packs
    // red in the low byte (0x00BBGGRR, see Ppu.palScreen), where TriCNES packs
    // it in the high byte. So the channel swap here is the exact inverse of
    // the one there. Get it backwards and the picture is fine except that
    // Mario is blue — which is exactly what both blitters did until it was
    // measured against the kernel's own packing rather than reasoned about.
    internal sealed unsafe class GopVideo
    {
        private const int NesWidth = 256;
        private const int NesHeight = 240;

        private readonly uint* _fb;
        private readonly uint _stride;      // pixels per scanline
        private readonly bool _swapRedBlue;
        private readonly int _scale;
        private readonly uint _offsetX;
        private readonly uint _offsetY;
        private readonly uint[] _line;      // one expanded row, in RAM
        private readonly uint[] _previous;  // last frame, for row comparison
        private bool _hasPrevious;

        public int Scale => _scale;

        public GopVideo(ulong baseAddress, uint width, uint height, uint stridePixels, uint pixelFormat)
        {
            _fb = (uint*)baseAddress;
            _stride = stridePixels;

            // Format 0 wants red in the low byte, anything else wants blue
            // there (OS/src/Hal/Framebuffer.cs PutPixel is the definition).
            // Fami's palette is already red-low, so format 0 passes through
            // and every other format needs the swap.
            _swapRedBlue = pixelFormat != 0;

            uint sx = width / NesWidth;
            uint sy = height / NesHeight;
            uint s = sx < sy ? sx : sy;
            if (s < 1) s = 1;
            _scale = (int)s;

            uint scaledW = (uint)(NesWidth * _scale);
            uint scaledH = (uint)(NesHeight * _scale);
            _offsetX = width > scaledW ? (width - scaledW) / 2 : 0;
            _offsetY = height > scaledH ? (height - scaledH) / 2 : 0;

            _line = new uint[scaledW];
            _previous = new uint[NesWidth * NesHeight];
        }

        /// <summary>
        /// One frame. <paramref name="pixels"/> is the PPU's own output buffer:
        /// 256x240, row-major, one packed 0x00BBGGRR pixel per uint.
        /// </summary>
        public void Blit(uint[] pixels)
        {
            if (pixels == null || pixels.Length < NesWidth * NesHeight) return;

            int scale = _scale;
            int rowPixels = NesWidth * scale;

            fixed (uint* line = _line)
            fixed (uint* previous = _previous)
            {
                for (int y = 0; y < NesHeight; y++)
                {
                    int src = y * NesWidth;

                    // Skip rows that did not change. The comparison reads RAM,
                    // which is two orders of magnitude faster than the video
                    // memory it saves writing: menus and status bars become
                    // free, a full-screen scroll costs what it always did.
                    if (_hasPrevious && RowUnchanged(pixels, previous, src)) continue;
                    for (int x = 0; x < NesWidth; x++) previous[src + x] = pixels[src + x];

                    // Expand horizontally into RAM first.
                    uint* d = line;
                    for (int x = 0; x < NesWidth; x++)
                    {
                        uint c = pixels[src + x];
                        if (_swapRedBlue)
                            c = (c & 0xFF00FF00u) | ((c & 0xFFu) << 16) | ((c >> 16) & 0xFFu);
                        for (int r = 0; r < scale; r++) *d++ = c;
                    }

                    // Then write it out `scale` times. Sequential writes only.
                    uint* dstBase = _fb + (_offsetY + (uint)(y * scale)) * _stride + _offsetX;
                    for (int r = 0; r < scale; r++)
                    {
                        uint* dst = dstBase + (uint)r * _stride;
                        for (int x = 0; x < rowPixels; x++) dst[x] = line[x];
                    }
                }
            }

            _hasPrevious = true;
        }

        private static bool RowUnchanged(uint[] pixels, uint* previous, int src)
        {
            for (int x = 0; x < NesWidth; x++)
                if (pixels[src + x] != previous[src + x]) return false;
            return true;
        }
    }
}
