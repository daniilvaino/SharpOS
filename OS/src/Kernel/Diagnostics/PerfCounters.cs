using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    /// <summary>
    /// Counters on the kernel paths the hosted runtime leans on, and the
    /// <c>[perf] scope.name=value</c> lines that report them.
    /// </summary>
    /// <remarks>
    /// Measured by the kernel, with the HPET, on purpose. The hosted runtime's
    /// own clocks — Stopwatch, QueryPerformanceCounter, TickCount64 — all end in
    /// the wall clock counted here, which is itself suspect: a program timing
    /// its own loops with it would be measuring with the instrument under
    /// test. These numbers do not depend on it.
    ///
    /// Updated with Interlocked: the runtime calls in from many threads, and
    /// preemption lands between a read and a write. Reported as deltas between
    /// <see cref="Mark"/> and <see cref="Report"/>, so each run is its own
    /// measurement whatever ran before it.
    ///
    /// The counting costs two HPET reads per counted call, and part of that is
    /// inside the measured interval. It is the same on every run, so it does
    /// not hide a change — but an average here is an upper bound, not the
    /// bare cost.
    /// </remarks>
    internal static class PerfCounters
    {
        // SharpOSHost_GetUtcFileTime: every hosted clock reads through it.
        public static long ClockCalls;
        public static long ClockTicks;

        private static long s_clockCallsMark;
        private static long s_clockTicksMark;
        private static ulong s_markCounter;

        public static ulong Now()
            => OS.Hal.Timer.Hpet.IsInitialized ? OS.Hal.Timer.Hpet.ReadCounter() : 0;

        /// <summary>Counts one wall-clock read that started at <paramref name="started"/>.</summary>
        public static void CountClock(ulong started)
        {
            if (started == 0)
                return;

            System.Threading.Interlocked.Increment(ref ClockCalls);
            System.Threading.Interlocked.Add(ref ClockTicks, (long)(Now() - started));
        }

        /// <summary>Starts a measured interval.</summary>
        public static void Mark()
        {
            s_clockCallsMark = System.Threading.Interlocked.Add(ref ClockCalls, 0);
            s_clockTicksMark = System.Threading.Interlocked.Add(ref ClockTicks, 0);
            s_markCounter = Now();
        }

        /// <summary>Reports what happened since <see cref="Mark"/>, under <paramref name="scope"/>.</summary>
        public static void Report(string scope)
        {
            ulong elapsed = s_markCounter == 0 ? 0 : Now() - s_markCounter;
            ulong calls = (ulong)(System.Threading.Interlocked.Add(ref ClockCalls, 0) - s_clockCallsMark);
            ulong ticks = (ulong)(System.Threading.Interlocked.Add(ref ClockTicks, 0) - s_clockTicksMark);

            Line(scope, "wall_ms", TicksToNs(elapsed) / 1_000_000);
            Line(scope, "clock.calls", calls);
            Line(scope, "clock.total_ms", TicksToNs(ticks) / 1_000_000);
            Line(scope, "clock.avg_ns", calls == 0 ? 0 : TicksToNs(ticks) / calls);
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
