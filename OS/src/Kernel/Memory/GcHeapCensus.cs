using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Memory
{
    // Who is filling the managed kernel heap.
    //
    // The heap grows and never collects under normal operation — collection
    // only fires when an allocation fails, and allocations do not fail while
    // KernelHeap still has pages to give. A machine left at a prompt therefore
    // grows until memory runs out: 502 growths of 65 pages, ~133 MiB, before
    // it stopped.
    //
    // Turning collection on by a budget would hide that symptom, and hiding it
    // is the wrong move while nobody knows whether the heap holds garbage or a
    // leak. Garbage a collector reclaims; a leak it does not, and the machine
    // would still die, just later. This says which, by naming what is IN the
    // heap rather than how much.
    //
    // The walk is GcSweep's, without the writes: segments in order, object by
    // object, size from the MethodTable. Allocates nothing — it has to be
    // callable from inside the allocator, and it is counting allocations.
    internal static unsafe class GcHeapCensus
    {
        // Distinct types tracked. Past this the tail is lumped into "other":
        // the question is which few types dominate, and a kernel heap that
        // holds more than this many kinds of object has already answered it.
        private const int Capacity = 96;

        private struct Table
        {
            public fixed ulong Mt[Capacity];
            public fixed uint Count[Capacity];
            public fixed ulong Bytes[Capacity];
        }

        // Static storage, not an array: no allocation, and no class
        // constructor to run (limits §1).
        private static Table s_table;
        private static bool s_running;

        /// <summary>
        /// Prints the types holding the most bytes. MethodTable addresses are
        /// image-relative symbols — feed them to tools/symbolize.ps1 to get
        /// names, the same way a crash address is resolved.
        /// </summary>
        public static void Dump(uint top)
        {
            // Re-entrancy: the print path allocates in some configurations,
            // which would call back in here through the allocator.
            if (s_running) return;
            s_running = true;

            // Pointers taken once: a fixed-size buffer in a static field
            // cannot be indexed directly, the same reason BootLog's early
            // buffer is reached through `fixed`.
            fixed (ulong* tableMt = s_table.Mt)
            fixed (uint* tableCount = s_table.Count)
            fixed (ulong* tableBytes = s_table.Bytes)
            {
                Walk(tableMt, tableCount, tableBytes, top);
            }

            s_running = false;
        }

        private static void Walk(ulong* tableMt, uint* tableCount, ulong* tableBytes, uint top)
        {
            uint used = 0;
            ulong liveBytes = 0;
            ulong liveCount = 0;
            ulong freeBytes = 0;
            ulong freeCount = 0;
            ulong overflowBytes = 0;
            ulong overflowCount = 0;

            GcMethodTable* freeMt = GcSweep.FreeObjectMt;

            GcSegmentHeader* seg = GcHeap.FirstSegment;
            while (seg != null)
            {
                nint p = seg->ObjectStart;
                nint end = seg->Current;

                while (p < end)
                {
                    GcObject* o = (GcObject*)p;
                    if (o->MethodTable == null) break;      // corrupt — stop here

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

                        ulong mt = (ulong)(nuint)o->MethodTable;
                        int slot = -1;
                        for (uint i = 0; i < used; i++)
                        {
                            if (tableMt[i] == mt) { slot = (int)i; break; }
                        }

                        if (slot < 0 && used < Capacity)
                        {
                            slot = (int)used;
                            tableMt[used] = mt;
                            tableCount[used] = 0;
                            tableBytes[used] = 0;
                            used++;
                        }

                        if (slot >= 0)
                        {
                            tableCount[slot]++;
                            tableBytes[slot] += aligned;
                        }
                        else
                        {
                            overflowBytes += aligned;
                            overflowCount++;
                        }
                    }

                    p += (nint)aligned;
                }

                seg = seg->Next;
            }

            // Wall clock on every census, because the failure may not be
            // exhaustion at all. A laptop with 7 GiB free stopped after only
            // 133 MiB of growth — so what ends the run is more likely
            // something that walks the heap and gets slower as it fills. An
            // interval that stretches census by census says exactly that; a
            // steady one says the growth is harmless until memory runs out.
            ulong nowMs = 0;
            ulong hz = OS.Hal.Timer.Hpet.FrequencyHz;
            if (hz != 0) nowMs = OS.Hal.Timer.Hpet.ReadCounter() / (hz / 1000);

            OS.Hal.Log.Begin(OS.Hal.LogLevel.Info);
            OS.Hal.Console.Write("[heapcensus] t=");
            OS.Hal.Console.WriteUInt((uint)nowMs);
            OS.Hal.Console.Write("ms live=");
            OS.Hal.Console.WriteUInt((uint)liveCount);
            OS.Hal.Console.Write(" objects, ");
            OS.Hal.Console.WriteUInt((uint)(liveBytes / 1024));
            OS.Hal.Console.Write(" KiB; free=");
            OS.Hal.Console.WriteUInt((uint)freeCount);
            OS.Hal.Console.Write(" blocks, ");
            OS.Hal.Console.WriteUInt((uint)(freeBytes / 1024));
            OS.Hal.Console.Write(" KiB; types=");
            OS.Hal.Console.WriteUInt(used);
            OS.Hal.Log.EndLine();

            // Selection sort by bytes, top entries only: no allocation, and
            // the list is short enough that the cost is beneath notice next to
            // the walk that produced it.
            for (uint rank = 0; rank < top && rank < used; rank++)
            {
                int best = -1;
                ulong bestBytes = 0;
                for (uint i = 0; i < used; i++)
                {
                    if (tableCount[i] == 0) continue;     // already reported
                    if (best < 0 || tableBytes[i] > bestBytes)
                    {
                        best = (int)i;
                        bestBytes = tableBytes[i];
                    }
                }
                if (best < 0) break;

                OS.Hal.Log.Begin(OS.Hal.LogLevel.Info);
                OS.Hal.Console.Write("[heapcensus]   mt=0x");
                OS.Hal.Console.WriteHex(tableMt[best]);
                OS.Hal.Console.Write(" count=");
                OS.Hal.Console.WriteUInt(tableCount[best]);
                OS.Hal.Console.Write(" bytes=");
                OS.Hal.Console.WriteUInt((uint)tableBytes[best]);
                OS.Hal.Log.EndLine();

                tableCount[best] = 0;                     // mark as reported
            }

            if (overflowCount != 0)
            {
                OS.Hal.Log.Begin(OS.Hal.LogLevel.Info);
                OS.Hal.Console.Write("[heapcensus]   other types: count=");
                OS.Hal.Console.WriteUInt((uint)overflowCount);
                OS.Hal.Console.Write(" bytes=");
                OS.Hal.Console.WriteUInt((uint)overflowBytes);
                OS.Hal.Log.EndLine();
            }

            // What is in the heap, then who put it there — the two halves of
            // the same question, printed together so they describe the same
            // interval.
            OS.Kernel.Diagnostics.AllocProfiler.Report(10);
        }
    }
}
