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
    // Under QEMU's software CPU the samples lean towards code that touches
    // devices. The timer is emulated under the same lock a device access
    // takes, so the tick tends to arrive right after an MMIO or port access:
    // the HPET read, an xHCI register write, a serial byte. An xHCI wait
    // loop came out at 9 % of a benchmark when the counters said 1.2 %
    // (step170). Check a device-heavy entry against a counter before acting.
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

        // Callers of whatever we interrupted.
        //
        // The address alone says WHERE the CPU is, not WHY. Three fixes in a
        // row went to the wrong place because the hot spot was a four-
        // instruction accessor called from somewhere unknown — Hpet.ReadCounter
        // has a dozen callers and only one of them was spinning.
        //
        // A function that small has no prologue and no frame, so the return
        // address is exactly at the interrupted RSP. That is an assumption
        // about leaves, not a general truth, which is why this is a separate
        // table and not presented as a stack trace.
        private static Table s_callers;

        private static Table s_table;
        private static bool s_enabled;
        private static ulong s_total;
        private static ulong s_dropped;
        private static ulong s_evicted;
        private static uint s_sinceReport;
        private static ulong s_haltsAtLastReport;

        // Ticks between reports. At 100 Hz this is every ten seconds — often
        // enough to watch a startup unfold, rare enough not to drown the log.
        private const uint ReportEvery = 1000;

        public static bool IsEnabled => s_enabled;
        public static ulong Total => s_total;

        public static void Start()
        {
            Clear();
            s_sinceReport = 0;
            s_enabled = true;
        }

        private static void Clear()
        {
            fixed (Table* t = &s_table)
            {
                for (int i = 0; i < Slots; i++) { t->Rip[i] = 0; t->Hits[i] = 0; }
            }
            fixed (ThreadTable* th = &s_threads)
            {
                for (int i = 0; i < ThreadSlots; i++) th->Samples[i] = 0;
            }
            fixed (Table* c = &s_callers)
            {
                for (int i = 0; i < Slots; i++) { c->Rip[i] = 0; c->Hits[i] = 0; }
            }
            s_total = 0;
            s_dropped = 0;
            s_evicted = 0;
        }

        /// <summary>
        /// Starts a fresh profile for one measured interval
        /// (<see cref="PerfCounters.Mark"/>); <see cref="ReportWindow"/> prints it.
        /// </summary>
        /// <remarks>
        /// The periodic report answers "where is a ten-second startup going";
        /// a benchmark needs "where did this run go", with everything before it
        /// out of the table. Sampling pauses while the table is cleared, so a
        /// tick cannot land in a half-cleared slot.
        /// </remarks>
        public static void BeginWindow()
        {
            if (!s_enabled) return;
            s_enabled = false;
            Clear();
            s_enabled = true;
        }

        // A flat profile — the exception path spreads over hundreds of
        // CoreCLR functions — needs a long list: sixteen covered a quarter of
        // the samples (step170).
        private const int WindowTop = 48;

        /// <summary>
        /// Prints the hottest addresses of the interval since
        /// <see cref="BeginWindow"/>, and their callers, as
        /// <c>[prof] scope hot|caller Nx address</c> lines.
        /// </summary>
        /// <remarks>
        /// Called from a thread, not from the tick, so it goes out on the Perf
        /// channel with the rest of the interval's report. Kernel-image
        /// addresses are RVAs of BOOTX64.EFI — CoreCLR included, it is linked
        /// in — for tools/symbolize.ps1.
        /// </remarks>
        public static void ReportWindow(string scope)
        {
            if (!s_enabled) return;

            PerfLine("[perf] " + scope + ".prof.samples=" + SharpOS.Std.NoRuntime.NumberFormatting.ULongToString(s_total) + "\n");
            ReportTop(scope, "hot", ref s_table);
            ReportTop(scope, "caller", ref s_callers);
        }

        // One pass, keeping the leaders in a small sorted array.
        private static void ReportTop(string scope, string kind, ref Table table)
        {
            ulong* rips = stackalloc ulong[WindowTop];
            uint* hits = stackalloc uint[WindowTop];
            int n = 0;

            fixed (Table* t = &table)
            {
                for (int i = 0; i < Slots; i++)
                {
                    uint h = t->Hits[i];
                    ulong rip = t->Rip[i];
                    if (h == 0 || rip == 0) continue;
                    if (n == WindowTop && h <= hits[n - 1]) continue;

                    int at = n < WindowTop ? n++ : WindowTop - 1;
                    while (at > 0 && hits[at - 1] < h)
                    {
                        hits[at] = hits[at - 1];
                        rips[at] = rips[at - 1];
                        at--;
                    }
                    hits[at] = h;
                    rips[at] = rip;
                }
            }

            for (int k = 0; k < n; k++)
            {
                string address = TryKernelRva(rips[k], out ulong rva)
                    ? "krnl+0x" + SharpOS.Std.NoRuntime.NumberFormatting.ULongToHex(rva, 8)
                    : "ext@0x" + SharpOS.Std.NoRuntime.NumberFormatting.ULongToHex(rips[k], 16);
                PerfLine("[prof] " + scope + " " + kind + " "
                    + SharpOS.Std.NoRuntime.NumberFormatting.ULongToString(hits[k]) + "x " + address + "\n");
            }
        }

        private static void PerfLine(string text)
        {
            for (int i = 0; i < text.Length; i++)
                Platform.WriteChar(text[i], OutputChannel.Perf);
        }

        // Whether an address is inside the kernel image — CoreCLR's native
        // code included — and its RVA there. By address range: only the
        // kernel's own managed code has GcInfo, and classifying by that put
        // every CoreCLR function under "JIT output".
        private static bool TryKernelRva(ulong rip, out ulong rva)
        {
            rva = 0;
            byte* image = CoffRuntimeFunctionTable.ImageBase;
            if (image == null) return false;

            int peOffset = *(int*)(image + 0x3C);
            uint sizeOfImage = *(uint*)(image + peOffset + 24 + 0x38);
            if (rip < (ulong)image || rip >= (ulong)image + sizeOfImage) return false;

            rva = rip - (ulong)image;
            return true;
        }

        public static void Stop() => s_enabled = false;

        /// <summary>
        /// Record one sample. Called from the timer interrupt, so it allocates
        /// nothing, takes no locks and never calls back into the runtime.
        /// </summary>
        public static void OnTick(ulong rip) => OnTick(rip, 0);

        public static void OnTick(ulong rip, ulong rsp)
        {
            if (!s_enabled || rip == 0) return;

            if (rsp != 0)
            {
                ulong caller = *(ulong*)rsp;
                if (caller != 0) Record(ref s_callers, caller);
            }

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

            Record(ref s_table, rip);
        }

        private static void Record(ref Table table, ulong key)
        {
            int slot = (int)(((key >> 4) ^ (key >> 20)) & SlotMask);

            fixed (Table* t = &table)
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

            // Say nothing about a window the machine spent asleep. The report
            // was written for a ten-second boot, where every window carried
            // work; sitting at an interactive prompt it filed the same "the CPU
            // did nothing" line every ten seconds and buried the log. Idle is
            // the one state that needs no profile.
            ulong halts = OS.Kernel.Threading.Scheduler.IdleHalts;
            ulong sleptThisWindow = halts - s_haltsAtLastReport;
            s_haltsAtLastReport = halts;
            if (sleptThisWindow >= (ReportEvery - (ReportEvery / 10))) return;

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
            // Activation: did the runtime ask us to interrupt a thread, and
            // did we manage it? Three numbers that separate "never asked",
            // "asked and we missed it" and "delivered".
            // Pool population: live / created / exited. Steady numbers mean
            // the pool found its size; a rising created-and-exited pair with a
            // flat live count means it is oscillating.
            Serial.WriteString(" thr=");
            WriteULong(OS.Kernel.Threading.Scheduler.ThreadsLive);
            Serial.WriteChar('/');
            WriteULong(OS.Kernel.Threading.Scheduler.ThreadsCreated);
            Serial.WriteChar('/');
            WriteULong(OS.Kernel.Threading.Scheduler.ThreadsExited);

            Serial.WriteString(" act=");
            WriteULong(OS.PAL.SharpOSHost.ThreadActivation.Injected);
            Serial.WriteChar('/');
            WriteULong(OS.PAL.SharpOSHost.ThreadActivation.Delivered);
            Serial.WriteChar('/');
            WriteULong(OS.PAL.SharpOSHost.ThreadActivation.NotSafe);

            Serial.WriteString(" yieldsInCrit=");
            WriteULong(OS.Kernel.Threading.Preemption.YieldsWhileSuppressed);
            Serial.WriteString(" critDepth=");
            WriteULong(OS.Kernel.Threading.Preemption.Depth);
            Serial.WriteString(" halt=");
            WriteULong(OS.Kernel.Threading.Scheduler.IdleHalts);
            Serial.WriteString(" busy=");
            WriteULong(OS.Kernel.Threading.Scheduler.IdleBusy);
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

            // Who called it. This is the line that names the loop.
            ulong c1 = 0, c2 = 0; uint ch1 = 0, ch2 = 0;
            fixed (Table* t = &s_callers)
            {
                for (int i = 0; i < Slots; i++)
                {
                    if (t->Rip[i] == 0) continue;
                    uint h = t->Hits[i];
                    if (h > ch1) { ch2 = ch1; c2 = c1; ch1 = h; c1 = t->Rip[i]; }
                    else if (h > ch2) { ch2 = h; c2 = t->Rip[i]; }
                }
            }
            if (ch1 != 0) { Serial.WriteString(" <= "); WriteULong(ch1); Serial.WriteString("x "); WriteAddress(c1); }
            if (ch2 != 0) { Serial.WriteString(" , "); WriteULong(ch2); Serial.WriteString("x "); WriteAddress(c2); }

            Serial.WriteChar((char)10);
        }

        // Kernel-image addresses are printed as an offset from the image base,
        // which is what a map file is keyed by. Anything else is printed raw
        // and marked: JIT output, stubs and applications, which no map of
        // the kernel can name.
        private static void WriteAddress(ulong rip)
        {
            if (TryKernelRva(rip, out ulong rva))
            {
                Serial.WriteString("krnl+0x");
                WriteHex(rva);
            }
            else
            {
                Serial.WriteString("ext@0x");
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
