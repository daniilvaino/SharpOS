namespace OS.Hal
{
    // Make the "write-through" page attribute mean write-combining instead.
    //
    // Why this exists: writing a framebuffer over PCIe one store at a time is
    // brutally slow. Write-combining lets the CPU gather stores into full
    // cache-line bursts, which is the difference between a screen that
    // repaints instantly and one that visibly fills over a second — the exact
    // symptom seen on a real machine while an emulator, where the framebuffer
    // is ordinary RAM, showed nothing.
    //
    // x86 has no page bit that simply says "write-combining". The three page
    // bits (PAT, PCD, PWT) select one of eight entries in the PAT register,
    // and by default none of them is write-combining. So the register is
    // reprogrammed: entry 1 — the one selected by PWT alone — changes from
    // write-through to write-combining.
    //
    // Entry 1 specifically, because PWT-alone is exactly what
    // MemoryKind.Framebuffer maps with, and nothing else uses it. Device
    // mappings set PCD|PWT (entry 3, uncacheable) and are untouched, as is
    // ordinary memory (entry 0, write-back).
    internal static unsafe class MemoryTypes
    {
        private const uint IA32_PAT = 0x277;

        private const byte PatWriteCombining = 0x01;
        private const byte PatWriteThrough = 0x04;

        private static bool s_enabled;
        public static bool WriteCombiningEnabled => s_enabled;

        public static bool TryEnableWriteCombining()
        {
            if (s_enabled) return true;
            if (!X64Asm.ReadMsr(IA32_PAT, out ulong pat)) return false;

            // Sanity: entry 1 should be write-through as the architecture
            // defines it at reset. If firmware already changed it, leave well
            // alone rather than guessing what it meant to do.
            byte entry1 = (byte)((pat >> 8) & 0xFF);
            if (entry1 != PatWriteThrough && entry1 != PatWriteCombining) return false;

            ulong updated = (pat & ~(0xFFUL << 8)) | ((ulong)PatWriteCombining << 8);
            if (!X64Asm.WriteMsr(IA32_PAT, updated)) return false;

            // The memory type of a page is resolved through the TLB, and lines
            // cached under the old type would keep their old behaviour, so
            // both have to go.
            OS.Kernel.Paging.Cr3Accessor.TryInvalidateCaches();
            OS.Kernel.Paging.X64PageTable.FlushTlbAll();

            if (!X64Asm.ReadMsr(IA32_PAT, out ulong readback)) return false;
            s_enabled = ((readback >> 8) & 0xFF) == PatWriteCombining;
            return s_enabled;
        }
    }
}
