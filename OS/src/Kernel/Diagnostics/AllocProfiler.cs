using OS.Boot.EH;

namespace OS.Kernel.Diagnostics
{
    // Who allocates, sampled by allocation rather than by time.
    //
    // The heap census answered "what is in the heap" — 1.45 million byte[] of
    // about 34 bytes each — and that was as far as reading the source got:
    // there is no `new byte[]` on any hot path in the kernel or the PAL, so
    // they are born somewhere a grep does not reach.
    //
    // The timer sampler cannot answer it either. These allocations are cheap;
    // the machine was idle in `StiHlt` while making them, so a profile by CPU
    // time shows the waiting, not the allocating. The trigger has to be the
    // allocation itself.
    //
    // The frames are collected conservatively: walk up the stack, keep every
    // value that lands inside the kernel image. That yields the real callers
    // mixed with anything pointer-shaped that happens to be lying there, and
    // with the allocator's own frames — which appear in EVERY sample and are
    // therefore easy to recognise and ignore. An exact unwind would be nicer
    // and is not worth it for a question of this shape: the answer is whichever
    // address is near the top of the list and is not part of the allocator.
    internal static unsafe class AllocProfiler
    {
        private const int Slots = 128;

        // Frames kept per sample. Deep enough to get past the allocator's own
        // three or four frames and reach the code that asked for the memory.
        private const int FramesPerSample = 10;

        // How far up the stack to look before giving up. 4 KiB — a caller
        // further away than that is not the one that allocated.
        private const int ScanQwords = 512;

        private struct Table
        {
            public fixed ulong Rva[Slots];
            public fixed uint Count[Slots];
        }

        private static Table s_table;
        private static bool s_running;
        private static ulong s_samples;
        private static ulong s_dropped;

        /// <summary>
        /// One sampled allocation. Called from inside the allocator, so it
        /// allocates nothing and takes no locks.
        /// </summary>
        public static void OnAllocation(uint size)
        {
            if (s_running) return;
            s_running = true;
            s_samples++;

            ulong anchor = 0;
            ulong* sp = (ulong*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref anchor);

            // Bounded by the running thread's own stack when it has one. The
            // boot thread does not (it is the firmware's), and there the fixed
            // window is the only bound available.
            ulong* limit = sp + ScanQwords;
            OS.Kernel.Threading.Thread? current = OS.Kernel.Threading.Scheduler.Current;
            if (current != null && current.StackTop != null)
            {
                ulong* top = (ulong*)current.StackTop;
                if (top < limit) limit = top;
            }

            fixed (ulong* rvas = s_table.Rva)
            fixed (uint* counts = s_table.Count)
            {
                int kept = 0;
                for (ulong* p = sp; p < limit && kept < FramesPerSample; p++)
                {
                    if (!Sampler.TryKernelRva(*p, out ulong rva)) continue;
                    Record(rvas, counts, rva);
                    kept++;
                }
            }

            s_running = false;
        }

        private static void Record(ulong* rvas, uint* counts, ulong rva)
        {
            for (int i = 0; i < Slots; i++)
            {
                if (counts[i] == 0)
                {
                    rvas[i] = rva;
                    counts[i] = 1;
                    return;
                }
                if (rvas[i] == rva)
                {
                    counts[i]++;
                    return;
                }
            }
            s_dropped++;
        }

        /// <summary>
        /// The addresses seen most often above a sampled allocation, as
        /// image-relative offsets for tools/symbolize.ps1.
        /// </summary>
        public static void Report(uint top)
        {
            if (s_samples == 0) return;

            OS.Hal.Log.Begin(OS.Hal.LogLevel.Info);
            OS.Hal.Console.Write("[allocprof] samples=");
            OS.Hal.Console.WriteUInt((uint)s_samples);
            if (s_dropped != 0)
            {
                OS.Hal.Console.Write(" dropped=");
                OS.Hal.Console.WriteUInt((uint)s_dropped);
            }
            OS.Hal.Log.EndLine();

            fixed (ulong* rvas = s_table.Rva)
            fixed (uint* counts = s_table.Count)
            {
                for (uint rank = 0; rank < top; rank++)
                {
                    int best = -1;
                    uint bestCount = 0;
                    for (int i = 0; i < Slots; i++)
                    {
                        if (counts[i] == 0) continue;
                        if (best < 0 || counts[i] > bestCount)
                        {
                            best = i;
                            bestCount = counts[i];
                        }
                    }
                    if (best < 0) break;

                    OS.Hal.Log.Begin(OS.Hal.LogLevel.Info);
                    OS.Hal.Console.Write("[allocprof]   rva=0x");
                    OS.Hal.Console.WriteHex(rvas[best]);
                    OS.Hal.Console.Write(" seen=");
                    OS.Hal.Console.WriteUInt(counts[best]);
                    OS.Hal.Log.EndLine();

                    counts[best] = 0;      // reported; keep the slot's rva
                }

                // Every report is a delta: counters reset, so the next one
                // describes the interval since this one rather than the whole
                // run. That is what makes a change of behaviour visible —
                // a profile since boot flattens it.
                for (int i = 0; i < Slots; i++) counts[i] = 0;
            }

            s_samples = 0;
            s_dropped = 0;
        }
    }
}
