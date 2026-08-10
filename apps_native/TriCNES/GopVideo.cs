using SharpOS.AppSdk;

namespace TriCNES
{
    // Blits the emulator's 256x240 frame to the GOP framebuffer.
    //
    // Integer scaling, centred, nearest-neighbour: the NES picture has hard
    // pixel edges and any smoothing would be a lie about what the console drew.
    //
    // The framebuffer is WRITE-ONLY as far as this code is concerned. On some
    // machines the firmware maps video memory uncacheable, where a read runs
    // ~50x slower than a write, so the expanded row is built in ordinary
    // memory and copied out — never read back. (Learned the hard way: DOOM's
    // blit replicated rows by copying inside the framebuffer and cost more
    // than a second per frame on a real desktop.)
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
        private readonly int[] _previous;   // last frame, for row comparison
        private bool _hasPrevious;

        public int Scale => _scale;
        public uint OffsetX => _offsetX;
        public uint OffsetY => _offsetY;

        public GopVideo(ulong baseAddress, uint width, uint height, uint stridePixels, uint pixelFormat)
        {
            _fb = (uint*)baseAddress;
            _stride = stridePixels;

            // The emulator packs ARGB (Color.ToArgb order), so blue sits in the
            // low byte. Format 0 wants red there, anything else wants blue
            // (OS/src/Hal/Framebuffer.cs PutPixel is the definition) — so it is
            // format 0, and only format 0, that needs the channels swapped.
            //
            // This was backwards until step153. It went unnoticed because the
            // only cartridge ever run here was AccuracyCoin, whose screens are
            // grey and white: a red/blue swap leaves those untouched. The bug
            // surfaced on the other emulator the moment a game with colour ran.
            _swapRedBlue = pixelFormat == 0;

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
            _previous = new int[NesWidth * NesHeight];
        }

        /// <summary>
        /// One frame. <paramref name="pixels"/> is the emulator's DirectBitmap
        /// buffer: 256x240, row-major, ARGB per int.
        /// </summary>
        public void Blit(int[] pixels)
        {
            if (pixels == null || pixels.Length < NesWidth * NesHeight) return;

            int scale = _scale;
            int rowPixels = NesWidth * scale;

            fixed (uint* line = _line)
            fixed (int* previous = _previous)
            {
                for (int y = 0; y < NesHeight; y++)
                {
                    int src = y * NesWidth;

                    // Skip rows that did not change.
                    //
                    // Writing the whole screen every frame costs 17 ms on a
                    // machine whose framebuffer runs at ~264 MiB/s — half the
                    // frame budget spent redrawing pixels that are already
                    // correct. The comparison reads RAM, which is two orders of
                    // magnitude faster than the video memory it saves writing.
                    // Menus and status bars become free; a full-screen scroll
                    // costs what it always did.
                    if (_hasPrevious && RowUnchanged(pixels, previous, src)) continue;
                    for (int x = 0; x < NesWidth; x++) previous[src + x] = pixels[src + x];

                    // Expand horizontally into RAM first.
                    uint* d = line;
                    for (int x = 0; x < NesWidth; x++)
                    {
                        uint c = (uint)pixels[src + x];
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

        private static bool RowUnchanged(int[] pixels, int* previous, int src)
        {
            for (int x = 0; x < NesWidth; x++)
                if (pixels[src + x] != previous[src + x]) return false;
            return true;
        }
    }
}
