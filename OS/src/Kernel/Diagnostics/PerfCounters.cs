using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    /// <summary>What <see cref="PerfCounters"/> counts.</summary>
    internal enum PerfCounter
    {
        // SharpOSHost_GetUtcFileTime: every hosted clock reads through it.
        ClockCalls,
        ClockTicks,

        // Output: disk-log sector writes and their time, characters per
        // serial port, terminal repaints (scrolling included) and their time.
        DiskLogWrites,
        DiskLogTicks,
        Com1Chars,
        Com3Chars,
        Com4Chars,
        ScreenPaints,
        ScreenTicks,

        // A program's writes as the kernel serves them, whichever tier made
        // them: the NativeAOT services and the hosted console handles. Time
        // includes everything below — disk log, ports, screen.
        ProgramWrites,
        ProgramWriteTicks,
        ProgramWriteChars,

        // Where every written character's time goes, per sink, in TSC ticks
        // (see Tsc): disk log, serial ports, terminal engine. The terminal's
        // includes the paints a line break triggers.
        SinkDiskLogTsc,
        SinkSerialTsc,
        SinkTerminalTsc,

        // Inside the terminal sink: waiting for the engine lock, feeding the
        // engine (TSC ticks), and characters dropped because the lock never
        // came free.
        TerminalLockTsc,
        TerminalFeedTsc,
        TerminalDrops,

        // Collections of the kernel's own heap, and their time.
        KernelGcs,
        KernelGcTicks,

        // Blocking waits for an xHCI event (disk transfers, commands; not the
        // keyboard's non-blocking polls), their time, and the turns of the
        // wait loop.
        UsbWaits,
        UsbWaitTicks,
        UsbWaitSpins,

        // Exceptions: RaiseException calls, function-table lookups (and which
        // table answered), R2R tables walked past before one did, and
        // virtual unwinds.
        SehRaises,
        SehLookups,
        SehLookupTicks,
        SehLookupImage,
        SehLookupJit,
        SehLookupR2r,
        SehLookupStub,
        SehLookupGap,
        SehR2rTablesScanned,
        SehUnwinds,
        SehUnwindTicks,

        Count,
    }

    /// <summary>
    /// Counters on the kernel paths the hosted runtime leans on, and the
    /// <c>[perf] scope.name=value</c> lines that report them.
    /// </summary>
    /// <remarks>
    /// Measured by the kernel, with the HPET, on purpose. The hosted runtime's
    /// own clocks all end in the wall clock counted here; numbers that do not
    /// depend on them stay honest whatever state that clock is in.
    ///
    /// Updated with Interlocked: the runtime calls in from many threads, and
    /// preemption lands between a read and a write. Reported as deltas between
    /// <see cref="Mark"/> and <see cref="Report"/>, so each run is its own
    /// measurement whatever ran before it.
    ///
    /// Timed counters run on the timestamp counter (<see cref="Now"/>), and
    /// only the interval's ends are read from the HPET — which then also
    /// converts the ticks. They used to read the HPET on every call: under
    /// QEMU that is an emulated-device access of about two microseconds, and
    /// the exception path, timed at its seven kernel calls a throw, spent a
    /// third of its time reading the clock that measured it (step170).
    ///
    /// A fixed buffer, not an array: an array would need a class constructor
    /// to allocate it, and those do not run here (limits §1).
    /// </remarks>
    internal static unsafe class PerfCounters
    {
        private const int Slots = (int)PerfCounter.Count;

        private struct Values { public fixed long V[Slots]; }

        private static Values s_values;
        private static Values s_mark;
        private static ulong s_markCounter;
        private static ulong s_markTsc;

        /// <summary>
        /// Start of a timed call, for the Count* methods; 0 when there is no
        /// counter yet, and then the call is not counted.
        /// </summary>
        public static ulong Now() => Tsc();

        /// <summary>
        /// The timestamp counter. Its rate is not known up front; the report
        /// converts with the HPET time of the same interval.
        /// </summary>
        public static ulong Tsc()
            => OS.Hal.X64Asm.ReadTsc(out ulong value) ? value : 0;

        // The interval's ends: the HPET, whose rate is known.
        private static ulong Wall()
            => OS.Hal.Timer.Hpet.IsInitialized ? OS.Hal.Timer.Hpet.ReadCounter() : 0;

        /// <summary>
        /// Times every written character per sink (sink.* and terminal.*
        /// lines). Off: two counter reads per sink per character cost a
        /// quarter to a third of what output itself costs (step169). Turn on
        /// to find where output time goes; off, the calls fold away.
        /// </summary>
        public const bool TimeSinks = false;

        /// <summary><see cref="Tsc"/> when <see cref="TimeSinks"/> is on, 0 otherwise.</summary>
        public static ulong SinkClock() => TimeSinks ? Tsc() : 0;

        public static void CountTsc(PerfCounter ticks, ulong started)
        {
            if (started == 0)
                return;
            Add(ticks, (long)(Tsc() - started));
        }

        public static void Add(PerfCounter counter, long amount)
        {
            fixed (long* v = s_values.V)
                System.Threading.Interlocked.Add(ref v[(int)counter], amount);
        }

        public static void Increment(PerfCounter counter) => Add(counter, 1);

        /// <summary>Counts one call of a timed pair that started at <paramref name="started"/>.</summary>
        public static void CountTimed(PerfCounter calls, PerfCounter ticks, ulong started)
        {
            if (started == 0)
                return;

            Add(calls, 1);
            Add(ticks, (long)(Now() - started));
        }

        public static void CountClock(ulong started) => CountTimed(PerfCounter.ClockCalls, PerfCounter.ClockTicks, started);

        public static void CountDiskLog(ulong started) => CountTimed(PerfCounter.DiskLogWrites, PerfCounter.DiskLogTicks, started);

        public static void CountScreen(ulong started) => CountTimed(PerfCounter.ScreenPaints, PerfCounter.ScreenTicks, started);

        /// <summary>Counts one program write of <paramref name="characters"/> characters.</summary>
        public static void CountProgramWrite(ulong started, long characters)
        {
            if (started == 0)
                return;

            CountTimed(PerfCounter.ProgramWrites, PerfCounter.ProgramWriteTicks, started);
            Add(PerfCounter.ProgramWriteChars, characters);
        }

        // When the program being measured first sat waiting for a key.
        private static ulong s_firstInputTsc;

        /// <summary>
        /// Called by the console reads each time a program waits for a key
        /// that is not there; the first one of an interval is its time to
        /// prompt (<c>first_input_ms</c>).
        /// </summary>
        /// <remarks>
        /// A shell's startup ends where it asks for input, not where it exits:
        /// an interactive session's wall time is mostly the person at the
        /// keyboard. The same point is what a Linux reference can see from
        /// outside — the prompt on the terminal.
        /// </remarks>
        public static void NoteInputWait()
        {
            if (s_markTsc == 0)
                return;

            if (s_firstInputTsc == 0)
                s_firstInputTsc = Tsc();

            // The key handed over last is now dealt with: the program is back
            // asking for the next one.
            if (s_keyPending)
            {
                s_keyPending = false;
                if (s_keyCount < MaxKeys)
                {
                    fixed (ulong* t = s_keyTicks.V) t[s_keyCount] = Tsc() - s_keyTsc;
                    fixed (ushort* k = s_keyVk.V) k[s_keyCount] = s_keyLastVk;
                    s_keyCount++;
                }
            }
        }

        // How long a program was busy with each key: from the console read
        // that delivered it to the next time the program waited for input.
        // For a shell, Enter is the command and its next prompt, Tab is the
        // completion. Keys typed ahead merge into the next wait's episode.
        private const int MaxKeys = 64;
        private struct KeyTicks { public fixed ulong V[MaxKeys]; }
        private struct KeyVks { public fixed ushort V[MaxKeys]; }
        private static KeyTicks s_keyTicks;
        private static KeyVks s_keyVk;
        private static int s_keyCount;
        private static bool s_keyPending;
        private static ulong s_keyTsc;
        private static ushort s_keyLastVk;

        /// <summary>
        /// Called by the console reads when they hand a key to the program;
        /// <paramref name="virtualKey"/> is its Windows virtual-key code.
        /// </summary>
        public static void NoteKeyDelivered(ushort virtualKey)
        {
            if (s_markTsc == 0)
                return;

            s_keyTsc = Tsc();
            s_keyLastVk = virtualKey;
            s_keyPending = true;
        }

        /// <summary>Starts a measured interval.</summary>
        public static void Mark()
        {
            s_firstInputTsc = 0;
            s_keyCount = 0;
            s_keyPending = false;
            fixed (long* v = s_values.V)
            fixed (long* m = s_mark.V)
            {
                for (int i = 0; i < Slots; i++)
                    m[i] = System.Threading.Interlocked.Add(ref v[i], 0);
            }
            s_markCounter = Wall();
            s_markTsc = Tsc();
            Sampler.BeginWindow();
        }

        /// <summary>Reports what happened since <see cref="Mark"/>, under <paramref name="scope"/>.</summary>
        public static void Report(string scope)
        {
            ulong elapsed = s_markCounter == 0 ? 0 : Wall() - s_markCounter;
            ulong elapsedTsc = s_markTsc == 0 ? 0 : Tsc() - s_markTsc;
            ulong elapsedUs = TicksToNs(elapsed) / 1000;

            // Every delta is taken before the first line goes out: the report
            // is output too, and would otherwise count itself.
            Values delta = default;
            fixed (long* v = s_values.V)
            fixed (long* m = s_mark.V)
            {
                for (int i = 0; i < Slots; i++)
                    delta.V[i] = System.Threading.Interlocked.Add(ref v[i], 0) - m[i];
            }

            s_reportTsc = elapsedTsc;
            s_reportUs = elapsedUs;

            Line(scope, "wall_ms", TicksToNs(elapsed) / 1_000_000);
            ulong firstInput = s_firstInputTsc;
            Line(scope, "first_input_ms", firstInput == 0 ? 0
                : TscToUs(firstInput - s_markTsc, elapsedTsc, elapsedUs) / 1000);

            // Keys the program took at least 20 ms over, in the order pressed:
            // key.<nn>.<enter|tab|key>_ms. A typed letter that echoes at once
            // is not worth a line; a command or a completion is.
            int keys = s_keyCount;
            for (int i = 0; i < keys; i++)
            {
                ulong ticks, ms;
                ushort vk;
                fixed (ulong* t = s_keyTicks.V) ticks = t[i];
                fixed (ushort* k = s_keyVk.V) vk = k[i];
                ms = TscToUs(ticks, elapsedTsc, elapsedUs) / 1000;
                if (ms < 20)
                    continue;

                string number = SharpOS.Std.NoRuntime.NumberFormatting.ULongToString((ulong)i);
                string index = i < 10 ? "0" + number : number;
                string kind = vk == 0x0D ? "enter" : vk == 0x09 ? "tab" : "key";
                Line(scope, "key." + index + "." + kind + "_ms", ms);
            }
            Timed(scope, "clock", ref delta, PerfCounter.ClockCalls, PerfCounter.ClockTicks);
            Timed(scope, "disklog", ref delta, PerfCounter.DiskLogWrites, PerfCounter.DiskLogTicks);
            Line(scope, "com1.chars", Get(ref delta, PerfCounter.Com1Chars));
            Line(scope, "com3.chars", Get(ref delta, PerfCounter.Com3Chars));
            Line(scope, "com4.chars", Get(ref delta, PerfCounter.Com4Chars));
            Timed(scope, "screen", ref delta, PerfCounter.ScreenPaints, PerfCounter.ScreenTicks);
            Timed(scope, "write", ref delta, PerfCounter.ProgramWrites, PerfCounter.ProgramWriteTicks);
            Line(scope, "write.chars", Get(ref delta, PerfCounter.ProgramWriteChars));
            if (TimeSinks)
            {
                Line(scope, "sink.disklog_ms", TscToUs(Get(ref delta, PerfCounter.SinkDiskLogTsc), elapsedTsc, elapsedUs) / 1000);
                Line(scope, "sink.serial_ms", TscToUs(Get(ref delta, PerfCounter.SinkSerialTsc), elapsedTsc, elapsedUs) / 1000);
                Line(scope, "sink.terminal_ms", TscToUs(Get(ref delta, PerfCounter.SinkTerminalTsc), elapsedTsc, elapsedUs) / 1000);
                Line(scope, "terminal.lock_ms", TscToUs(Get(ref delta, PerfCounter.TerminalLockTsc), elapsedTsc, elapsedUs) / 1000);
                Line(scope, "terminal.feed_ms", TscToUs(Get(ref delta, PerfCounter.TerminalFeedTsc), elapsedTsc, elapsedUs) / 1000);
            }
            Line(scope, "terminal.drops", Get(ref delta, PerfCounter.TerminalDrops));
            Timed(scope, "kgc", ref delta, PerfCounter.KernelGcs, PerfCounter.KernelGcTicks);
            Timed(scope, "usb.wait", ref delta, PerfCounter.UsbWaits, PerfCounter.UsbWaitTicks);
            Line(scope, "usb.wait.spins", Get(ref delta, PerfCounter.UsbWaitSpins));
            Line(scope, "seh.raises", Get(ref delta, PerfCounter.SehRaises));
            Timed(scope, "seh.lookup", ref delta, PerfCounter.SehLookups, PerfCounter.SehLookupTicks);
            Line(scope, "seh.lookup.image", Get(ref delta, PerfCounter.SehLookupImage));
            Line(scope, "seh.lookup.jit", Get(ref delta, PerfCounter.SehLookupJit));
            Line(scope, "seh.lookup.r2r", Get(ref delta, PerfCounter.SehLookupR2r));
            Line(scope, "seh.lookup.stub", Get(ref delta, PerfCounter.SehLookupStub));
            Line(scope, "seh.lookup.gap", Get(ref delta, PerfCounter.SehLookupGap));
            Line(scope, "seh.r2r_tables_scanned", Get(ref delta, PerfCounter.SehR2rTablesScanned));
            Timed(scope, "seh.unwind", ref delta, PerfCounter.SehUnwinds, PerfCounter.SehUnwindTicks);

            // Where the interval's time went, by address.
            Sampler.ReportWindow(scope);
        }

        private static ulong Get(ref Values values, PerfCounter counter)
            => (ulong)values.V[(int)counter];

        // The interval being reported: its TSC ticks and its HPET time, which
        // together convert the timed counters.
        private static ulong s_reportTsc;
        private static ulong s_reportUs;

        // calls, total time, and time per call for one timed pair.
        private static void Timed(string scope, string name, ref Values delta, PerfCounter calls, PerfCounter ticks)
        {
            ulong n = Get(ref delta, calls);
            ulong ns = TscToUs(Get(ref delta, ticks), s_reportTsc, s_reportUs) * 1000;
            Line(scope, name + ".calls", n);
            Line(scope, name + ".total_ms", ns / 1_000_000);
            Line(scope, name + ".avg_ns", n == 0 ? 0 : ns / n);
        }

        // Split so that neither product can overflow: a naive ticks * 1e9
        // does after three minutes at the QEMU HPET's 100 MHz.
        private static ulong TicksToNs(ulong ticks)
        {
            ulong hz = OS.Hal.Timer.Hpet.FrequencyHz;
            if (hz == 0)
                return 0;
            return ticks / hz * 1_000_000_000UL + ticks % hz * 1_000_000_000UL / hz;
        }

        // TSC ticks to microseconds at the rate the interval itself showed:
        // elapsedTsc ticks took elapsedUs. Split like TicksToNs, and both
        // sides of the ratio halved until the remainder product fits: an
        // interactive session is an interval of minutes.
        private static ulong TscToUs(ulong ticks, ulong elapsedTsc, ulong elapsedUs)
        {
            while (elapsedTsc > uint.MaxValue)
            {
                elapsedTsc >>= 1;
                ticks >>= 1;
            }
            if (elapsedTsc == 0)
                return 0;
            return ticks / elapsedTsc * elapsedUs + ticks % elapsedTsc * elapsedUs / elapsedTsc;
        }

        private static void Line(string scope, string name, ulong value)
        {
            Put("[perf] ");
            Put(scope);
            Put(".");
            Put(name);
            Put("=");
            Put(SharpOS.Std.NoRuntime.NumberFormatting.ULongToString(value));
            Put("\n");
        }

        private static void Put(string text)
        {
            for (int i = 0; i < text.Length; i++)
                Platform.WriteChar(text[i], OutputChannel.Perf);
        }
    }
}
