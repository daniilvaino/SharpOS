using System;
using OS.Hal;
using OS.Hal.Timer;
using OS.Kernel.Memory;
using OS.Kernel.Threading;
using SharpOS.Std.Exchange;
using SharpOS.Std.Exchange.Probe;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Diagnostics
{
    // The kernel as a region's receiver (pipe_plan.md "Проверить опытом", 1
    // and 2). AotTests builds RegionProbeGraph straight into an exchange block
    // it owns and calls RegionToKernel; this runs inside that call, on the
    // app's thread, with preemption on.
    //
    //   1. take the block: it must be a live block of the calling run, and
    //      becomes the kernel's;
    //   2. print it by the schema alone — no declaration consulted (item 2);
    //   3. refuse a copy with one bad key at its last record, and leave the
    //      copy byte for byte as it was: all or nothing;
    //   4. translate in place with the kernel's own keys and check the graph;
    //   5. read it in a loop while two kernel threads allocate and a third
    //      collects; the region's bytes, the graph, the workers' live objects,
    //      a graph of the kernel's own and the heap walk must all come through
    //      (item 1).
    //
    // Answers the number of failures; negative when the hand-over is refused.
    internal static unsafe class RegionIntakeProbe
    {
        private const int Numbers = RegionProbeGraph.Numbers;

        private const uint LoadMilliseconds = 1000;
        private const int RingSize = 64;
        private const int RingElements = 40;

        private static object[] s_ring0;
        private static object[] s_ring1;
        private static volatile uint s_allocations;
        private static volatile uint s_corruptions;
        private static volatile uint s_collections;
        private static volatile int s_running;
        private static volatile bool s_stop;

        public static int Receive(byte* region, ulong length, byte* schema, ulong schemaLength)
        {
            uint generation = Scheduler.Current?.AppGeneration ?? 0;
            if (region == null || length == 0 || generation == 0)
                return Refuse("no region");
            if (ExchangeHeap.OwnerOf(region) != generation || ExchangeHeap.SizeOf(region) < length)
                return Refuse("not a live exchange block of the calling run");
            if (schema == null || schemaLength == 0 || schemaLength > 64 * 1024)
                return Refuse("no schema");

            ExchangeHeap.SetOwner(region, ExchangeHeap.OwnerKernel);
            Say($"received {length} bytes from generation {generation}; the block is the kernel's now");

            int failed = 0;
            try
            {
                failed += Run(region, length, schema, schemaLength);
            }
            catch (Exception e)
            {
                Say("threw: " + e.Message);
                failed++;
            }

            ExchangeHeap.Free(region);
            Say(failed == 0 ? "PASS" : $"FAIL {failed}");
            return failed;
        }

        private static int Run(byte* region, ulong length, byte* schema, ulong schemaLength)
        {
            int failed = 0;

            // 2. Print by the schema. Nothing here knows the producer's types.
            byte[] schemaBytes = new byte[(int)schemaLength];
            for (int i = 0; i < schemaBytes.Length; i++)
                schemaBytes[i] = schema[i];
            int printed = RegionSchema.Print(region, length, schemaBytes,
                                             line => Console.WriteLine("[region-print] " + line),
                                             out string printComplaint);
            if (printed < 0)
                Say("print refused: " + printComplaint);

            RegionProbeGraph.Declare();
            failed += Expect("kernel declared the graph's types", RegionProbeGraph.Problems == 0);
            if (RegionProbeGraph.Problems != 0)
                return failed;

            int records = CountRecords(region, length, out ulong lastRecord);
            failed += Expect($"printed by the schema alone: {printed} objects, {records} records",
                             printed > 0 && printed == records);

            // 3. All or nothing, on a copy: the last record's key is one this
            // kernel does not know, so every record before it passes the first
            // pass, and none of them may be touched.
            byte* copy = (byte*)ExchangeHeap.Allocate(length, ExchangeHeap.OwnerKernel);
            if (copy != null)
            {
                MemoryPrimitives.Memcpy(copy, region, length);
                *(ulong*)(copy + lastRecord + Region.HeaderSize) = 0x0BADBADBADBADBADUL | 1;
                bool refused = !Region.Resolve(copy, length, out _, out string why);
                bool untouched = true;
                for (ulong i = 0; i < length && untouched; i++)
                {
                    bool inForged = i >= lastRecord + Region.HeaderSize && i < lastRecord + Region.HeaderSize + 8;
                    untouched = inForged || copy[i] == region[i];
                }
                failed += Expect("unknown key at the last record: refused, nothing written (" + why + ")",
                                 refused && untouched);
                ExchangeHeap.Free(copy);
            }

            // The region's root before translation, used as an object by the
            // kernel: each access that reads the table word must throw an
            // exception the kernel catches (item 3, receiver's side).
            failed += Untranslated(region);

            // 4. Translate in place.
            if (!Region.Resolve(region, length, out object root, out string complaint))
            {
                Say("resolve refused: " + complaint);
                return failed + 1;
            }
            failed += Expect("translated in place", true);
            failed += RegionProbeGraph.Check(root, Numbers, line => Console.WriteLine("[region] " + line));

            // 5. Read under load.
            failed += ReadUnderLoad(region, length, root);
            return failed;
        }

        private static int Untranslated(byte* region)
        {
            ulong objectAt = (ulong)region + Region.HeaderSize;
            object untranslated = System.Runtime.CompilerServices.Unsafe.As<ulong, object>(ref objectAt);
            int failed = 0;

            bool caught = false;
            try { VirtualOn(untranslated); }
            catch (AccessViolationException) { caught = true; }
            failed += Expect("untranslated: virtual call throws, caught", caught);

            caught = false;
            try { CastOn(untranslated); }
            catch (AccessViolationException) { caught = true; }
            failed += Expect("untranslated: cast throws, caught", caught);

            caught = false;
            try { InterfaceOn(untranslated); }
            catch (AccessViolationException) { caught = true; }
            failed += Expect("untranslated: interface dispatch throws, caught", caught);
            return failed;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int VirtualOn(object o) => System.Runtime.CompilerServices.Unsafe.As<Base>(o).Rank();

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool CastOn(object o) => o is Base;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int InterfaceOn(object o) => System.Runtime.CompilerServices.Unsafe.As<ILabelled>(o).Label();

        private static int ReadUnderLoad(byte* region, ulong length, object root)
        {
            int failed = 0;
            ulong before = Checksum(region, length);

            // The kernel's own graph, in its own heap, held only by this frame.
            object own = RegionProbeGraph.Build(Numbers);

            failed += Expect("preemption is on", Preemption.IsEnabled);
            if (!Hpet.IsInitialized)
            {
                Say("SKIP load: no HPET");
                return failed + 1;
            }

            s_ring0 = new object[RingSize];
            s_ring1 = new object[RingSize];
            s_allocations = 0;
            s_corruptions = 0;
            s_collections = 0;
            s_stop = false;
            s_running = 0;

            ulong gcBefore = KernelGC.Collections;
            ulong switchesBefore = Preemption.Switches;

            int spawned = 0;
            delegate* unmanaged<void> worker0 = &Worker0;
            delegate* unmanaged<void> worker1 = &Worker1;
            delegate* unmanaged<void> collector = &Collector;
            if (Spawn(worker0)) spawned++;
            if (Spawn(worker1)) spawned++;
            if (Spawn(collector)) spawned++;
            failed += Expect("three load threads started", spawned == 3);

            ulong hz = Hpet.FrequencyHz;
            ulong start = Hpet.ReadCounter();
            ulong until = hz * LoadMilliseconds / 1000;
            uint reads = 0;
            int readFailures = 0;
            while (Hpet.ReadCounter() - start < until)
            {
                readFailures += RegionProbeGraph.Check(root, Numbers, null);
                reads++;
            }

            s_stop = true;
            ulong waitStart = Hpet.ReadCounter();
            while (s_running > 0 && Hpet.ReadCounter() - waitStart < hz)
                Scheduler.Yield();

            ulong collections = KernelGC.Collections - gcBefore;
            ulong switches = Preemption.Switches - switchesBefore;
            Say($"load: {reads} reads, {s_allocations} allocations, {collections} collections, {switches} switches");

            failed += Expect("load threads finished", s_running == 0);
            failed += Expect("the reader, the allocators and the collector all ran",
                             reads > 0 && s_allocations > 0 && s_collections > 0 && collections > 0);
            failed += Expect("the timer switched between them", switches > 0);
            failed += Expect("every read of the region checked out", readFailures == 0);
            failed += Expect("region bytes unchanged by the collections", Checksum(region, length) == before);
            failed += Expect("the workers' live objects intact", s_corruptions == 0 && RingIntact(s_ring0) && RingIntact(s_ring1));
            failed += Expect("the kernel's own graph intact", RegionProbeGraph.Check(own, Numbers, null) == 0);
            failed += Expect("the kernel heap walks end to end", HeapWalks(out uint objects), objects);

            s_ring0 = null;
            s_ring1 = null;
            return failed;
        }

        private static bool Spawn(delegate* unmanaged<void> entry)
        {
            Preemption.Suppress();
            s_running++;
            Preemption.Allow();
            if (Scheduler.Spawn(entry, 16 * 4096) != null)
                return true;
            Preemption.Suppress();
            s_running--;
            Preemption.Allow();
            return false;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void Worker0() => Allocate(s_ring0, 0x11);

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void Worker1() => Allocate(s_ring1, 0x77);

        // Keeps a ring of live arrays, each filled with a pattern from its
        // number, and checks the one it is about to replace: a collection that
        // swept or overwrote a live object shows here.
        private static void Allocate(object[] ring, byte seed)
        {
            uint n = 0;
            while (!s_stop)
            {
                int slot = (int)(n % RingSize);
                if (ring[slot] is byte[] old && !Intact(old))
                    s_corruptions++;

                byte[] block = new byte[RingElements];
                byte mark = (byte)(seed + n);
                block[0] = mark;
                for (int i = 1; i < RingElements; i++) block[i] = (byte)(mark ^ i);
                ring[slot] = block;

                // Garbage as well, so the collector has something to sweep.
                string garbage = new string('g', 8 + (int)(n % 24));
                if (garbage.Length == 0) s_corruptions++;

                n++;
                s_allocations++;
            }
            Finish();
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void Collector()
        {
            while (!s_stop)
            {
                KernelGC.Collect();
                s_collections++;
                Scheduler.Yield();
            }
            Finish();
        }

        private static void Finish()
        {
            Preemption.Suppress();
            s_running--;
            Preemption.Allow();
            Scheduler.Exit();
        }

        private static bool Intact(byte[] block)
        {
            if (block.Length != RingElements) return false;
            byte mark = block[0];
            for (int i = 1; i < RingElements; i++)
                if (block[i] != (byte)(mark ^ i)) return false;
            return true;
        }

        private static bool RingIntact(object[] ring)
        {
            for (int i = 0; i < ring.Length; i++)
                if (ring[i] != null && !(ring[i] is byte[] b && Intact(b))) return false;
            return true;
        }

        // Records by the kernel's tables, and the offset of the last one.
        private static int CountRecords(byte* region, ulong length, out ulong last)
        {
            int count = 0;
            last = 0;
            for (ulong cursor = 0; cursor < length;)
            {
                ulong objectAt = (ulong)region + cursor + Region.HeaderSize;
                if (!TypeKeys.TryTable(*(ulong*)objectAt, out ulong table))
                    return -1;
                last = cursor;
                count++;
                cursor += Region.HeaderSize + Region.PayloadSize(objectAt, table);
            }
            return count;
        }

        private static ulong Checksum(byte* p, ulong length)
        {
            ulong h = 0xCBF29CE484222325UL;
            for (ulong i = 0; i < length; i++)
            {
                h ^= p[i];
                h *= 0x100000001B3UL;
            }
            return h;
        }

        // Segment by segment, object by object, as the sweep walks: every
        // object has a table, a size, and the last one ends at the segment's
        // bump pointer.
        private static bool HeapWalks(out uint objects)
        {
            objects = 0;
            Preemption.Suppress();
            try
            {
                GcSegmentHeader* seg = GcHeap.FirstSegment;
                while (seg != null)
                {
                    nint p = seg->ObjectStart;
                    nint end = seg->Current;
                    while (p < end)
                    {
                        GcObject* o = (GcObject*)p;
                        if (o->MethodTable == null) return false;
                        uint size = o->ComputeSize();
                        if (size < 16) return false;
                        p += (nint)((size + 15u) & ~15u);
                        objects++;
                    }
                    if (p != end) return false;
                    seg = seg->Next;
                }
                return true;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        private static int Refuse(string why)
        {
            Say("refused: " + why);
            return -1;
        }

        private static int Expect(string what, bool ok, uint value = 0)
        {
            Say((ok ? "ok   " : "FAIL ") + what + (value != 0 ? $" ({value})" : ""));
            return ok ? 0 : 1;
        }

        private static void Say(string line) => Console.WriteLine("[region] " + line);
    }
}
