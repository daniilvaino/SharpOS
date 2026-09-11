using System.Runtime;
using System.Runtime.InteropServices;
using OS.Hal;

namespace OS.PAL.SharpOSHost
{
    // step 73 quick-win — bridge CoreCLR's wall clock to the kernel CMOS
    // RTC. Stock .NET's GetSystemTimeAsFileTime stub wrote 0, so every
    // DateTime.UtcNow read as 1601-01-01. The kernel already reads CMOS
    // (Hal.Rtc). Bare metal has no timezone DB — CMOS time is treated as
    // UTC (best effort). RTC read failure → 0 / zero-fill (caller keeps
    // the old 1601 behavior; no fault).
    internal static unsafe class SharpOSHostClock
    {
        // days_from_civil (Howard Hinnant) → days since 1970-01-01.
        private static long DaysSince1970(int y, int m, int d)
        {
            y -= (m <= 2) ? 1 : 0;
            int era = (y >= 0 ? y : y - 399) / 400;
            int yoe = y - era * 400;                                   // [0,399]
            int doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;  // [0,365]
            int doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;           // [0,146096]
            return (long)era * 146097 + doe - 719468;
        }

        // Windows FILETIME = 100-ns ticks since 1601-01-01 UTC.
        // 134774 = days between 1601-01-01 and 1970-01-01.
        [RuntimeExport("SharpOSHost_GetUtcFileTime")]
        public static long GetUtcFileTime()
        {
            // Every hosted clock — QPC, TickCount64, DateTime.UtcNow — ends
            // here, so this is where the cost of "what time is it" is counted.
            ulong started = OS.Kernel.Diagnostics.PerfCounters.Now();
            long fileTime = ReadUtcFileTime();
            OS.Kernel.Diagnostics.PerfCounters.CountClock(started);
            return fileTime;
        }

        // The wall clock is the RTC read once, carried forward by the HPET.
        //
        // It used to read the CMOS on every call and add the HPET's position
        // within its own second (counter % hz). Two faults in one line: a full
        // CMOS read — dozens of port accesses and a wait on the update flag —
        // is ~10 us under QEMU, and every hosted clock pays it (the census
        // spent 1.8 s of 16 in here); and the two halves are unrelated, the
        // RTC's second boundary falls anywhere in the HPET's, so time jumped
        // back by up to a second each time the RTC ticked. A benchmark timed
        // with Stopwatch came out at -442 ms for a 574 ms run.
        //
        // Now: one anchor (RTC seconds + the counter at that moment), and
        // every read is anchor + elapsed counter ticks. The absolute error is
        // what it always was — under a second, the RTC has no finer grain —
        // but it no longer changes between reads.
        private static long s_anchorFileTime;
        private static ulong s_anchorCounter;
        private static bool s_anchored;

        // Largest value ever handed out; nothing below it is returned again.
        private static long s_lastFileTime;

        private static long ReadUtcFileTime()
        {
            ulong hz = OS.Hal.Timer.Hpet.FrequencyHz;
            if (hz == 0)
                return ReadRtcFileTime();

            // One CPU: with preemption held, the anchor pair and the last
            // value cannot be half-updated under a reader.
            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                ulong now = OS.Hal.Timer.Hpet.ReadCounter();

                // Re-anchor when the counter is behind the anchor: the one
                // restart EnsureRunning may do after ExitBootServices zeroes
                // it, or a 32-bit counter wrapping. A 32-bit counter that wraps
                // more than once between two reads is not caught — time then
                // lags, but the clamp below keeps it from going back.
                if (!s_anchored || now < s_anchorCounter)
                {
                    long rtc = ReadRtcFileTime();
                    s_anchorFileTime = rtc > s_lastFileTime ? rtc : s_lastFileTime;
                    s_anchorCounter = now;
                    s_anchored = true;
                }

                ulong elapsed = now - s_anchorCounter;
                long fileTime = s_anchorFileTime
                    + (long)(elapsed / hz * 10_000_000UL + elapsed % hz * 10_000_000UL / hz);

                if (fileTime < s_lastFileTime)
                    fileTime = s_lastFileTime;
                s_lastFileTime = fileTime;
                return fileTime;
            }
            finally
            {
                OS.Kernel.Threading.Preemption.Allow();
            }
        }

        // Whole seconds from the CMOS; 0 if it cannot be read.
        private static long ReadRtcFileTime()
        {
            if (!Rtc.TryRead(out Rtc.Snapshot s))
                return 0;
            long days = DaysSince1970(s.Year, s.Month, s.Day) + 134774;
            long secs = days * 86400L + s.Hour * 3600L + s.Minute * 60L + s.Second;
            return secs * 10_000_000L;
        }

        // Stopwatch routes: System.Native's GetTimestamp asks for monotonic
        // hi-res ticks. HPET is exactly that — fixed-freq monotonic counter.
        [RuntimeExport("SharpOSHost_GetHpetCounter")]
        public static ulong GetHpetCounter() => OS.Hal.Timer.Hpet.ReadCounter();

        [RuntimeExport("SharpOSHost_GetHpetFrequencyHz")]
        public static ulong GetHpetFrequencyHz() => OS.Hal.Timer.Hpet.FrequencyHz;

        // Fill a Win32 SYSTEMTIME (8 × WORD: Year, Month, DayOfWeek, Day,
        // Hour, Minute, Second, Milliseconds). DayOfWeek: 0=Sunday;
        // 1970-01-01 was a Thursday (=4).
        [RuntimeExport("SharpOSHost_GetSystemTime")]
        public static void GetSystemTime(ushort* outSt)
        {
            if (outSt == null) return;
            for (int i = 0; i < 8; i++) outSt[i] = 0;
            if (!Rtc.TryRead(out Rtc.Snapshot s)) return;
            long dow = (DaysSince1970(s.Year, s.Month, s.Day) + 4) % 7;
            if (dow < 0) dow += 7;
            outSt[0] = s.Year;
            outSt[1] = s.Month;
            outSt[2] = (ushort)dow;
            outSt[3] = s.Day;
            outSt[4] = s.Hour;
            outSt[5] = s.Minute;
            outSt[6] = s.Second;
            outSt[7] = 0;            // milliseconds — CMOS has no sub-second
        }
    }
}
