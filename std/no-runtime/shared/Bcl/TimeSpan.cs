// System.TimeSpan — minimal port from dotnet/runtime v8.0.27
//   src/libraries/System.Private.CoreLib/src/System/TimeSpan.cs (MIT)
// Tick constants and field name (_ticks) verbatim.
//
// Cuts vs original: parsing/formatting, arithmetic operators, comparison
// interfaces, From{Days,Hours,Minutes,...} factories beyond FromSeconds,
// Days/Hours/... component properties. The consumers today are
// Stopwatch.Elapsed readers (TotalSeconds/TotalMilliseconds).

namespace System
{
    public readonly struct TimeSpan
    {
        public const long TicksPerMillisecond = 10_000;
        public const long TicksPerSecond = TicksPerMillisecond * 1000;
        public const long TicksPerMinute = TicksPerSecond * 60;

        private readonly long _ticks;

        public TimeSpan(long ticks)
        {
            _ticks = ticks;
        }

        public static TimeSpan Zero => default;

        public long Ticks => _ticks;

        public double TotalSeconds => (double)_ticks / TicksPerSecond;
        public double TotalMilliseconds => (double)_ticks / TicksPerMillisecond;

        public static TimeSpan FromTicks(long value) => new TimeSpan(value);

        public static TimeSpan FromSeconds(double value)
            => new TimeSpan((long)(value * TicksPerSecond));
    }
}
