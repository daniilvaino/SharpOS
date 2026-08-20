// System.DateTime — a real calendar, not a stub.
//
// The app tier carried a DateTime whose Now was the epoch and whose only other
// member was a Millisecond that always returned zero. That was honest while
// nothing read it for anything but seeding a random number generator, and it
// stops being honest the moment a date field asks what year it is.
//
// Ported in shape from dotnet/runtime (MIT) — same ticks-since-0001 origin, same
// day-number arithmetic — with the parts that need culture data or a full
// format grammar left out rather than approximated. What is here:
//
//   * the civil calendar: year, month, day, hour, minute, second, leap years;
//   * comparison and arithmetic, including subtraction giving a TimeSpan;
//   * a small set of format strings, listed in ToString.
//
// Where "now" comes from is the one thing that differs between tiers, so it is
// the one thing behind a hook: the kernel reads the CMOS clock, an app asks the
// kernel. With neither installed, Now is the epoch — as before, and IsRealClock
// says so rather than leaving the caller to guess.

using System.Globalization;

namespace System
{
    public readonly struct DateTime : IEquatable<DateTime>, IComparable<DateTime>
    {
        private const long TicksPerMillisecond = 10000;
        private const long TicksPerSecond = TicksPerMillisecond * 1000;
        private const long TicksPerMinute = TicksPerSecond * 60;
        private const long TicksPerHour = TicksPerMinute * 60;
        private const long TicksPerDay = TicksPerHour * 24;

        private const int DaysPerYear = 365;
        private const int DaysPer4Years = DaysPerYear * 4 + 1;
        private const int DaysPer100Years = DaysPer4Years * 25 - 1;
        private const int DaysPer400Years = DaysPer100Years * 4 + 1;

        private readonly long _ticks;

        public DateTime(long ticks) { _ticks = ticks; }

        public DateTime(int year, int month, int day)
            : this(year, month, day, 0, 0, 0) { }

        public DateTime(int year, int month, int day, int hour, int minute, int second)
        {
            _ticks = DateToTicks(year, month, day)
                   + hour * TicksPerHour + minute * TicksPerMinute + second * TicksPerSecond;
        }

        public long Ticks => _ticks;

        public static DateTime MinValue => new DateTime(0);
        public static DateTime MaxValue => new DateTime(3155378975999999999L);

        public static DateTime Now => Clock.Now();
        public static DateTime UtcNow => Clock.Now();
        public static DateTime Today => Now.Date;

        public DateTime Date => new DateTime(_ticks - _ticks % TicksPerDay);

        public int Year => GetDatePart(DatePart.Year);
        public int Month => GetDatePart(DatePart.Month);
        public int Day => GetDatePart(DatePart.Day);

        public int Hour => (int)((_ticks / TicksPerHour) % 24);
        public int Minute => (int)((_ticks / TicksPerMinute) % 60);
        public int Second => (int)((_ticks / TicksPerSecond) % 60);
        public int Millisecond => (int)((_ticks / TicksPerMillisecond) % 1000);

        public DayOfWeek DayOfWeek => (DayOfWeek)((_ticks / TicksPerDay + 1) % 7);

        public TimeSpan TimeOfDay => TimeSpan.FromTicks(_ticks % TicksPerDay);

        public DateTime AddTicks(long value) => new DateTime(_ticks + value);
        public DateTime AddDays(double value) => AddTicks((long)(value * TicksPerDay));
        public DateTime AddHours(double value) => AddTicks((long)(value * TicksPerHour));
        public DateTime AddMinutes(double value) => AddTicks((long)(value * TicksPerMinute));
        public DateTime AddSeconds(double value) => AddTicks((long)(value * TicksPerSecond));
        public DateTime Add(TimeSpan value) => AddTicks(value.Ticks);

        public static TimeSpan operator -(DateTime a, DateTime b) => TimeSpan.FromTicks(a._ticks - b._ticks);
        public static DateTime operator +(DateTime d, TimeSpan t) => d.AddTicks(t.Ticks);
        public static DateTime operator -(DateTime d, TimeSpan t) => d.AddTicks(-t.Ticks);

        public static bool operator ==(DateTime a, DateTime b) => a._ticks == b._ticks;
        public static bool operator !=(DateTime a, DateTime b) => a._ticks != b._ticks;
        public static bool operator <(DateTime a, DateTime b) => a._ticks < b._ticks;
        public static bool operator >(DateTime a, DateTime b) => a._ticks > b._ticks;
        public static bool operator <=(DateTime a, DateTime b) => a._ticks <= b._ticks;
        public static bool operator >=(DateTime a, DateTime b) => a._ticks >= b._ticks;

        public bool Equals(DateTime other) => _ticks == other._ticks;
        public override bool Equals(object? obj) => obj is DateTime d && d._ticks == _ticks;
        public override int GetHashCode() => (int)_ticks ^ (int)(_ticks >> 32);
        public int CompareTo(DateTime other) => _ticks < other._ticks ? -1 : _ticks > other._ticks ? 1 : 0;

        public static bool IsLeapYear(int year) =>
            (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;

        public static int DaysInMonth(int year, int month)
        {
            if (month == 2) return IsLeapYear(year) ? 29 : 28;
            return month == 4 || month == 6 || month == 9 || month == 11 ? 30 : 31;
        }

        /// <summary>
        /// Formats the date. Only the patterns callers here actually use are
        /// understood — anything else falls back to the round-trip form rather
        /// than silently producing a differently-shaped string.
        /// </summary>
        public string ToString(string? format)
        {
            if (string.IsNullOrEmpty(format)) return ToString();

            switch (format)
            {
                case "d":
                case "MM/dd/yyyy": return Pad(Month, 2) + "/" + Pad(Day, 2) + "/" + Pad(Year, 4);
                case "dd/MM/yyyy": return Pad(Day, 2) + "/" + Pad(Month, 2) + "/" + Pad(Year, 4);
                case "yyyy-MM-dd": return Pad(Year, 4) + "-" + Pad(Month, 2) + "-" + Pad(Day, 2);
                case "t":
                case "HH:mm": return Pad(Hour, 2) + ":" + Pad(Minute, 2);
                case "T":
                case "HH:mm:ss": return Pad(Hour, 2) + ":" + Pad(Minute, 2) + ":" + Pad(Second, 2);
                default: return ToString();
            }
        }

        public string ToString(string? format, IFormatProvider? provider) => ToString(format);

        public override string ToString() =>
            Pad(Year, 4) + "-" + Pad(Month, 2) + "-" + Pad(Day, 2) + " " +
            Pad(Hour, 2) + ":" + Pad(Minute, 2) + ":" + Pad(Second, 2);

        private static string Pad(int value, int width)
        {
            string s = value.ToString();
            while (s.Length < width) s = "0" + s;
            return s;
        }

        private enum DatePart { Year, Month, Day, DayOfYear }

        // Ported from dotnet/runtime's DateTime.GetDatePart: walk the 400/100/4
        // year cycles, then months. The awkward-looking corrections are the leap
        // rules — a 4-year or 400-year block ends with a leap day, and the
        // division has to be clamped rather than allowed to roll over.
        private int GetDatePart(DatePart part)
        {
            int n = (int)(_ticks / TicksPerDay);

            int y400 = n / DaysPer400Years;
            n -= y400 * DaysPer400Years;

            int y100 = n / DaysPer100Years;
            if (y100 == 4) y100 = 3;
            n -= y100 * DaysPer100Years;

            int y4 = n / DaysPer4Years;
            n -= y4 * DaysPer4Years;

            int y1 = n / DaysPerYear;
            if (y1 == 4) y1 = 3;

            if (part == DatePart.Year)
                return y400 * 400 + y100 * 100 + y4 * 4 + y1 + 1;

            n -= y1 * DaysPerYear;
            if (part == DatePart.DayOfYear) return n + 1;

            bool leap = y1 == 3 && (y4 != 24 || y100 == 3);
            int month = 1;
            while (true)
            {
                int daysInMonth = DaysInMonthOfCycle(month, leap);
                if (n < daysInMonth) break;
                n -= daysInMonth;
                month++;
            }

            return part == DatePart.Month ? month : n + 1;
        }

        private static int DaysInMonthOfCycle(int month, bool leap)
        {
            if (month == 2) return leap ? 29 : 28;
            return month == 4 || month == 6 || month == 9 || month == 11 ? 30 : 31;
        }

        private static long DateToTicks(int year, int month, int day)
        {
            if (year < 1 || month < 1 || month > 12 || day < 1)
                throw new ArgumentOutOfRangeException(nameof(year));

            int days = 0;
            int y = year - 1;
            days += y * 365 + y / 4 - y / 100 + y / 400;
            for (int m = 1; m < month; m++) days += DaysInMonth(year, m);
            days += day - 1;

            return days * TicksPerDay;
        }

        /// <summary>
        /// Parses a date written exactly as one of the patterns ToString emits.
        /// Deliberately strict: a lenient parser that guessed at separators
        /// would accept input the formatter can never produce.
        /// </summary>
        public static bool TryParseExact(string? input, string? format, IFormatProvider? provider,
            DateTimeStyles style, out DateTime result)
        {
            result = MinValue;
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(format)) return false;

            int year = 1, month = 1, day = 1, hour = 0, minute = 0, second = 0;
            int at = 0;

            for (int i = 0; i < format!.Length; )
            {
                char token = format[i];
                int run = 0;
                while (i + run < format.Length && format[i + run] == token) run++;

                switch (token)
                {
                    case 'y': if (!Take(input!, ref at, run, out year)) return false; break;
                    case 'M': if (!Take(input!, ref at, run, out month)) return false; break;
                    case 'd': if (!Take(input!, ref at, run, out day)) return false; break;
                    case 'H': if (!Take(input!, ref at, run, out hour)) return false; break;
                    case 'm': if (!Take(input!, ref at, run, out minute)) return false; break;
                    case 's': if (!Take(input!, ref at, run, out second)) return false; break;
                    default:
                        // A literal in the pattern must appear literally.
                        for (int k = 0; k < run; k++)
                        {
                            if (at >= input!.Length || input[at] != token) return false;
                            at++;
                        }
                        break;
                }

                i += run;
            }

            if (month < 1 || month > 12 || day < 1 || day > DaysInMonth(year, month)) return false;
            if (hour > 23 || minute > 59 || second > 59) return false;

            result = new DateTime(year, month, day, hour, minute, second);
            return true;
        }

        public static bool TryParse(string? input, out DateTime result)
            => TryParseExact(input, "yyyy-MM-dd", null, DateTimeStyles.None, out result);

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

        /// <summary>
        /// Where "now" comes from. The kernel installs a reader over the CMOS
        /// clock; an app asks the kernel through the service table.
        /// </summary>
        public static unsafe class Clock
        {
            private static delegate*<long> s_ticks;

            /// <summary>
            /// False when nothing is installed: Now is the epoch, and a caller
            /// that cares can tell rather than trusting a plausible date.
            /// </summary>
            public static bool IsRealClock => s_ticks != null;

            public static void Install(delegate*<long> ticksNow) { s_ticks = ticksNow; }

            internal static DateTime Now() => s_ticks == null ? MinValue : new DateTime(s_ticks());
        }
    }

    public enum DayOfWeek
    {
        Sunday = 0, Monday = 1, Tuesday = 2, Wednesday = 3,
        Thursday = 4, Friday = 5, Saturday = 6,
    }
}

namespace System.Globalization
{
    [Flags]
    public enum DateTimeStyles
    {
        None = 0,
        AllowLeadingWhite = 1,
        AllowTrailingWhite = 2,
        AllowInnerWhite = 4,
        AllowWhiteSpaces = AllowLeadingWhite | AllowTrailingWhite | AllowInnerWhite,
        NoCurrentDateDefault = 8,
        AdjustToUniversal = 16,
        AssumeLocal = 32,
        AssumeUniversal = 64,
        RoundtripKind = 128,
    }

    [Flags]
    public enum TimeSpanStyles
    {
        None = 0,
        AssumeNegative = 1,
    }
}
