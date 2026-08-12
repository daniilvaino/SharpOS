using OS.Boot.EH;
using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // Sampling profiler on the back of the timer interrupt.
    //
    // Every tick already knows where the CPU was: the interrupt frame carries
    // the RIP of whatever it stopped. Counting those addresses is a profile —
    // 100 samples a second, which over a ten-second startup is a thousand
    // points, plenty to find a loop that is burning time.
    //
    // Exists because "startup is slow and I think something is spinning" is a
    // claim about where the CPU is, and that is measurable rather than
    // arguable. Guessing at it from the log tells you which lines print, not
    // where the seconds go.
    //
    // Addresses are classified rather than named: an address inside the kernel
    // image resolves to an offset that can be looked up in the map afterwards,
    // and everything else is code the JIT produced at runtime, which no static
    // map can name. Even that split answers the first question — whether the
    // time is ours or the runtime's.
    internal static unsafe class Sampler
    {
        // Open-addressed table, power of two. Fixed buffers inside a struct
        // rather than static arrays: a static array initialiser needs a class
        // constructor, and those do not work here (limits doc §1).
        private const int Slots = 8192;
        private const int SlotMask = Slots - 1;

        private struct Table
        {
            public fixed ulong Rip[Slots];
            public fixed uint Hits[Slots];
        }

        // Samples per thread id. The question "which thread is eating the
        // CPU" is not answerable from addresses: two threads run the same
        // JIT-compiled code and land on the same RIP. Under cooperative
        // scheduling a thread that never yields simply keeps the CPU, and the
        // one waiting for input never gets it — this is what shows that.
        private const int ThreadSlots = 32;
        private struct ThreadTable { public fixed uint Samples[ThreadSlots]; }
        private static ThreadTable s_threads;

        private static Table s_table;
        private static bool s_enabled;
        private static ulong s_total;
        private static ulong s_dropped;
        private static ulong s_evicted;
        private static uint s_sinceReport;

        // Ticks between reports. At 100 Hz this is every ten seconds — often
        // enough to watch a startup unfold, rare enough not to drown the log.
        private const uint ReportEvery = 1000;

        public static bool IsEnabled => s_enabled;
        public static ulong Total => s_total;

        public static void Start()
        {
            fixed (Table* t = &s_table)
            {
                for (int i = 0; i < Slots; i++) { t->Rip[i] = 0; t->Hits[i] = 0; }
            }
            fixed (ThreadTable* th = &s_threads)
            {
                for (int i = 0; i < ThreadSlots; i++) th->Samples[i] = 0;
            }
            s_total = 0;
            s_dropped = 0;
            s_evicted = 0;
            s_sinceReport = 0;
            s_enabled = true;
        }

        public static void Stop() => s_enabled = false;

        /// <summary>
        /// Record one sample. Called from the timer interrupt, so it allocates
        /// nothing, takes no locks and never calls back into the runtime.
        /// </summary>
        public static void OnTick(ulong rip)
        {
            if (!s_enabled || rip == 0) return;

            s_total++;

            // Whoever is running right now. Ids above the table are folded
            // into the last slot rather than dropped: losing them would make
            // a busy thread look idle.
            OS.Kernel.Threading.Thread? current = OS.Kernel.Threading.Scheduler.Current;
            if (current != null)
            {
                int id = current.Id;
                if (id < 0) id = 0;
                if (id >= ThreadSlots) id = ThreadSlots - 1;
                fixed (ThreadTable* th = &s_threads) th->Samples[id]++;
            }

            // Mix the address down to a slot. The low four bits are dropped:
            // instructions are not that dense, and the point is to find hot
            // regions rather than exact instructions.
            ulong key = rip;
            int slot = (int)(((key >> 4) ^ (key >> 20)) & SlotMask);

            fixed (Table* t = &s_table)
            {
                int weakest = -1;
                uint weakestHits = uint.MaxValue;

                for (int probe = 0; probe < 16; probe++)
                {
                    int at = (slot + probe) & SlotMask;
                    if (t->Rip[at] == key) { t->Hits[at]++; return; }
                    if (t->Rip[at] == 0) { t->Rip[at] = key; t->Hits[at] = 1; return; }
                    if (t->Hits[at] < weakestHits) { weakestHits = t->Hits[at]; weakest = at; }
                }

                // Full window: evict the coldest entry instead of discarding
                // the sample.
                //
                // Discarding was worse than useless — it made the profiler go
                // blind exactly when it mattered. After a crash the machine
                // span at ONE new address, every sample of it was thrown away
                // because the table had filled during normal running, and the
                // reported top three stayed frozen on the pre-crash picture
                // while `dropped` climbed by a thousand per report. The tool
                // stopped answering at the moment the question appeared.
                if (weakest >= 0 && weakestHits <= 2)
                {
                    t->Rip[weakest] = key;
                    t->Hits[weakest] = 1;
                    s_evicted++;
                    return;
                }
            }

            s_dropped++;
        }

        /// <summary>
        /// Called from the tick after OnTick. Reports periodically, on the
        /// serial port only.
        /// </summary>
        /// <remarks>
        /// Serial and not Console: this runs inside an interrupt, and the
        /// console front-end carries state (cursor, terminal engine) that the
        /// interrupted code may be in the middle of updating. The UART is a
        /// port write with nothing to corrupt — the worst case is a line
        /// interleaved with another, which is cosmetic.
        /// </remarks>
        public static void MaybeReport()
        {
            if (!s_enabled) return;
            if (++s_sinceReport < ReportEvery) return;
            s_sinceReport = 0;
            Report();
        }

        public static void Report()
        {
            // Top three in a single pass, keeping the leaders in locals.
            // A first version rescanned the table once per rank with a
            // "hits must be below the previous winner" threshold — which
            // silently collapses when two addresses tie, because the second
            // scan finds the same entry again.
            ulong rip1 = 0, rip2 = 0, rip3 = 0;
            uint hits1 = 0, hits2 = 0, hits3 = 0;

            fixed (Table* t = &s_table)
            {
                for (int i = 0; i < Slots; i++)
                {
                    if (t->Rip[i] == 0) continue;
                    uint hits = t->Hits[i];
                    ulong rip = t->Rip[i];

                    if (hits > hits1)
                    {
                        hits3 = hits2; rip3 = rip2;
                        hits2 = hits1; rip2 = rip1;
                        hits1 = hits;  rip1 = rip;
                    }
                    else if (hits > hits2)
                    {
                        hits3 = hits2; rip3 = rip2;
                        hits2 = hits;  rip2 = rip;
                    }
                    else if (hits > hits3)
                    {
                        hits3 = hits;  rip3 = rip;
                    }
                }
            }

            Serial.WriteString("[prof] samples=");
            WriteULong(s_total);
            Serial.WriteString(" dropped=");
            WriteULong(s_dropped);
            Serial.WriteString(" evicted=");
            WriteULong(s_evicted);

            // Per-thread first: it answers the coarser question, and a single
            // thread holding almost every sample is the signature of a
            // cooperative scheduler with nobody yielding.
            Serial.WriteString(" threads[");
            fixed (ThreadTable* th = &s_threads)
            {
                bool first = true;
                for (int id = 0; id < ThreadSlots; id++)
                {
                    if (th->Samples[id] == 0) continue;
                    if (!first) Serial.WriteChar(' ');
                    first = false;
                    WriteULong((ulong)id);
                    Serial.WriteChar(':');
                    WriteULong(th->Samples[id]);
                }
            }
            Serial.WriteChar(']');

            if (hits1 != 0) { Serial.WriteString(" | "); WriteULong(hits1); Serial.WriteString("x "); WriteAddress(rip1); }
            if (hits2 != 0) { Serial.WriteString(" | "); WriteULong(hits2); Serial.WriteString("x "); WriteAddress(rip2); }
            if (hits3 != 0) { Serial.WriteString(" | "); WriteULong(hits3); Serial.WriteString("x "); WriteAddress(rip3); }

            Serial.WriteChar((char)10);
        }

        // Kernel-image addresses are printed as an offset from the image base,
        // which is what a map file is keyed by. Anything else is printed raw
        // and marked: it is JIT output, and no static map can name it.
        private static void WriteAddress(ulong rip)
        {
            byte* imageBase = CoffRuntimeFunctionTable.ImageBase;
            if (imageBase != null &&
                CoffMethodGcInfo.TryResolve((byte*)rip, out _))
            {
                Serial.WriteString("krnl+0x");
                WriteHex(rip - (ulong)imageBase);
            }
            else
            {
                Serial.WriteString("jit@0x");
                WriteHex(rip);
            }
        }

        private static void WriteULong(ulong value)
        {
            if (value == 0) { Serial.WriteChar('0'); return; }
            byte* digits = stackalloc byte[20];
            int n = 0;
            while (value != 0) { digits[n++] = (byte)('0' + (int)(value % 10)); value /= 10; }
            while (n > 0) Serial.WriteChar((char)digits[--n]);
        }

        private static void WriteHex(ulong value)
        {
            byte* digits = stackalloc byte[16];
            int n = 0;
            if (value == 0) { Serial.WriteChar('0'); return; }
            while (value != 0)
            {
                int d = (int)(value & 0xF);
                digits[n++] = (byte)(d < 10 ? '0' + d : 'a' + d - 10);
                value >>= 4;
            }
            while (n > 0) Serial.WriteChar((char)digits[--n]);
        }
    }
}
