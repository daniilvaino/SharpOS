namespace OS.Hal.Audio
{
    /// <summary>
    /// The PC speaker (step248): channel 2 of the 8254 PIT as a square wave,
    /// gated onto the speaker by port 0x61 bits 0 (timer gate) and 1 (speaker
    /// data). Every x86 has the ports; whether a speaker is wired to them no
    /// program can tell — Present only says channel 2 counts (its output, bit 5
    /// of 0x61, toggles), which is as far as software can see.
    /// </summary>
    /// <remarks>
    /// A tone has an end time, kept here: a program that dies mid-beep must
    /// not leave the machine whining. A kernel thread, started by the first
    /// beep, switches it off at the deadline.
    /// </remarks>
    internal static unsafe class PcSpeaker
    {
        private const ushort PitChannel2 = 0x42;
        private const ushort PitCommand = 0x43;
        private const ushort SystemControl = 0x61;
        private const uint PitHz = 1_193_182;

        private static bool s_probed, s_present, s_threadStarted;
        private static ulong s_offAt;          // HPET ticks; 0 = no tone

        /// <summary>Channel 2 of the PIT counts (checked once).</summary>
        public static bool Present
        {
            get
            {
                if (!s_probed) { s_probed = true; s_present = Probe(); }
                return s_present;
            }
        }

        // Gate on, speaker data off (silent), channel 2 as a fast square wave:
        // its output bit must change within a few thousand port reads.
        private static bool Probe()
        {
            byte control = PortIo.In8(SystemControl);
            PortIo.Out8(SystemControl, (byte)((control & ~0x2) | 0x1));
            SetDivisor(64);
            byte first = (byte)(PortIo.In8(SystemControl) & 0x20);
            bool toggles = false;
            for (int i = 0; i < 100_000 && !toggles; i++)
                toggles = (PortIo.In8(SystemControl) & 0x20) != first;
            PortIo.Out8(SystemControl, (byte)(control & ~0x3));
            return toggles;
        }

        // Mode 3 (square wave), lobyte then hibyte, binary.
        private static void SetDivisor(uint divisor)
        {
            PortIo.Out8(PitCommand, 0xB6);
            PortIo.Out8(PitChannel2, (byte)divisor);
            PortIo.Out8(PitChannel2, (byte)(divisor >> 8));
        }

        /// <summary>
        /// A tone of <paramref name="hz"/> for <paramref name="ms"/>, without
        /// waiting; 0 Hz stops one. False without the speaker or a clock.
        /// </summary>
        public static bool Tone(int hz, int ms)
        {
            if (!Present) return false;
            if (hz <= 0 || ms <= 0)
            {
                Off();
                return true;
            }
            if (!OS.Hal.Timer.Hpet.IsInitialized) return false;

            uint divisor = PitHz / (uint)(hz < 19 ? 19 : hz > 20_000 ? 20_000 : hz);
            OS.Kernel.Threading.Preemption.Suppress();
            SetDivisor(divisor);
            PortIo.Out8(SystemControl, (byte)(PortIo.In8(SystemControl) | 0x3));
            s_offAt = OS.Hal.Timer.Hpet.ReadCounter() + OS.Hal.Timer.Hpet.FrequencyHz * (ulong)ms / 1000UL;
            OS.Kernel.Threading.Preemption.Allow();

            if (!s_threadStarted)
            {
                s_threadStarted = true;
                OS.Kernel.Threading.Scheduler.Spawn(&WatchEntry, 8 * 1024);
            }
            return true;
        }

        private static void Off()
        {
            OS.Kernel.Threading.Preemption.Suppress();
            s_offAt = 0;
            PortIo.Out8(SystemControl, (byte)(PortIo.In8(SystemControl) & ~0x3));
            OS.Kernel.Threading.Preemption.Allow();
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WatchEntry()
        {
            while (true)
            {
                OS.Kernel.Threading.Scheduler.Sleep(2);
                // Under suppression: a beep started between the check and the
                // switch-off would otherwise be cut by the previous one's end.
                OS.Kernel.Threading.Preemption.Suppress();
                ulong offAt = s_offAt;
                if (offAt != 0 && OS.Hal.Timer.Hpet.ReadCounter() >= offAt)
                {
                    s_offAt = 0;
                    PortIo.Out8(SystemControl, (byte)(PortIo.In8(SystemControl) & ~0x3));
                }
                OS.Kernel.Threading.Preemption.Allow();
            }
        }
    }
}
