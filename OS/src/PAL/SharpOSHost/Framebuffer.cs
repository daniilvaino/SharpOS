using System.Runtime;
using System.Runtime.InteropServices;
using OS.Hal;

namespace OS.PAL.SharpOSHost
{
    // Phase B#2 sub-step 2 — hosted-tier half of the dual-layer FB bridge.
    // A managed renderer running under the forked CoreCLR JIT P/Invokes
    // this to obtain the live framebuffer VA + geometry mapped by the
    // kernel (OS.Hal.Framebuffer). Returns 1 if available, 0 otherwise
    // (out params zeroed). The AOT kernel tier reaches the same FB via
    // OS.Hal.Framebuffer directly — same memory, two callable surfaces.
    internal static unsafe class SharpOSHostFramebuffer
    {
        [RuntimeExport("SharpOSHost_GetFramebuffer")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetFramebuffer")]
        public static int GetFramebuffer(
            ulong* outBaseVa, uint* outWidth, uint* outHeight,
            uint* outStride, uint* outFormat)
        {
            if (outBaseVa != null) *outBaseVa = 0;
            if (outWidth != null)  *outWidth  = 0;
            if (outHeight != null) *outHeight = 0;
            if (outStride != null) *outStride = 0;
            if (outFormat != null) *outFormat = 0;

            if (!Framebuffer.IsAvailable)
                return 0;

            if (outBaseVa != null) *outBaseVa = Framebuffer.BaseAddress;
            if (outWidth != null)  *outWidth  = Framebuffer.Width;
            if (outHeight != null) *outHeight = Framebuffer.Height;
            if (outStride != null) *outStride = Framebuffer.Stride;
            if (outFormat != null) *outFormat = Framebuffer.PixelFormat;
            return 1;
        }
    }
}
