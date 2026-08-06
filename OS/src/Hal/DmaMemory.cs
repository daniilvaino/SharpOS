using OS.Kernel;
using OS.Kernel.Memory;

namespace OS.Hal
{
    // Physically contiguous, identity-mapped, zeroed pages for device DMA.
    //
    // Identity mapping is the point: a controller reads these by physical
    // address, so virt == phys removes the translation step from every
    // pointer we hand it. Left cacheable — DMA is coherent on x86, the
    // platform snoops — unlike the register windows, which must be uncached.
    //
    // (Ahci.AllocDma predates this and does the same thing; worth folding
    // into here when AHCI is next touched.)
    internal static unsafe class DmaMemory
    {
        public static ulong AllocPages(uint pages)
        {
            ulong phys = PhysicalMemory.AllocPages(pages);
            if (phys == 0) return 0;

            ulong size = (ulong)pages * 4096UL;
            if (!VirtualMemory.MapFixed((void*)phys, phys, size, exec: false))
                return 0;

            byte* b = (byte*)phys;
            for (ulong i = 0; i < size; i++) b[i] = 0;
            return phys;
        }
    }
}
