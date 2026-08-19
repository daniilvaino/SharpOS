using OS.Hal.Timer;
using OS.Kernel.Memory;

namespace OS.Hal.Apic
{
    // Local APIC — the first interrupt controller SharpOS owns.
    //
    // Chosen over the IO-APIC on purpose: preemption needs a periodic tick,
    // not devices, and the local APIC's timer lives inside the core with no
    // routing to configure. It is also per-CPU, so the same code is what each
    // core will run when SMP arrives. Device interrupts stay on the polling
    // path until the IO-APIC is worth its own step.
    //
    // xAPIC (memory-mapped) rather than x2APIC (MSR): the base address is
    // already in the MADT, and the register window is one page. x2APIC buys
    // nothing until there are more cores than an 8-bit APIC id can name.
    //
    // Everything here runs post-ExitBootServices, after Pic.MaskAll — before
    // that the firmware owns interrupt delivery and must keep it.
    internal static unsafe class LocalApic
    {
        // Register offsets within the 4 KiB window (Intel SDM Vol. 3A §11.4.1).
        private const uint RegId               = 0x020;
        private const uint RegVersion          = 0x030;
        private const uint RegEoi              = 0x0B0;
        private const uint RegSpurious         = 0x0F0;
        private const uint RegLvtTimer         = 0x320;
        private const uint RegTimerInitCount   = 0x380;
        private const uint RegTimerCurrentCount= 0x390;
        private const uint RegTimerDivide      = 0x3E0;

        // Spurious register: bit 8 is the software enable. The low byte is the
        // vector the CPU raises when an interrupt is withdrawn between being
        // signalled and being taken — it must be a vector we handle rather
        // than a hole in the IDT, which is why it points at our own stub.
        private const uint SpuriousEnable = 1u << 8;

        // LVT timer: bit 16 masks, bit 17 selects periodic mode.
        private const uint LvtMasked   = 1u << 16;
        private const uint LvtPeriodic = 1u << 17;

        // Divide configuration 0b1010 = divide by 128. The coarsest setting
        // available, chosen so the initial count for a 100 Hz tick stays well
        // inside 32 bits on fast cores.
        private const uint DivideBy128 = 0xA;

        private static byte* s_base;
        private static bool s_enabled;
        private static uint s_ticksPerSecond;
        private static uint s_initialCount;
        private static byte s_vector;
        private static ulong s_timerTicks;
        private static ulong s_spurious;

        public static bool IsEnabled => s_enabled;

        /// <summary>Periodic timer interrupts taken since the timer was armed.</summary>
        public static ulong TimerTicks => s_timerTicks;

        /// <summary>Spurious interrupts observed. Non-zero is not an error, but
        /// a growing count means something is being withdrawn repeatedly.</summary>
        public static ulong SpuriousCount => s_spurious;

        // Called from the interrupt dispatcher. Deliberately the smallest
        // possible body: everything here runs with interrupts disabled, on
        // whatever stack was current, and will one day run inside the
        // preemption path where every instruction is paid for on every tick.
        public static void OnTimerTick() => s_timerTicks++;

        public static void OnSpurious() => s_spurious++;

        /// <summary>Measured local-APIC timer frequency, in counts per second.</summary>
        public static uint TicksPerSecond => s_ticksPerSecond;

        /// <summary>
        /// Map the register window and software-enable the APIC. Returns false
        /// if the MADT gave no address or the mapping failed — the caller then
        /// stays on the polling path rather than proceeding half-configured.
        /// </summary>
        public static bool Initialize(byte spuriousVector)
        {
            if (s_enabled) return true;

            ulong physical = Acpi.Madt.LocalApicAddress;
            if (physical == 0) return false;

            // Device memory, not RAM: these are registers, and a cached read
            // returns the same value forever. Same reasoning as the HPET.
            if (!VirtualMemory.MapFixed((void*)physical, physical, 0x1000,
                                        exec: false, VirtualMemory.MemoryKind.Device))
            {
                return false;
            }

            s_base = (byte*)physical;

            // The hardware enable in IA32_APIC_BASE (bit 11) is normally
            // already set by firmware; setting it again is harmless and makes
            // the state ours rather than inherited.
            if (X64Asm.ReadMsr(0x1B, out ulong apicBase))
                X64Asm.WriteMsr(0x1B, apicBase | (1UL << 11));

            Write(RegSpurious, SpuriousEnable | spuriousVector);
            s_enabled = true;
            return true;
        }

        public static uint Id => s_enabled ? Read(RegId) >> 24 : 0;

        public static uint Version => s_enabled ? Read(RegVersion) & 0xFF : 0;

        /// <summary>
        /// Acknowledge the interrupt currently in service. Every handler must
        /// do this before returning: without it the APIC considers the vector
        /// still in service and delivers nothing further — the failure looks
        /// like a clock that ticked exactly once.
        /// </summary>
        public static void EndOfInterrupt()
        {
            if (s_enabled) Write(RegEoi, 0);
        }

        /// <summary>
        /// Calibrate against the HPET and start a periodic interrupt at
        /// <paramref name="hz"/> on <paramref name="vector"/>. Returns false if
        /// the HPET is unusable — guessing a frequency would give a clock that
        /// is silently wrong, which is worse than no clock.
        /// </summary>
        public static bool StartPeriodic(byte vector, uint hz)
        {
            if (!s_enabled || hz == 0) return false;
            if (!Hpet.IsInitialized || Hpet.FrequencyHz == 0) return false;

            Write(RegTimerDivide, DivideBy128);

            // Count down from the maximum for a known slice of HPET time and
            // see how far we got. One-shot (not periodic) so the count cannot
            // wrap mid-measurement.
            Write(RegLvtTimer, LvtMasked);
            Write(RegTimerInitCount, 0xFFFFFFFF);

            ulong hpetTicks = Hpet.FrequencyHz / 100;      // 10 ms
            ulong start = Hpet.ReadCounter();
            while (Hpet.ReadCounter() - start < hpetTicks) { }

            uint remaining = Read(RegTimerCurrentCount);
            Write(RegTimerInitCount, 0);                   // stop counting

            uint elapsed = 0xFFFFFFFFu - remaining;
            if (elapsed == 0) return false;                // timer never moved

            s_ticksPerSecond = elapsed * 100;

            uint initial = s_ticksPerSecond / hz;
            if (initial == 0) return false;

            s_initialCount = initial;
            s_vector = vector;
            Write(RegLvtTimer, LvtPeriodic | vector);
            Write(RegTimerInitCount, initial);
            return true;
        }

        /// <summary>
        /// Check the rate the timer actually delivers and correct it. Must run
        /// with interrupts enabled — it counts real interrupts, not register
        /// reads.
        ///
        /// Calibration measures the countdown for 10 ms and trusts the answer.
        /// On VirtualBox that answer came out five times too high: the timer
        /// was armed for 20 Hz while everything assumed 100, so every thread
        /// waited up to 50 ms for its turn and keystrokes lagged visibly. The
        /// estimate was never wrong in a way anything checked — it was simply
        /// believed. Measuring what was delivered costs one window and turns a
        /// silent five-fold error into a corrected clock.
        /// </summary>
        public static bool RetuneToDeliveredRate(uint hz, uint rounds = 3)
        {
            if (!s_enabled || hz == 0 || s_initialCount == 0) return false;
            if (!Hpet.IsInitialized || Hpet.FrequencyHz == 0) return false;

            for (uint round = 0; round < rounds; round++)
            {
                ulong before = s_timerTicks;
                ulong hpetStart = Hpet.ReadCounter();
                ulong window = Hpet.FrequencyHz / 10;           // 100 ms
                while (Hpet.ReadCounter() - hpetStart < window) { }

                ulong observed = s_timerTicks - before;
                ulong expected = hz / 10;
                if (expected == 0) return false;
                if (observed == 0) return false;                // not ticking at all

                // Within a tenth is as close as this needs to be; the point is
                // to catch a clock off by a factor, not to trim percentages.
                ulong low = expected - (expected / 10);
                ulong high = expected + (expected / 10);
                if (observed >= low && observed <= high) return true;

                ulong scaled = (ulong)s_initialCount * observed / expected;
                if (scaled == 0) scaled = 1;
                if (scaled > 0xFFFFFFFFUL) scaled = 0xFFFFFFFFUL;

                s_initialCount = (uint)scaled;
                Write(RegTimerInitCount, s_initialCount);
            }

            return false;
        }

        public static void StopTimer()
        {
            if (!s_enabled) return;
            Write(RegLvtTimer, LvtMasked);
            Write(RegTimerInitCount, 0);
        }

        private static void Write(uint register, uint value)
            => *(uint*)(s_base + register) = value;

        // Non-inlined so the compiler cannot hoist a register read out of a
        // wait loop and spin forever on a stale value — the same trap that
        // froze the HPET poll and the emulator's frame pacing.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static uint Read(uint register)
            => *(uint*)(s_base + register);
    }
}
