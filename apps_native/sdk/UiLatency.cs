namespace SharpOS.AppSdk
{
    // Time from a keystroke to the frame that answers it.
    //
    // This exists because every other number we had was the wrong one. The
    // sampling profiler says where busy time goes, but a loop that sleeps
    // waiting for input is not busy, and its blocks are cumulative — they
    // cannot say whether anyone was typing while they were taken. The heap
    // census says what the heap holds, not what a keypress costs. Three
    // explanations for "it gets slower and slower" came out of those numbers
    // and none survived contact with the next measurement.
    //
    // So measure the thing that is actually observed: press a key, count until
    // the screen has caught up.
    //
    // The two ends are the main loop's own boundaries. A key goes out for
    // handling in MainIteration; the loop comes back to ask for the next event
    // only after that handling — redraw included — has finished. The gap
    // between them is the frame, with nothing inferred.
    //
    // `cells` comes along because the two candidate shapes are otherwise
    // indistinguishable in a latency number alone: a redraw that costs more
    // per cell, and a redraw that touches more cells.
    internal static unsafe class UiLatency
    {
        // On by default — this is the measurement, not a probe. A line per 32
        // keys costs nothing and is assembled without allocating, so it does
        // not disturb the heap it is reporting next to.
        public const bool Enabled = true;

        private const uint ReportEvery = 32;

        private static ulong s_startTicks;
        private static bool s_pending;

        private static uint s_keys;
        private static ulong s_sumTicks;
        private static ulong s_maxTicks;
        private static ulong s_cells;
        private static ulong s_cellsThisKey;

        /// <summary>A key has been handed to the interface. Start the clock.</summary>
        public static void KeyDispatched()
        {
            if (!Enabled) return;

            s_startTicks = System.Diagnostics.Stopwatch.ReadCounter();
            s_cellsThisKey = 0;
            s_pending = true;
        }

        /// <summary>
        /// The loop is asking for work again, so the frame for the last key is
        /// on screen. Stop the clock.
        /// </summary>
        public static void FrameDone()
        {
            if (!Enabled || !s_pending) return;
            s_pending = false;

            ulong now = System.Diagnostics.Stopwatch.ReadCounter();
            if (now <= s_startTicks) return;

            ulong elapsed = now - s_startTicks;
            s_sumTicks += elapsed;
            if (elapsed > s_maxTicks) s_maxTicks = elapsed;
            s_cells += s_cellsThisKey;
            s_keys++;

            if (s_keys >= ReportEvery) Report();
        }

        /// <summary>Cells the driver actually emitted, counted per key.</summary>
        public static void CountCells(uint cells)
        {
            if (!Enabled) return;
            s_cellsThisKey += cells;
        }

        private static void Report()
        {
            ulong frequency = (ulong)System.Diagnostics.Stopwatch.Frequency;
            if (frequency == 0 || !AppHost.HasDiagnosticStream)
            {
                Reset();
                return;
            }

            ulong avgUs = (s_sumTicks / s_keys) * 1000000UL / frequency;
            ulong maxUs = s_maxTicks * 1000000UL / frequency;

            byte* line = stackalloc byte[128];
            int n = 0;

            Put(line, ref n, "[uilat] keys=");
            PutULong(line, ref n, s_keys);
            Put(line, ref n, " avg_us=");
            PutULong(line, ref n, avgUs);
            Put(line, ref n, " max_us=");
            PutULong(line, ref n, maxUs);
            Put(line, ref n, " cells_per_key=");
            PutULong(line, ref n, s_cells / s_keys);
            Put(line, ref n, "\n");
            line[n] = 0;

            AppHost.WriteDiagnostic(line);
            Reset();
        }

        private static void Reset()
        {
            s_keys = 0;
            s_sumTicks = 0;
            s_maxTicks = 0;
            s_cells = 0;
        }

        private static void Put(byte* buffer, ref int at, string text)
        {
            for (int i = 0; i < text.Length && at < 126; i++)
                buffer[at++] = (byte)text[i];
        }

        private static void PutULong(byte* buffer, ref int at, ulong value)
        {
            byte* digits = stackalloc byte[20];
            int count = 0;
            do
            {
                digits[count++] = (byte)('0' + (int)(value % 10UL));
                value /= 10UL;
            }
            while (value != 0);

            while (count > 0 && at < 126)
                buffer[at++] = digits[--count];
        }
    }
}
