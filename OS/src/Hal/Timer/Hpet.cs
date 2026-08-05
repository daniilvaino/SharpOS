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

        // Config register as it read back after we set ENABLE_CNF. If bit 0 is
        // clear here, the write did not stick — which is a different failure
        // from "the counter is enabled but frozen", and only this tells them
        // apart on a machine with no debugger.
        private static ulong s_configBefore, s_configAfter, s_caps;
        public static ulong ConfigBefore => s_configBefore;
        public static ulong ConfigAfter => s_configAfter;
        public static ulong Capabilities => s_caps;

        /// <summary>
        /// Halt, zero and restart the main counter.
        ///
        /// ENABLE_CNF reading back as set does not guarantee the counter is
        /// running — it can be left halted across the firmware handoff. The
        /// spec allows writing the counter only while it is halted, so a
        /// clean restart is the disable / write / enable sequence rather than
        /// just setting the bit again.
        /// </summary>
        public static bool TryForceRestart()
        {
            if (!s_initialized) return false;

            ulong* cfg = (ulong*)(s_base + OFFSET_CONFIG);
            *cfg = *cfg & ~1UL;
            *(ulong*)(s_base + OFFSET_COUNTER) = 0;
            *cfg = *cfg | 1UL;
            s_restarted = true;
            return true;
        }

        private static bool s_restarted;

        /// <summary>True when the counter had to be restarted by hand because
        /// the firmware left it halted with ENABLE_CNF already set.</summary>
        public static bool WasRestarted => s_restarted;

        /// <summary>
        /// Verify the counter actually moves, and restart it once if it does
        /// not. Observed on real hardware: the firmware hands over a halted
        /// counter whose ENABLE_CNF bit already reads as set, so setting the
        /// bit is a no-op and only the halt/zero/start cycle revives it.
        ///
        /// The restart zeroes the counter, so it happens at most once — later
        /// callers must not have timestamps pulled out from under them.
        /// </summary>
        public static bool EnsureRunning()
        {
            if (!s_initialized) return false;
            if (Advances()) return true;
            if (s_restarted) return false;
            TryForceRestart();
            return Advances();
        }

        private static bool Advances()
        {
            ulong t0 = ReadCounter();
            for (int i = 0; i < 1_000_000; i++)
                if (ReadCounter() != t0) return true;
            return false;
        }

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

            // The firmware leaves this window mapped cacheable (or not mapped
            // at all). A cached HPET reads the same counter value forever —
            // "hpet=STUCK" — and ENABLE_CNF below may never reach the chip.
            if (!OS.Kernel.Memory.VirtualMemory.MapFixed(
                    (void*)baseAddr, baseAddr, 0x1000, exec: false,
                    OS.Kernel.Memory.VirtualMemory.MemoryKind.Device))
                return false;

            byte* baseP = (byte*)baseAddr;

            ulong caps = *(ulong*)(baseP + OFFSET_CAPS);
            s_caps = caps;
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
            s_configBefore = config;
            *(ulong*)(baseP + OFFSET_CONFIG) = config | 1UL;
            s_configAfter = *(ulong*)(baseP + OFFSET_CONFIG);

            s_initialized = true;
            // The bit above may already have been set by the firmware while
            // the counter sat halted — check rather than assume.
            EnsureRunning();
            return true;
        }

        // NoInlining is load-bearing: ILC hoists a plain MMIO read out of a
        // spin loop, which looks exactly like a frozen counter.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
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
