using System;
using SharpOS.Std.Exchange;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // The smart Expando (step201): an entry found by its hash, in the heap and
    // in a received message; edits of a received message in place. [perf201]
    // lines: nanoseconds per operation. The checks: a lookup costs the same in
    // 4 entries and in 256; edits of entries that are there allocate nothing;
    // 100 stages that add a field each, and 1 000 adds and removes in turn,
    // keep the values right and the block bounded.
    internal static unsafe partial class AppEntry
    {
        private static void SmartExpando()
        {
            RegionShapes shapes = RegionShapes.Parse(MessageCatalog.Schema, out _);
            Lookups(shapes);
            EditsInPlace(shapes);
            Stages(shapes);
            AddsAndRemoves(shapes);
        }

        // A message laid out as a writer sends it, held as a reader receives it.
        private static RawRegion Received(object root, RegionShapes shapes)
        {
            Region.Plan plan = Region.Lay(root, out _);
            byte* block = (byte*)PipeTransport.Allocate(plan.Size);
            Region.Write(plan, block);
            return new RawRegion(block, plan.Size, MessageCatalog.Schema, 0, shapes);
        }

        private static Expando Numbers(int n)
        {
            var bag = new Expando();
            for (int i = 0; i < n; i++) bag["k" + i.ToString()] = i;
            return bag;
        }

        // The fastest of five rounds: a tick or a switch in one round is not
        // the lookup's. In the heap, ContainsKey — the indexer would box the
        // number it returns, and the allocation, not the lookup, would be timed.
        private static void Lookups(RegionShapes shapes)
        {
            int[] sizes = { 4, 16, 64, 256 };
            long[] heap = new long[sizes.Length], view = new long[sizes.Length];
            long sink = 0;
            const int N = 2000, Rounds = 5;
            for (int k = 0; k < sizes.Length; k++)
            {
                Expando bag = Numbers(sizes[k]);
                RawRegion region = Received(bag, shapes);
                View root = region.Root;
                heap[k] = view[k] = long.MaxValue;

                // Four keys spread over the entries, the best of them kept. One
                // key made the result hang on where a single entry happened to
                // land: under QEMU without acceleration some placements cost 3x
                // for the same work (step203, a heap change that only moved
                // objects). A search by name would be slow for every key.
                int[] picks = { 0, sizes[k] / 4, sizes[k] / 2, sizes[k] - 1 };
                for (int p = 0; p < picks.Length; p++)
                {
                    string key = "k" + picks[p].ToString();
                    for (int w = 0; w <= Rounds; w++)
                    {
                        int n = w == 0 ? 20 : N;   // a warm-up round first
                        Timer t = Timer.Start();
                        for (int i = 0; i < n; i++) if (bag.ContainsKey(key)) sink++;
                        long h = t.Stop("find_heap", n, false);
                        t = Timer.Start();
                        for (int i = 0; i < n; i++) sink += (int)root[key];
                        long v = t.Stop("find_view", n, false);
                        if (w > 0 && h < heap[k]) heap[k] = h;
                        if (w > 0 && v < view[k]) view[k] = v;
                    }
                }
                Console.WriteLine("[perf201] find_heap_" + sizes[k].ToString() + "_ns=" + heap[k].ToString()
                                  + " find_view_" + sizes[k].ToString() + "_ns=" + view[k].ToString());
                region.Dispose();
            }

            // Messages of different keys and kinds, one after another: nothing
            // is kept per shape, so a lookup costs what it costs in one.
            const int Mixed = 64;
            var regions = new RawRegion[Mixed];
            for (int m = 0; m < Mixed; m++)
            {
                var bag = new Expando();
                for (int i = 0; i < 8 + m % 17; i++)
                {
                    string name = "f" + ((m * 7 + i * 3) % 50).ToString();
                    if (i % 3 == 0) bag[name] = i;
                    else if (i % 3 == 1) bag[name] = "s" + i.ToString();
                    else bag[name] = i * 0.5;
                }
                bag["id"] = m;
                regions[m] = Received(bag, shapes);
            }
            for (int w = 0; w < 2; w++)
            {
                bool show = w == 1;
                int n = show ? 2000 : 64;
                Timer t = Timer.Start();
                for (int i = 0; i < n; i++)
                {
                    View root = regions[i % Mixed].Root;
                    sink += (int)root["id"];
                }
                t.Stop("find_view_mixed", n, show, "perf201");
            }
            for (int m = 0; m < Mixed; m++) regions[m].Dispose();

            Check("7: a lookup costs the same in 4 entries and in 256: by hash, not by name (heap "
                  + heap[0].ToString() + "/" + heap[3].ToString() + " ns, view " + view[0].ToString() + "/" + view[3].ToString() + " ns)",
                  sink != 0 && heap[3] <= 2 * heap[0] + 100 && view[3] <= 2 * view[0] + 100);
        }

        private static void EditsInPlace(RegionShapes shapes)
        {
            Expando bag = Numbers(8);
            bag["text"] = "some text";
            RawRegion region = Received(bag, shapes);
            View root = region.Root;
            ExpandoName k1 = "k1", k2 = "k2";
            const int N = 2000;
            long sum = 0;
            Timer t = Timer.Start();
            ulong before = SharpOS.Std.NoRuntime.GcHeap.AllocCount;
            for (int i = 0; i < N; i++)
            {
                root["k0"] = i;                       // through the view
                region.Set(root, k1, (long)i);        // a number
                region.Set(root, k2, (i & 1) == 0);   // a bool where a number was
                sum += (int)root["k0"];
            }
            ulong edits = SharpOS.Std.NoRuntime.GcHeap.AllocCount - before;
            t.Stop("edit_in_place", 3 * N, true, "perf201");
            bool right = sum == (long)N * (N - 1) / 2 && (int)root["k0"] == N - 1 && (long)root["k1"] == N - 1
                         && (bool)root["k2"] == false && (string)root["text"] == "some text";

            // Removals, each from a message of its own.
            const int M = 200;
            var regions = new RawRegion[M];
            for (int i = 0; i < M; i++) regions[i] = Received(bag, shapes);
            ExpandoName k7 = "k7";
            int removed = 0;
            t = Timer.Start();
            before = SharpOS.Std.NoRuntime.GcHeap.AllocCount;
            for (int i = 0; i < M; i++)
                if (regions[i].Remove(regions[i].Root, k7)) removed++;
            ulong removals = SharpOS.Std.NoRuntime.GcHeap.AllocCount - before;
            t.Stop("edit_remove", M, true, "perf201");
            for (int i = 0; i < M; i++)
            {
                View r = regions[i].Root;
                if (r.Has("k7") || r.Length != 8 || (int)r["k6"] != 6) right = false;
                regions[i].Dispose();
            }
            region.Dispose();

            Check("7: edits of entries that are there (the view's write, a number, a bool for a number, a removal) allocate nothing: "
                  + edits.ToString() + " and " + removals.ToString() + " allocations",
                  edits == 0 && removals == 0 && removed == M && right);
        }

        private static void Stages(RegionShapes shapes)
        {
            RawRegion region = Received(Numbers(8), shapes);
            ulong start = region.Length;
            var names = new ExpandoName[100];
            for (int i = 0; i < names.Length; i++) names[i] = "stage" + i.ToString();
            var costs = new int[4];
            Timer t = Timer.Start();
            for (int i = 0; i < names.Length; i++)
                costs[(int)region.Set(region.Root, names[i], i)]++;
            long addNs = t.Stop("stage_add", names.Length, true, "perf201");

            // A stage of its own — a process per stage in a pipeline — walks
            // the block whole before its first edit (ViewScope.EnsureValid).
            // Here one region takes all hundred edits, so the walk happens
            // once; its price per stage is measured on the final block (step207).
            const int Walks = 1000;
            Timer w = Timer.Start();
            for (int r = 0; r < Walks; r++) shapes.Validate((byte*)region.Block, region.Length, out _);
            long walkNs = w.Stop("validate_walk", Walks, true, "perf207");
            Console.WriteLine("[perf207] walk_vs_edit: block " + region.Length.ToString() + " bytes, walk "
                              + walkNs.ToString() + " ns, edit " + addNs.ToString() + " ns");

            View root = region.Root;
            bool right = root.Length == 108 && (int)root["k3"] == 3;
            for (int i = 0; i < names.Length && right; i++) right = (int)root[names[i].Name] == i;
            Console.WriteLine("[perf201] stages_100: block " + start.ToString() + " -> " + region.Length.ToString()
                              + " bytes; " + Costs(costs));
            region.Dispose();
            Check("7: 100 stages that add a field each: the 108 fields right; " + Costs(costs),
                  right && costs[(int)EditCost.Grown] > 0 && costs[(int)EditCost.Tail] > 0);
        }

        private static void AddsAndRemoves(RegionShapes shapes)
        {
            RawRegion region = Received(Numbers(8), shapes);
            ulong start = region.Length, longest = start;
            ExpandoName temp = "temp";
            var costs = new int[4];
            const int N = 1000;
            Timer t = Timer.Start();
            for (int i = 0; i < N; i++)
            {
                costs[(int)region.Set(region.Root, temp, i)]++;
                region.Remove(region.Root, temp);
                if (region.Length > longest) longest = region.Length;
            }
            t.Stop("add_remove_pair", N, true, "perf201");

            View root = region.Root;
            bool right = root.Length == 8 && !root.Has("temp");
            for (int i = 0; i < 8 && right; i++) right = (int)root["k" + i.ToString()] == i;
            Console.WriteLine("[perf201] add_remove_1000: block " + start.ToString() + " -> longest " + longest.ToString()
                              + " -> " + region.Length.ToString() + " bytes; " + Costs(costs));
            region.Dispose();
            Check("7: 1 000 adds and removes in turn: the 8 fields right, the block at most 8 times its start ("
                  + start.ToString() + " -> " + longest.ToString() + "); " + Costs(costs),
                  right && longest <= 8 * start && costs[(int)EditCost.Compacted] > 0);
        }

        private static string Costs(int[] costs)
            => "tail " + costs[(int)EditCost.Tail].ToString() + ", grown " + costs[(int)EditCost.Grown].ToString()
               + ", compacted " + costs[(int)EditCost.Compacted].ToString() + ", in place " + costs[(int)EditCost.InPlace].ToString();
    }
}
