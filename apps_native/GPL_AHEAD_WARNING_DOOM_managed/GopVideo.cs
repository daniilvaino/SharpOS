// GopVideo — IVideo for SharpOS (step143). ManagedDoom's software Renderer
// fills a column-major 32-bit buffer (index = x * Height + y, R in the low
// byte — see Renderer.WriteData / Palette.ResetColors); the GOP linear
// framebuffer is row-major BGRX/RGBX. The blit transposes and, for BGRX,
// swizzles R<->B. SilkVideo did the same transpose on the GPU (texture
// uploaded H×W + rotated UVs); this is the CPU equivalent, shape mirrored
// from src/Silk/SilkVideo.cs minus the GL plumbing.
//
// The FB pointer comes straight from the AppServiceTable (identity-mapped
// in the shared address space) — pixels are written directly, no service
// calls on the render path.

using System;
using ManagedDoom;
using ManagedDoom.Video;

namespace DoomApp
{
    public sealed unsafe class GopVideo : IVideo
    {
        private readonly Renderer renderer;
        private readonly byte[] frame; // 4 * W * H, column-major, from Renderer

        private readonly ulong fbBase;
        private readonly uint fbStride;  // pixels per scanline
        private readonly bool fbIsRgbx;  // pixel format 0 = RGBX (renderer-native), else BGRX
        private readonly int scale;      // integer upscale: min(fbW/W, fbH/H), >= 1
        private readonly uint offsetX;   // centering offset for the scaled image
        private readonly uint offsetY;

        public GopVideo(
            Config config,
            GameContent content,
            ulong baseAddress,
            uint fbWidth,
            uint fbHeight,
            uint stridePixels,
            uint pixelFormat)
        {
            renderer = new Renderer(config, content);
            frame = new byte[4 * renderer.Width * renderer.Height];

            fbBase = baseAddress;
            fbStride = stridePixels;
            fbIsRgbx = pixelFormat == 0;

            // Largest integer scale that fits, centered (e.g. 640x400 in a
            // 1280x800 mode -> 2x, exactly full screen).
            uint w = (uint)renderer.Width;
            uint h = (uint)renderer.Height;
            uint sx = fbWidth / w;
            uint sy = fbHeight / h;
            uint s = sx < sy ? sx : sy;
            if (s < 1) s = 1;
            scale = (int)s;

            uint scaledW = w * s;
            uint scaledH = h * s;
            offsetX = fbWidth > scaledW ? (fbWidth - scaledW) / 2 : 0;
            offsetY = fbHeight > scaledH ? (fbHeight - scaledH) / 2 : 0;
        }

        public void Render(Doom doom, Fixed frameFrac)
        {
            renderer.Render(doom, frame, frameFrac);

            int width = renderer.Width;
            int height = renderer.Height;
            int s = scale;
            bool swap = !fbIsRgbx;

            fixed (byte* src = frame)
            {
                uint* srcPixels = (uint*)src;
                uint* dstBase = (uint*)fbBase + offsetY * fbStride + offsetX;

                // Source is column-major (x*height + y); walk destination rows,
                // striding the source by `height` per horizontal step. Each
                // source row expands to `s` destination rows of `width*s` px.
                for (int y = 0; y < height; y++)
                {
                    uint* dstRow = dstBase + (uint)(y * s) * fbStride;
                    uint* srcCol = srcPixels + y;

                    uint* d = dstRow;
                    for (int x = 0; x < width; x++)
                    {
                        uint c = srcCol[x * height];
                        if (swap)
                        {
                            // RGBA (R low byte) -> BGRX: swap R and B, keep G/A lanes.
                            c = (c & 0xFF00FF00u) | ((c & 0xFFu) << 16) | ((c >> 16) & 0xFFu);
                        }
                        for (int r = 0; r < s; r++)
                            *d++ = c;
                    }

                    // Replicate the finished row for the remaining s-1 rows.
                    int rowPixels = width * s;
                    for (int r = 1; r < s; r++)
                    {
                        uint* rep = dstRow + (uint)r * fbStride;
                        for (int x = 0; x < rowPixels; x++)
                            rep[x] = dstRow[x];
                    }
                }
            }
        }

        public void InitializeWipe() => renderer.InitializeWipe();

        public bool HasFocus() => true;

        public int WipeBandCount => renderer.WipeBandCount;
        public int WipeHeight => renderer.WipeHeight;

        public int MaxWindowSize => renderer.MaxWindowSize;

        public int WindowSize
        {
            get => renderer.WindowSize;
            set => renderer.WindowSize = value;
        }

        public bool DisplayMessage
        {
            get => renderer.DisplayMessage;
            set => renderer.DisplayMessage = value;
        }

        public int MaxGammaCorrectionLevel => renderer.MaxGammaCorrectionLevel;

        public int GammaCorrectionLevel
        {
            get => renderer.GammaCorrectionLevel;
            set => renderer.GammaCorrectionLevel = value;
        }
    }
}
