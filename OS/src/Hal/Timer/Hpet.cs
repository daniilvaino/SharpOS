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
    //   offset 0x0F0  Main Counter Value Register — free-running, and 64 bits
    //                 wide only when COUNT_SIZE_CAP says so. Otherwise the
    //                 low 32 bits are the counter and the rest is undefined;
    //                 ReadCounter carries the epoch in software (step177).
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

        // Epoch carrier for a 32-bit counter. Not a time source: every read
        // still takes the live counter itself, and this only says which
        // wrap-around the low 32 bits belong to. One aligned 64-bit word, so
        // the store is atomic on x64 and a racing reader can at worst carry a
        // stale epoch forward -- which the comparison below re-derives anyway.
        //
        // NOT CORRECT FOR SMP: two cores can interleave a read and a store
        // such that the epoch moves back by one and stays there. A seqlock or
        // a CAS-max belongs here before a second core runs.
        private static ulong s_latch;

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
            // The counter just went to zero. Leaving the old epoch in place
            // would make the next read look like a wrap and jump time forward
            // by the whole 32-bit range -- 300 s on this hardware.
            s_latch = 0;
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
        //
        // Never valid on its own: CounterBits below says how much of the
        // register is real. Reading 64 bits from a 32-bit counter is what
        // made time run backwards on this laptop every 300 s.
        public static ulong CounterAddress => s_initialized ? (ulong)(s_base + OFFSET_COUNTER) : 0;
        public static ulong PeriodFemtoseconds => s_periodFs;
        public static bool Is64BitCounter => s_is64Bit;

        // What an app needs to read the counter itself without a service
        // call. Width, because a 32-bit counter cannot be read as 64; and the
        // latch, because the epoch is shared state -- an app that ran for one
        // frame every ten seconds would otherwise miss a wrap it never saw.
        public static uint CounterBits => s_is64Bit ? 64u : 32u;
        // Through Unsafe.AsPointer, not `&`: a static field is a MOVEABLE
        // variable to the compiler, so plain address-of is rejected and a
        // `fixed` statement would only hold it for the length of a block —
        // useless for an address we hand to another image and keep.
        //
        // Safe because nothing here moves in fact: the collector does not
        // compact, and a ulong static lives in the non-GC static region to
        // begin with.
        public static ulong LatchAddress
            => s_initialized
                ? (ulong)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_latch)
                : 0;

        // Move the epoch on without anyone needing a timestamp. The timer
        // tick calls this because an idle machine reads the clock nowhere:
        // DrainExpiredTimers deliberately skips the counter when the queue is
        // empty, and Idle() then halts. Left alone long enough, the machine
        // misses its own wrap.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Refresh()
        {
            if (s_initialized && !s_is64Bit) ReadCounter();
        }

        // Extend a 32-bit reading to 64, given the last 64-bit value we
        // handed out. Pure and separable on purpose: the wrap is a property
        // of this arithmetic, not of the hardware, and it is the only part a
        // test can pin exactly.
        //
        // The step is taken as a SIGNED 32-bit difference rather than
        // "smaller means it wrapped". Two threads can interleave — one reads
        // the counter, loses the CPU, and extends against a latch another
        // thread has since moved forward. Its reading is then a few ticks
        // behind the latch, which the naive test calls a wrap and answers
        // with a timestamp 300 s in the future: a deadline nobody reaches,
        // the exact bug this whole change exists to remove. A signed
        // difference calls it what it is, a small step backwards.
        //
        // Correct while consecutive reads are less than 2^31 ticks apart —
        // 150 s at 14.318 MHz, against a refresh at 15.6 Hz.
        internal static ulong Extend(ulong previous, uint raw)
        {
            int delta = (int)(raw - (uint)previous);
            return previous + (ulong)(long)delta;
        }

        // Point the driver at a buffer instead of the chip, so a test can
        // drive the counter by hand and cross a wrap in microseconds. Returns
        // the previous base for RestoreRealBase; callers must restore it,
        // since while this is installed the whole kernel's clock is fiction.
        internal static byte* UseSyntheticBase(byte* buffer)
        {
            byte* previous = s_base;
            s_base = buffer;
            s_is64BitSaved = s_is64Bit;
            s_latchSaved = s_latch;
            s_is64Bit = false;
            s_latch = 0;
            s_initialized = true;
            return previous;
        }

        // The epoch is restored along with the base. Dropping it would cost
        // the machine every wrap it had accumulated — harmless in the first
        // five minutes of boot, where the probe runs, and silently wrong
        // anywhere else.
        internal static void RestoreRealBase(byte* previous)
        {
            s_base = previous;
            s_is64Bit = s_is64BitSaved;
            s_latch = s_latchSaved;
        }

        private static bool s_is64BitSaved;
        private static ulong s_latchSaved;
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

            // A 64-bit counter is read whole and is atomic on x64.
            if (s_is64Bit) return *(ulong*)(s_base + OFFSET_COUNTER);

            // A 32-bit one is not: the upper half of the register is
            // undefined, and the low half wraps every 2^32 ticks -- 300 s at
            // the 14.318 MHz this laptop reports. Reading 64 bits and calling
            // the result correct (as this did until step177) makes every
            // "deadline = now + delta" in the system unreachable once an hour
            // of uptime in: Sleep, Monitor.Wait, the AHCI and xHCI spins, and
            // every bounded wait CoreCLR computes.
            //
            // So carry the epoch ourselves. Correct as long as two reads are
            // never more than half the range apart, which the timer tick
            // guarantees by refreshing at ~15 Hz.
            ulong previous = s_latch;
            uint raw = *(uint*)(s_base + OFFSET_COUNTER);

            // Zero means nothing has been read yet (or the counter was just
            // rewritten, which zeroes both). Seed from the hardware instead
            // of extending against it: the counter need not be near zero when
            // we first look at it, and treating a large first reading as a
            // step from zero would place it half a range in the past.
            ulong extended = previous == 0 ? raw : Extend(previous, raw);
            s_latch = extended;
            return extended;
        }
    }
}
