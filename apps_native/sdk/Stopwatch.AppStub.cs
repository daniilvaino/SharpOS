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

        private bool _isRunning;
        private ulong _startTicks;
        private ulong _accumulatedTicks;

        public static long Frequency => (long)s_frequencyHz;
        public static bool IsHighResolution => s_counterAddress != 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong ReadCounter()
        {
            ulong address = s_counterAddress;
            return address == 0 ? 0 : *(ulong*)address;
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
