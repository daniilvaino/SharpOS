// System.Diagnostics.Stopwatch — app tier, HPET-backed (step143; was a
// zero-stub until the kernel handed the identity-mapped HPET main counter
// + frequency over the service table). AppRuntime.Initialize wires the
// source; without HPET (headless/pre-EBS) Elapsed stays zero, same as the
// old stub.
//
// The counter read goes through a NoInlining helper: ILC's LICM hoists
// non-volatile MMIO reads out of spin loops (limits doc / AOT-MMIO gotcha),
// and callers of ElapsedTicks legitimately spin on it for frame pacing.

using System.Runtime.CompilerServices;

namespace System.Diagnostics
{
    public unsafe class Stopwatch
    {
        // Wired once at startup by AppRuntime.Initialize (no cctor).
        internal static ulong s_counterAddress;
        internal static ulong s_frequencyHz;

        // Width of the counter behind that address, and where the kernel
        // keeps the epoch for a narrow one. This laptop's HPET is 32-bit and
        // comes back to zero every 300 s; an app that read it as 64 bits saw
        // time jump backwards mid-session, which for frame pacing means a
        // frame that never ends (DOOM has no limiter at all) and for any
        // deadline means one that never arrives.
        //
        // The epoch is the kernel's and read-only here: its timer tick
        // refreshes it at ~15 Hz, far inside the half-range, so an app that
        // only wakes once a second still extends correctly without writing to
        // another tier's state.
        //
        // Stated as "is it narrow" rather than a width, because a field
        // initializer here would give the class a cctor, and cctors do not
        // run in this environment (limits doc, ClassConstructorRunner). The
        // default must be the common case, and the common case is 64 bits.
        internal static bool s_counterIsNarrow;
        internal static ulong s_latchAddress;

        private bool _isRunning;
        private ulong _startTicks;
        private ulong _accumulatedTicks;

        public static long Frequency => (long)s_frequencyHz;
        public static bool IsHighResolution => s_counterAddress != 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong ReadCounter()
        {
            ulong address = s_counterAddress;
            if (address == 0) return 0;
            if (!s_counterIsNarrow) return *(ulong*)address;

            uint raw = *(uint*)address;
            ulong* latch = (ulong*)s_latchAddress;
            ulong previous = latch == null ? 0UL : *latch;
            if (previous == 0) return raw;

            // Signed step, so a reading that lands a little behind the epoch
            // (the kernel refreshed it between our two instructions) counts
            // as a step back rather than a whole extra wrap.
            int delta = (int)(raw - (uint)previous);
            return previous + (ulong)(long)delta;
        }

        public static long GetTimestamp() => (long)ReadCounter();

        public void Start()
        {
            if (_isRunning) return;
            _startTicks = ReadCounter();
            _isRunning = true;
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _accumulatedTicks += ReadCounter() - _startTicks;
            _isRunning = false;
        }

        public void Reset()
        {
            _isRunning = false;
            _accumulatedTicks = 0;
        }

        public void Restart()
        {
            _accumulatedTicks = 0;
            _startTicks = ReadCounter();
            _isRunning = true;
        }

        public bool IsRunning => _isRunning;

        private ulong RawElapsedTicks
        {
            get
            {
                ulong total = _accumulatedTicks;
                if (_isRunning)
                    total += ReadCounter() - _startTicks;
                return total;
            }
        }

        public long ElapsedTicks => (long)RawElapsedTicks;

        public long ElapsedMilliseconds
            => s_frequencyHz == 0 ? 0 : (long)(RawElapsedTicks * 1000UL / s_frequencyHz);

        public TimeSpan Elapsed
        {
            get
            {
                if (s_frequencyHz == 0) return default;
                // TimeSpan tick = 100ns = 1e7/sec.
                return new TimeSpan((long)(RawElapsedTicks * 10_000_000UL / s_frequencyHz));
            }
        }

        public static Stopwatch StartNew()
        {
            var sw = new Stopwatch();
            sw.Start();
            return sw;
        }
    }
}
