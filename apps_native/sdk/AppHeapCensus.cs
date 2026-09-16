using SharpOS.Std.NoRuntime;

namespace SharpOS.AppSdk
{
    // What is filling an application's heap.
    //
    // The app heap is a fixed 64 MiB pool in the image's .bss
    // (GcMemorySource.AppStatic): blocks are handed out by bumping a cursor
    // and never returned. AppGC reclaims inside the segments it already has,
    // but a growing LIVE set needs new ones, and when the pool runs dry the
    // next allocation throws out of memory — at whichever innocent call site
    // asked first.
    //
    // That is what a launcher walked around for a few minutes does: it slows
    // as collections grow frequent and useless, then dies somewhere in a
    // redraw. Naming the type that keeps growing is the difference between
    // that and a real diagnosis.
    //
    // The walk is GcSweep's without the writes. Printing happens outside the
    // allocator — MaybeDump is polled from the idle loop, not called from a
    // hook — so it is free to allocate and take as long as it likes.
    internal static unsafe class AppHeapCensus
    {
        // OFF by default, and the default is not timidity — this instrument
        // changes what it measures. MaybeDump is polled from Terminal.Gui's
        // EventsPending, which is to say from between a keypress and its
        // redraw, and a dump walks the WHOLE heap there. It fires once per
        // new 256 KiB segment — a constant rate — while its cost grows with
        // the heap, from a few thousand objects to nine hundred thousand.
        //
        // Measured: 3926 of 10000 profiler samples inside Walk, against 3225
        // spent halted. Every N-th keypress stalls, N stays put, the stall
        // grows — which is exactly the "it gets slower and slower" this was
        // built to explain, and a fresh launcher is fast again only because
        // its heap is empty.
        //
        // A compile-time const so ILC drops the walk entirely rather than
        // leaving a branch on the input path. Flip to true to take a census,
        // and do not compare timings across the flip.
        public const bool Enabled = false;

        private const int Capacity = 64;

        private struct Table
        {
            public fixed ulong Mt[Capacity];
            public fixed uint Count[Capacity];
            public fixed ulong Bytes[Capacity];
        }

        private static Table s_table;
        private static uint s_dumps;

        /// <summary>
        /// Prints a census if the heap has taken a new segment since the last
        /// call. Cheap when it has not: one flag read.
        /// </summary>
        public static void MaybeDump()
        {
            if (!Enabled) return;

            // Without the diagnostic stream the only way out would paint over
            // the interface this is meant to describe. Stay quiet instead.
            if (!AppHost.HasDiagnosticStream) return;
            if (!GcHeap.TakeSegmentGrewFlag()) return;
            Dump(8);
        }

        public static void Dump(uint top)
        {
            s_dumps++;

            fixed (ulong* mt = s_table.Mt)
            fixed (uint* count = s_table.Count)
            fixed (ulong* bytes = s_table.Bytes)
            {
                Walk(mt, count, bytes, top);
            }
        }

        // No ToString("x") in this std, and a census does not justify one.
        private static string Hex(ulong value)
        {
            const string digits = "0123456789ABCDEF";
            var chars = new char[16];
            for (int i = 15; i >= 0; i--)
            {
                chars[i] = digits[(int)(value & 0xF)];
                value >>= 4;
            }
            return new string(chars, 0, 16);
        }

        private static void Walk(ulong* tableMt, uint* tableCount, ulong* tableBytes, uint top)
        {
            uint used = 0;
            ulong liveBytes = 0;
            ulong liveCount = 0;
            ulong freeBytes = 0;
            ulong freeCount = 0;

            GcMethodTable* freeMt = GcSweep.FreeObjectMt;

            GcSegmentHeader* seg = GcHeap.FirstSegment;
            while (seg != null)
            {
                nint p = seg->ObjectStart;
                nint end = seg->Current;

                while (p < end)
                {
                    GcObject* o = (GcObject*)p;
                    if (o->MethodTable == null) break;

                    uint size = o->ComputeSize();
                    if (size == 0) break;

                    uint aligned = (size + 15u) & ~15u;

                    if (o->MethodTable == freeMt)
                    {
                        freeBytes += aligned;
                        freeCount++;
                    }
                    else
                    {
                        liveBytes += aligned;
                        liveCount++;

                        ulong key = (ulong)(nuint)o->MethodTable;
                        int slot = -1;
                        for (uint i = 0; i < used; i++)
                            if (tableMt[i] == key) { slot = (int)i; break; }

                        if (slot < 0 && used < Capacity)
                        {
                            slot = (int)used;
                            tableMt[used] = key;
                            tableCount[used] = 0;
                            tableBytes[used] = 0;
                            used++;
                        }

                        if (slot >= 0)
                        {
                            tableCount[slot]++;
                            tableBytes[slot] += aligned;
                        }
                    }

                    p += (nint)aligned;
                }

                seg = seg->Next;
            }

            // One composed line per call: the diagnostic service takes a
            // whole string, and building it allocates — which is fine here
            // and would not be inside the allocator.
            AppHost.WriteDiagnostic("[appheap] #" + s_dumps.ToString()
                + " segments=" + GcHeap.SegmentCount.ToString()
                + " live=" + ((uint)liveCount).ToString()
                + " objects, " + ((uint)(liveBytes / 1024)).ToString()
                + " KiB; free=" + ((uint)freeCount).ToString()
                + " blocks, " + ((uint)(freeBytes / 1024)).ToString()
                + " KiB\n");

            for (uint rank = 0; rank < top && rank < used; rank++)
            {
                int best = -1;
                ulong bestBytes = 0;
                for (uint i = 0; i < used; i++)
                {
                    if (tableCount[i] == 0) continue;
                    if (best < 0 || tableBytes[i] > bestBytes)
                    {
                        best = (int)i;
                        bestBytes = tableBytes[i];
                    }
                }
                if (best < 0) break;

                // The MethodTable address is a symbol in the app image — feed
                // it to tools/symbolize.ps1 with that image and its base to
                // get the type name.
                AppHost.WriteDiagnostic("[appheap]   mt=0x" + Hex(tableMt[best])
                    + " count=" + tableCount[best].ToString()
                    + " bytes=" + ((uint)tableBytes[best]).ToString() + "\n");

                tableCount[best] = 0;
            }
        }
    }
}
