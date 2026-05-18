using OS.Boot;
using OS.Kernel.Memory;

namespace OS.Hal
{
    // Phase B#2 sub-step 2 — map the GOP linear framebuffer (captured into
    // BootInfo by UefiGop while Boot Services were alive) into kernel VA so
    // a managed renderer can draw to it after the pager's CR3 is active.
    //
    // The FB lives in an MMIO BAR (OVMF std-vga: phys 0x80000000), above the
    // 512 MiB RAM ceiling and clear of every VA window (RAM 0..0x2000_0000,
    // kernel image ~0x1C00_0000, VM window 0x5000_0000_0000). Identity-mapped
    // (va == pa) — the simplest robust choice and exactly what firmware does
    // pre-paging; VirtualMemory.MapFixed re-establishes it in the pager PML4
    // (already-mapped pages are skipped, so a firmware identity map is fine).
    //
    // RW + NX (no CacheDisable): write-back caching is correct for a linear
    // FB; a write-combining attribute is a later perf optimization, not a
    // correctness requirement. Non-fatal: headless / BltOnly / no-GOP boots
    // leave IsAvailable=false and the kernel continues.
    internal static unsafe class Framebuffer
    {
        private static ulong s_base;     // kernel VA (== phys, identity)
        private static uint s_width;
        private static uint s_height;
        private static uint s_stride;    // pixels per scan line
        private static uint s_format;    // 0=RGBX8 1=BGRX8 2=BitMask 3=BltOnly
        private static bool s_available;

        public static bool IsAvailable => s_available;
        public static ulong BaseAddress => s_base;
        public static uint Width => s_width;
        public static uint Height => s_height;
        public static uint Stride => s_stride;
        public static uint PixelFormat => s_format;

        // Map [FramebufferBase, +FramebufferSize) identity into the active
        // (pager) PML4. Call after InitializePager + VM self-test. Returns
        // false (IsAvailable stays false) if GOP wasn't captured or the
        // mapping fails — caller logs warn and continues (headless boot).
        public static bool TryInit()
        {
            if (s_available)
                return true;

            BootInfo bi = Platform.GetBootInfo();
            if (bi.GraphicsAvailable == 0 ||
                bi.FramebufferBase == 0 || bi.FramebufferSize == 0)
                return false;

            if (!VirtualMemory.MapFixed(
                    (void*)bi.FramebufferBase,
                    bi.FramebufferBase,
                    bi.FramebufferSize,
                    exec: false))
                return false;

            s_base      = bi.FramebufferBase;
            s_width     = bi.FramebufferWidth;
            s_height    = bi.FramebufferHeight;
            s_stride    = bi.FramebufferStride;
            s_format    = bi.FramebufferPixelFormat;
            s_available = true;
            return true;
        }

        // 32-bit pixel write (no bounds check — caller clips). Stride is in
        // pixels. Pack per PixelFormat: 0 RGBX (R in low byte), else BGRX.
        public static void PutPixel(uint x, uint y, byte r, byte g, byte b)
        {
            if (!s_available) return;
            uint packed = s_format == 0
                ? ((uint)r) | ((uint)g << 8) | ((uint)b << 16)
                : ((uint)b) | ((uint)g << 8) | ((uint)r << 16);
            ((uint*)s_base)[(ulong)y * s_stride + x] = packed;
        }

        // Raw 32-bit readback through the same mapping (liveness oracle).
        public static uint GetPixelRaw(uint x, uint y)
        {
            if (!s_available) return 0;
            return ((uint*)s_base)[(ulong)y * s_stride + x];
        }
    }
}
