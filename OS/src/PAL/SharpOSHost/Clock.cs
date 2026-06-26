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
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetUtcFileTime")]
        public static long GetUtcFileTime()
        {
            if (!Rtc.TryRead(out Rtc.Snapshot s))
                return 0;
            long days = DaysSince1970(s.Year, s.Month, s.Day) + 134774;
            long secs = days * 86400L + s.Hour * 3600L + s.Minute * 60L + s.Second;
            // Mix in HPET sub-second offset (100-ns resolution) so callers
            // observing FILETIME at sub-second cadence still see monotonic
            // forward progress. RTC alone is 1 Hz; QPC/Stopwatch consumers
            // (ProcessorIdCache.ProcessorNumberSpeedCheck, SpinWait, timer
            //  scheduling) would otherwise spin until the next RTC tick.
            long fileTime = secs * 10_000_000L;
            ulong hz = OS.Hal.Timer.Hpet.FrequencyHz;
            if (hz != 0)
            {
                ulong c = OS.Hal.Timer.Hpet.ReadCounter();
                // sub-second portion in 100-ns ticks
                ulong subSec = (c % hz) * 10_000_000UL / hz;
                fileTime += (long)subSec;
            }
            return fileTime;
        }

        // Stopwatch routes: System.Native's GetTimestamp asks for monotonic
        // hi-res ticks. HPET is exactly that — fixed-freq monotonic counter.
        [RuntimeExport("SharpOSHost_GetHpetCounter")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetHpetCounter")]
        public static ulong GetHpetCounter() => OS.Hal.Timer.Hpet.ReadCounter();

        [RuntimeExport("SharpOSHost_GetHpetFrequencyHz")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetHpetFrequencyHz")]
        public static ulong GetHpetFrequencyHz() => OS.Hal.Timer.Hpet.FrequencyHz;

        // Fill a Win32 SYSTEMTIME (8 × WORD: Year, Month, DayOfWeek, Day,
        // Hour, Minute, Second, Milliseconds). DayOfWeek: 0=Sunday;
        // 1970-01-01 was a Thursday (=4).
        [RuntimeExport("SharpOSHost_GetSystemTime")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetSystemTime")]
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
