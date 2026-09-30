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

        // The rest of the original, restored as callers appeared: the header's
        // cut list said components, arithmetic and parsing were dropped because
        // only Stopwatch read this type. Terminal.Gui's TimeField reads all of
        // them.
        public const long TicksPerHour = TicksPerMinute * 60;
        public const long TicksPerDay = TicksPerHour * 24;

        public static TimeSpan MinValue => new TimeSpan(long.MinValue);
        public static TimeSpan MaxValue => new TimeSpan(long.MaxValue);

        public TimeSpan(int hours, int minutes, int seconds)
            : this(hours * TicksPerHour + minutes * TicksPerMinute + seconds * TicksPerSecond) { }

        public TimeSpan(int days, int hours, int minutes, int seconds)
            : this(days * TicksPerDay + hours * TicksPerHour
                   + minutes * TicksPerMinute + seconds * TicksPerSecond) { }

        public int Days => (int)(_ticks / TicksPerDay);
        public int Hours => (int)((_ticks / TicksPerHour) % 24);
        public int Minutes => (int)((_ticks / TicksPerMinute) % 60);
        public int Seconds => (int)((_ticks / TicksPerSecond) % 60);
        public int Milliseconds => (int)((_ticks / TicksPerMillisecond) % 1000);

        public double TotalDays => (double)_ticks / TicksPerDay;
        public double TotalHours => (double)_ticks / TicksPerHour;
        public double TotalMinutes => (double)_ticks / TicksPerMinute;

        public static TimeSpan FromMilliseconds(double value)
            => new TimeSpan((long)(value * TicksPerMillisecond));

        public static TimeSpan FromMinutes(double value)
            => new TimeSpan((long)(value * TicksPerMinute));

        public static TimeSpan FromHours(double value)
            => new TimeSpan((long)(value * TicksPerHour));

        public static TimeSpan FromDays(double value)
            => new TimeSpan((long)(value * TicksPerDay));

        public TimeSpan Add(TimeSpan other) => new TimeSpan(_ticks + other._ticks);
        public TimeSpan Subtract(TimeSpan other) => new TimeSpan(_ticks - other._ticks);
        public TimeSpan Negate() => new TimeSpan(-_ticks);
        public TimeSpan Duration() => new TimeSpan(_ticks < 0 ? -_ticks : _ticks);

        public static TimeSpan operator +(TimeSpan a, TimeSpan b) => new TimeSpan(a._ticks + b._ticks);
        public static TimeSpan operator -(TimeSpan a, TimeSpan b) => new TimeSpan(a._ticks - b._ticks);
        public static TimeSpan operator -(TimeSpan t) => new TimeSpan(-t._ticks);

        public static bool operator ==(TimeSpan a, TimeSpan b) => a._ticks == b._ticks;
        public static bool operator !=(TimeSpan a, TimeSpan b) => a._ticks != b._ticks;
        public static bool operator <(TimeSpan a, TimeSpan b) => a._ticks < b._ticks;
        public static bool operator >(TimeSpan a, TimeSpan b) => a._ticks > b._ticks;
        public static bool operator <=(TimeSpan a, TimeSpan b) => a._ticks <= b._ticks;
        public static bool operator >=(TimeSpan a, TimeSpan b) => a._ticks >= b._ticks;

        public bool Equals(TimeSpan other) => _ticks == other._ticks;
        public override bool Equals(object? obj) => obj is TimeSpan t && t._ticks == _ticks;
        public override int GetHashCode() => (int)_ticks ^ (int)(_ticks >> 32);
        public int CompareTo(TimeSpan other) => _ticks < other._ticks ? -1 : _ticks > other._ticks ? 1 : 0;

        /// <summary>
        /// Formats as [-][d.]hh:mm:ss. The day part appears only when there is
        /// one, matching the BCL's default.
        /// </summary>
        public override string ToString()
        {
            long ticks = _ticks < 0 ? -_ticks : _ticks;
            var t = new TimeSpan(ticks);

            string sign = _ticks < 0 ? "-" : "";
            string body = Pad(t.Hours, 2) + ":" + Pad(t.Minutes, 2) + ":" + Pad(t.Seconds, 2);
            return t.Days != 0 ? sign + t.Days.ToString() + "." + body : sign + body;
        }

        public string ToString(string? format) => ToString();

        public string ToString(string? format, IFormatProvider? provider) => ToString();

        private static string Pad(int value, int width)
        {
            string s = value.ToString();
            while (s.Length < width) s = "0" + s;
            return s;
        }

        /// <summary>
        /// Parses hh:mm:ss written exactly as the pattern says. Strict for the
        /// same reason DateTime.TryParseExact is: a lenient parser would accept
        /// shapes this type never produces.
        /// </summary>
        public static bool TryParseExact(string? input, string? format,
            IFormatProvider? provider, System.Globalization.TimeSpanStyles styles, out TimeSpan result)
        {
            result = Zero;
            if (string.IsNullOrEmpty(input)) return false;

            int hours = 0, minutes = 0, seconds = 0;
            int at = 0;

            if (!Take(input!, ref at, 2, out hours)) return false;
            if (at >= input!.Length || input[at] != ':') return false;
            at++;
            if (!Take(input, ref at, 2, out minutes)) return false;

            if (at < input.Length && input[at] == ':')
            {
                at++;
                if (!Take(input, ref at, 2, out seconds)) return false;
            }

            if (hours > 23 || minutes > 59 || seconds > 59) return false;

            result = new TimeSpan(hours, minutes, seconds);
            return true;
        }

        public static bool TryParseExact(string? input, string? format,
            IFormatProvider? provider, out TimeSpan result)
            => TryParseExact(input, format, provider,
                             System.Globalization.TimeSpanStyles.None, out result);

        public static bool TryParse(string? input, out TimeSpan result)
            => TryParseExact(input, "hh\\:mm\\:ss", null,
                             System.Globalization.TimeSpanStyles.None, out result);

        private static bool Take(string input, ref int at, int width, out int value)
        {
            value = 0;
            int taken = 0;

            while (taken < width && at < input.Length && input[at] >= '0' && input[at] <= '9')
            {
                value = value * 10 + (input[at] - '0');
                at++;
                taken++;
            }

            return taken > 0;
        }
    }
}
