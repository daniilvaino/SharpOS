// System.DateTime — the platform part: where "now" comes from.
//
// Takes the place of upstream's DateTime.Unix.cs / DateTime.Windows.cs
// (dotnet/runtime .NET 10 fork, MIT). Everything else of DateTime is the BCL
// port in DateTime.cs.
//
// "Now" is the one thing that differs between tiers, so it is the one thing
// behind a hook: the kernel installs a reader over its wall clock (the RTC
// carried by the HPET), an app installs one that asks the kernel. With
// neither installed UtcNow is MinValue, and IsRealClock says so rather than
// leaving the caller to guess.
//
// The local time zone is UTC (TimeZoneInfo.SharpOS.cs), so DateTime.Now is
// UtcNow relabelled Local by upstream's own code in DateTime.cs.
//
// Leap seconds: none, as on Unix (SystemSupportsLeapSeconds is false).

namespace System
{
    public readonly partial struct DateTime
    {
        internal static bool SystemSupportsLeapSeconds => false;

        public static DateTime UtcNow
        {
            get
            {
                // SharpOS: upstream reads Interop.Sys.GetSystemTimeAsTicks(); the clock here
                // answers DateTime ticks (since 0001-01-01) directly.
                return new DateTime(((ulong)Clock.UtcTicks()) | KindUtc);
            }
        }

#pragma warning disable IDE0060

        // Never called
        private static DateTime FromFileTimeLeapSecondsAware(ulong fileTime) => default;

        // Never called
        private static ulong ToFileTimeLeapSecondsAware(long ticks) => default;

        // IsValidTimeWithLeapSeconds is not expected to be called at all for now on non-Windows platforms
        internal static bool IsValidTimeWithLeapSeconds(DateTime value) => false;

#pragma warning restore IDE0060

        /// <summary>
        /// Where "now" comes from (SharpOS, not BCL). The kernel installs a
        /// reader over its wall clock; an app installs one that asks the
        /// kernel through the service table.
        /// </summary>
        /// <remarks>
        /// A pointer static and nothing else: an app installs its reader
        /// before its GC statics are materialized, the kernel before Phase 2.
        /// </remarks>
        public static unsafe class Clock
        {
            private static delegate*<long> s_ticks;

            /// <summary>
            /// False when nothing is installed: UtcNow is MinValue, and a
            /// caller that cares can tell rather than trusting a plausible date.
            /// </summary>
            public static bool IsRealClock => s_ticks != null;

            /// <summary>Installs the reader: it answers UTC as DateTime ticks (since 0001-01-01).</summary>
            public static void Install(delegate*<long> ticksNow) { s_ticks = ticksNow; }

            internal static long UtcTicks()
            {
                if (s_ticks == null) return MinTicks;
                long ticks = s_ticks();
                return (ulong)ticks > MaxTicks ? MinTicks : ticks;
            }
        }
    }
}
