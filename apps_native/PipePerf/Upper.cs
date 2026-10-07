using System;
using SharpOS.Std.Exchange;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // The upper paths over a message (step196 §4): a view's field, Into<T>,
    // ToExpando, a field through `dynamic` (view, Expando, class), and what a
    // connection costs — the writer's declaration, the reader's parse, the
    // description's size. Each line: nanoseconds and allocations per operation.
    internal static unsafe partial class AppEntry
    {
        private static void UpperPaths()
        {
            const int N = 2000;
            Small small = new Small { A = 1, B = 2, S = "small" };
            Expando bag = new Expando();
            for (int i = 0; i < 8; i++)
            {
                if ((i & 1) == 0) bag["k" + i.ToString()] = i;
                else bag["k" + i.ToString()] = "v" + i.ToString();
            }

            RegionShapes shapes = RegionShapes.Parse(MessageCatalog.Schema, out _);
            ViewScope smallScope = Laid(small, shapes, out byte* smallBlock);
            ViewScope bagScope = Laid(bag, shapes, out byte* bagBlock);
            View view = smallScope.Root;
            View bagView = bagScope.Root;

            // ToHeap of the same message, for Into<T>'s bound.
            Region.Plan plan = Region.Lay(small, out _);
            byte* work = (byte*)PipeTransport.Allocate(plan.Size);
            Region.Write(plan, work);
            new RegionReader().Resolve(work, plan.Size, out _);

            int sink = 0;
            object keep = null;
            Small target = null;
            Timer t;
            for (int w = 0; w < 2; w++)
            {
                int n = w == 0 ? 20 : N;   // a warm-up round, then the measured one
                bool show = w == 1;
                t = Timer.Start(); for (int i = 0; i < n; i++) sink += (int)view["A"]; long field = t.Stop("view_field", n, show);
                t = Timer.Start(); for (int i = 0; i < n; i++) sink += (int)bagView["k4"]; t.Stop("view_field_expando", n, show);
                t = Timer.Start(); for (int i = 0; i < n; i++) keep = Region.ToHeap(work, plan.Size); long toHeap = t.Stop("to_heap", n, show);
                t = Timer.Start(); for (int i = 0; i < n; i++) target = view.Into<Small>(); long into = t.Stop("into", n, show);
                t = Timer.Start(); for (int i = 0; i < n; i++) keep = view.ToExpando(); long toExpando = t.Stop("to_expando", n, show);
                t = Timer.Start();
                for (int i = 0; i < n; i++)
                {
                    var e = new Expando();
                    e["A"] = (int)view["A"];
                    e["B"] = (int)view["B"];
                    e["S"] = (string)view["S"];
                    keep = e;
                }
                long manual = t.Stop("expando_by_hand", n, show);
                t = Timer.Start(); for (int i = 0; i < n; i++) keep = bagView.ToExpando(); t.Stop("to_expando_bag", n, show);

                dynamic dv = view;
                dynamic de = bag;
                dynamic dc = small;
                t = Timer.Start(); for (int i = 0; i < n; i++) sink += (int)dv.A; t.Stop("dynamic_view", n, show);
                t = Timer.Start(); for (int i = 0; i < n; i++) sink += (int)de.k4; t.Stop("dynamic_expando", n, show);
                t = Timer.Start(); for (int i = 0; i < n; i++) sink += (int)dc.A; t.Stop("dynamic_class", n, show);
                t = Timer.Start(); for (int i = 0; i < n; i++) keep = dc.A == 1; t.Stop("dynamic_class_bool", n, show);
                if (show)
                {
                    Console.WriteLine("[perf196] into_vs_to_heap=" + Ratio(into, toHeap) + " to_expando_vs_by_hand=" + Ratio(toExpando, manual));
                    Check("upper paths measured", sink != 0 && keep != null && target != null && target.S == "small" && field > 0);
                }
            }

            // A connection: a writer declares, a reader takes the description
            // and indexes it.
            byte[] declared = MessageCatalog.Schema;
            for (int w = 0; w < 2; w++)
            {
                int n = w == 0 ? 5 : 200;
                t = Timer.Start();
                for (int i = 0; i < n; i++)
                {
                    Pipe.Create<Small>(4, PipeOverflow.DropOldest, out PipeWriter<Small> pw, out PipeReader<Small> pr);
                    pw.Dispose();
                    pr.Dispose();
                }
                t.Stop("connect_declare", n, w == 1);
                t = Timer.Start(); for (int i = 0; i < n; i++) keep = RegionShapes.Parse(declared, out _); t.Stop("connect_parse", n, w == 1);
            }
            Console.WriteLine("[perf196] connect.catalog_bytes=" + declared.Length.ToString());

            // What a writer of Small declares now, and its first parse (a
            // reader that has not seen it): once, before anything caches it.
            byte[] own = MessageCatalog.WriterSchema(MessageCatalog.KeyOf(typeof(Small)));
            t = Timer.Start();
            keep = RegionShapes.Parse(own, out _);
            t.Stop("connect_first_parse_small", 1, true);
            Console.WriteLine("[perf196] connect.description_bytes_small=" + own.Length.ToString());
            PipeTransport.Free(work);
            PipeTransport.Free(smallBlock);
            PipeTransport.Free(bagBlock);
        }

        private static ViewScope Laid(object root, RegionShapes shapes, out byte* block)
        {
            Region.Plan plan = Region.Lay(root, out _);
            block = (byte*)PipeTransport.Allocate(plan.Size);
            Region.Write(plan, block);
            return new ViewScope(block, plan.Size, shapes);
        }

        private static string Ratio(long a, long b)
            => b == 0 ? "?" : (a / b).ToString() + "." + ((a * 100 / b) % 100).ToString("00");

        private struct Timer
        {
            private ulong _allocs;
            private System.Diagnostics.Stopwatch _clock;

            public static Timer Start()
                => new Timer { _allocs = SharpOS.Std.NoRuntime.GcHeap.AllocCount, _clock = System.Diagnostics.Stopwatch.StartNew() };

            // Nanoseconds per operation; the line is printed when asked.
            public long Stop(string name, int n, bool show)
            {
                long ticks = _clock.ElapsedTicks;
                ulong allocs = SharpOS.Std.NoRuntime.GcHeap.AllocCount - _allocs - 1;   // less the clock
                long ns = ticks * 1000000000 / System.Diagnostics.Stopwatch.Frequency / n;
                if (show) Console.WriteLine("[perf196] " + name + "_ns=" + ns.ToString() + " allocs=" + Per(allocs, n));
                return ns;
            }
        }
    }
}
