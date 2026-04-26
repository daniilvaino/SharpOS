using OS.Hal.Timer;

namespace System.Diagnostics
{
    // System.Diagnostics.Stopwatch — minimal port using HPET as the time
    // source. BCL has TimeSpan-returning properties which we skip (no
    // TimeSpan struct in our std). Elapsed* exposes ms / us / ticks.
    //
    // BCL semantics:
    //   - Start/Stop accumulate elapsed; Reset clears; Restart = Reset+Start.
    //   - StartNew() is the conventional factory.
    //   - Frequency = ticks per second.
    //   - GetTimestamp() = raw counter snapshot.
    //
    // Threading: not safe — single-threaded use only (kernel boot is
    // single-threaded; SUPER-Phase 3 scheduler will add per-thread
    // Stopwatch instances).
    public sealed class Stopwatch
    {
        private long _elapsed;       // accumulated ticks across Start/Stop pairs
        private long _startTimestamp;
        private bool _isRunning;

        public static long Frequency => (long)Hpet.FrequencyHz;
        public static bool IsHighResolution => true;

        public bool IsRunning => _isRunning;

        public long ElapsedTicks
        {
            get
            {
                long ticks = _elapsed;
                if (_isRunning) ticks += GetTimestamp() - _startTimestamp;
                return ticks;
            }
        }

        public long ElapsedMilliseconds
        {
            get
            {
                long freq = Frequency;
                if (freq <= 0) return 0;
                return ElapsedTicks * 1000L / freq;
            }
        }

        public long ElapsedMicroseconds
        {
            get
            {
                long freq = Frequency;
                if (freq <= 0) return 0;
                return ElapsedTicks * 1_000_000L / freq;
            }
        }

        public void Start()
        {
            if (!_isRunning)
            {
                _startTimestamp = GetTimestamp();
                _isRunning = true;
            }
        }

        public void Stop()
        {
            if (_isRunning)
            {
                _elapsed += GetTimestamp() - _startTimestamp;
                _isRunning = false;
            }
        }

        public void Reset()
        {
            _elapsed = 0;
            _startTimestamp = 0;
            _isRunning = false;
        }

        public void Restart()
        {
            _elapsed = 0;
            _startTimestamp = GetTimestamp();
            _isRunning = true;
        }

        public static Stopwatch StartNew()
        {
            var sw = new Stopwatch();
            sw.Start();
            return sw;
        }

        public static long GetTimestamp() => (long)Hpet.ReadCounter();
    }
}
