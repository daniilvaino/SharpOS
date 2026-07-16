namespace OS.Hal.Timer
{
    // HPET — High Precision Event Timer hardware abstraction.
    //
    // The base address is discovered via ACPI HPET table (step 38).
    // Register block layout (per HPET spec rev 1.0a):
    //
    //   offset 0x000  General Capabilities and ID Register (64 bit)
    //                 bits 0..7    REV_ID
    //                 bits 8..12   NUM_TIM_CAP — number of comparators - 1
    //                 bit  13      COUNT_SIZE_CAP — 0=32-bit, 1=64-bit
    //                 bits 16..31  VENDOR_ID
    //                 bits 32..63  COUNTER_CLK_PERIOD — period in femtoseconds (1e-15 s)
    //   offset 0x010  General Configuration Register
    //                 bit 0 ENABLE_CNF — set to 1 to start counter
    //                 bit 1 LEG_RT_CNF — legacy interrupt routing
    //   offset 0x020  General Interrupt Status Register
    //   offset 0x0F0  Main Counter Value Register (64-bit free-running counter)
    //   offset 0x100+ per-comparator config blocks (32 bytes each)
    //
    // We use HPET as a free-running counter source for Stopwatch / duration
    // measurements. Comparator interrupts come later (Phase 3 scheduler).

    internal static unsafe class Hpet
    {
        private const uint OFFSET_CAPS = 0x000;
        private const uint OFFSET_CONFIG = 0x010;
        private const uint OFFSET_COUNTER = 0x0F0;

        private static bool s_initialized;
        private static byte* s_base;
        private static ulong s_periodFs;     // counter period in femtoseconds
        private static ulong s_frequencyHz;  // ticks per second
        private static bool s_is64Bit;
        private static int s_numComparators;

        public static bool IsInitialized => s_initialized;
        public static ulong FrequencyHz => s_frequencyHz;
        // Raw MMIO address of the main counter — handed to apps via the
        // service table (step143): identity-mapped in the shared address
        // space, so an app reads the free-running counter directly.
        public static ulong CounterAddress => s_initialized ? (ulong)(s_base + OFFSET_COUNTER) : 0;
        public static ulong PeriodFemtoseconds => s_periodFs;
        public static bool Is64BitCounter => s_is64Bit;
        public static int NumComparators => s_numComparators;

        public static bool Init()
        {
            if (s_initialized) return true;

            ulong baseAddr = OS.Hal.Acpi.Hpet.Base;
            if (baseAddr == 0) return false;

            byte* baseP = (byte*)baseAddr;

            ulong caps = *(ulong*)(baseP + OFFSET_CAPS);
            ulong period = (caps >> 32) & 0xFFFFFFFFu;
            if (period == 0 || period > 100_000_000UL) return false;  // sanity: <0.1s/tick

            s_periodFs = period;
            // Frequency = 10^15 femtoseconds/sec / period_fs
            s_frequencyHz = 1_000_000_000_000_000UL / period;
            s_is64Bit = ((caps >> 13) & 1) != 0;
            s_numComparators = (int)((caps >> 8) & 0x1F) + 1;
            s_base = baseP;

            // Enable the counter (set ENABLE_CNF in Configuration Register).
            ulong config = *(ulong*)(baseP + OFFSET_CONFIG);
            *(ulong*)(baseP + OFFSET_CONFIG) = config | 1UL;

            s_initialized = true;
            return true;
        }

        public static ulong ReadCounter()
        {
            if (!s_initialized) return 0;
            // 64-bit counter is atomic on x64; on 32-bit-only HPETs (rare on
            // modern hardware), the upper 32 bits are zero so a simple
            // 64-bit read still yields the correct value.
            return *(ulong*)(s_base + OFFSET_COUNTER);
        }
    }
}
