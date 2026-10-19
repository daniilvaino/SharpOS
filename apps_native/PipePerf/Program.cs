using System;
using System.IO;
using System.Runtime;
using SharpOS.AppSdk;
using SharpOS.Std.Exchange;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    [Message]
    public sealed class Small
    {
        public int A;
        public int B;
        public string S;
    }

    [Message]
    public sealed class Item
    {
        public int X;
        public int Y;
    }

    [Message]
    public sealed class Holder
    {
        public int Id;
        public Item[] Items;
    }

    // The large message (step208): BigGraph.cs, shared with Bench.
    [Message]
    public sealed class BigBatch
    {
        public SharpOS.Micro.BigRecord[] Items;
    }

    // PIPEPERF.EXE — where a message's time goes (step195): each phase of the
    // lower layer apart, the whole path, and the same graphs by hand through
    // BinaryWriter and BinaryReader. Three graphs: an object with two numbers
    // and a string; an object with an array of 100 nested objects; an Expando
    // of 8 entries. Exit code: checks passed.
    internal static unsafe partial class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run(AppHost.Arguments);
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run(AppHost.Arguments);

        private static ulong Us(ulong ticks) => ticks * 1_000_000 / (ulong)System.Diagnostics.Stopwatch.Frequency;

        private static int s_passed;
        private static int s_failed;

        private static void Check(string name, bool ok)
        {
            if (ok) s_passed++; else s_failed++;
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
        }

        private static int Run(string[] args)
        {
            TaskBackendInstaller.Install();
            // --micro (step208): the pieces of a BinaryWriter round trip, the
            // same file Bench runs on CoreCLR. Exit 0.
            if (args.Length > 0 && args[0] == "--micro")
            {
                SharpOS.Micro.MicroBench.CounterNames = new[]
                {
                    "gcs", "allocs", "refills", "reused", "probes", "mark_us", "sweep_us", "commits", "commit_us", "releases", "release_us",
                };
                SharpOS.Micro.MicroBench.Counters = () => new ulong[]
                {
                    AppGC.Collections, SharpOS.Std.NoRuntime.GcHeap.AllocCount, SharpOS.Std.NoRuntime.GcHeap.ContextRefills,
                    SharpOS.Std.NoRuntime.GcHeap.FreelistReuseCount, SharpOS.Std.NoRuntime.GcHeap.FreelistProbeCount,
                    Us(AppGC.MarkTicks), Us(AppGC.SweepTicks),
                    SharpOS.Std.NoRuntime.GcMemorySource.Commits, Us(SharpOS.Std.NoRuntime.GcMemorySource.CommitTicks),
                    SharpOS.Std.NoRuntime.GcMemorySource.Releases, Us(SharpOS.Std.NoRuntime.GcMemorySource.ReleaseTicks),
                };
                SharpOS.Micro.MicroBench.Run();
                return 0;
            }
            MessageCatalog.Ensure();
            // --profile PHASE (step208): one phase of the large message, over
            // and over for a few seconds, for the kernel's sampler (on in test
            // builds): its [prof] lines for this run say where that phase goes.
            if (args.Length > 1 && args[0] == "--profile")
                return Profile(args[1]);
            if (args.Length > 0 && args[0] == "--ipc")
            {
                s_ipcSmallOnly = args.Length > 2 && args[2] == "small";
                if (args.Length > 3) s_ipcSmallCount = int.Parse(args[3]);
                return Ipc(args.Length > 1 ? (uint)int.Parse(args[1]) : 16u);
            }
            if (args.Length > 1 && args[0] == "--ipc-threads")
                return IpcThreads((uint)int.Parse(args[1]), args.Length > 2 ? int.Parse(args[2]) : 20000);
            if (args.Length > 3 && args[0] == "--ipc-child")
                return IpcChild(args[1], args[2], int.Parse(args[3]));
            Console.WriteLine("[pipeperf] begin");

            Small small = new Small { A = 1, B = 2, S = "small" };
            Holder holder = new Holder { Id = 7, Items = new Item[100] };
            for (int i = 0; i < 100; i++) holder.Items[i] = new Item { X = i, Y = i * 2 };
            Expando bag = new Expando();
            for (int i = 0; i < 8; i++)
            {
                if ((i & 1) == 0) bag["k" + i.ToString()] = i;
                else bag["k" + i.ToString()] = "v" + i.ToString();
            }

            int smallObjects = Phases("small", small, 2000);
            int holderObjects = Phases("holder", holder, 200);
            int bagObjects = Phases("expando", bag, 1000);

            Baseline("small", 2000, smallObjects, WriteSmall, ReadSmall, small);
            Baseline("holder", 200, holderObjects, WriteHolder, ReadHolder, holder);
            Baseline("expando", 1000, bagObjects, WriteBag, ReadBag, bag);

            // 5000 records, 30 001 objects, about 1.5 MB a message (step208).
            var big = new BigBatch { Items = SharpOS.Micro.BigGraph.Build() };
            int bigObjects = Phases("big", big, 20);
            Baseline("big", 20, bigObjects, (w, b) => SharpOS.Micro.BigGraph.Write(w, b.Items),
                     r => new BigBatch { Items = SharpOS.Micro.BigGraph.Read(r) }, big);

            UpperPaths();
            Tests();

            Console.WriteLine("[pipeperf] done: passed " + s_passed.ToString() + ", failed " + s_failed.ToString());
            return s_passed;
        }

        // ---- one phase for the sampler ----

        private static int Profile(string phase)
        {
            var big = new BigBatch { Items = SharpOS.Micro.BigGraph.Build() };
            Region.Plan plan = Region.Lay(big, out string complaint);
            if (plan == null) { Console.WriteLine("[profile] not laid out: " + complaint); return 1; }
            ulong size = plan.Size;
            byte* template = (byte*)PipeTransport.Allocate(size);
            byte* work = (byte*)PipeTransport.Allocate(size);
            Region.Write(plan, template);
            var layout = new RegionWriter();
            var translate = new RegionReader();
            layout.Lay(big);
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);
            var reader = new BinaryReader(stream);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            int rounds = 0;
            while (clock.ElapsedMilliseconds < 3000)
            {
                switch (phase)
                {
                    case "lay": layout.Lay(big); break;
                    case "write": layout.Write(work); break;
                    case "resolve":
                        SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy(work, template, size);
                        translate.Resolve(work, size, out _);
                        break;
                    case "release":
                        SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy(work, template, size);
                        translate.Resolve(work, size, out _);
                        translate.Release(work, size);
                        break;
                    case "toheap":
                        if (rounds == 0)
                        {
                            SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy(work, template, size);
                            translate.Resolve(work, size, out _);
                        }
                        Region.ToHeap(work, size);
                        break;
                    case "bw":
                        stream.Position = 0;
                        SharpOS.Micro.BigGraph.Write(writer, big.Items);
                        stream.Position = 0;
                        SharpOS.Micro.BigGraph.Read(reader);
                        break;
                    default:
                        Console.WriteLine("[profile] phases: lay write resolve release toheap bw");
                        return 2;
                }
                rounds++;
            }
            Console.WriteLine("[profile] " + phase + " rounds=" + rounds.ToString() + " ms=" + clock.ElapsedMilliseconds.ToString());
            PipeTransport.Free(template);
            PipeTransport.Free(work);
            return 0;
        }

        // ---- the lower layer, phase by phase ----

        private static int Phases<T>(string name, T root, int n) where T : class
        {
            Region.Plan plan = Region.Lay(root, out string complaint);
            if (plan == null) { Check(name + ": laid out (" + complaint + ")", false); return 1; }
            int objects = plan.Count;
            ulong size = plan.Size;
            byte* template = (byte*)PipeTransport.Allocate(size);
            byte* work = (byte*)PipeTransport.Allocate(size);
            Region.Write(plan, template);

            // The reusable writer and reader a pipe end keeps (step195).
            var layout = new RegionWriter();
            var translate = new RegionReader();
            long lay = Loop(n, () => layout.Lay(root));
            layout.Lay(root);
            long write = Loop(n, () => layout.Write(work));
            long copy = Loop(n, () => SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy(work, template, size));
            long resolve = Loop(n, () =>
            {
                SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy(work, template, size);
                translate.Resolve(work, size, out _);
            }) - copy;
            long release = Loop(n, () =>
            {
                SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy(work, template, size);
                translate.Resolve(work, size, out _);
                translate.Release(work, size);
            }) - copy - resolve;
            RegionShapes shapes = RegionShapes.Parse(MessageCatalog.Schema, out _);
            long validate = Loop(n, () => shapes.Validate(template, size, out _));
            SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy(work, template, size);
            translate.Resolve(work, size, out _);
            long toHeap = Loop(n, () => Region.ToHeap(work, size));
            long allocFree = Loop(n, () => PipeTransport.Free(PipeTransport.Allocate(size)));

            PipeTransport.Create(16, PipeOverflow.DropOldest, out int w, out int r);
            long transport = Loop(n, () =>
            {
                void* b = PipeTransport.Allocate(size);
                PipeTransport.Send(w, b, size);
                PipeTransport.Receive(r, false, out void* got, out _, out _);
                PipeTransport.Free(got);
            }) - allocFree;
            PipeTransport.Close(w);
            PipeTransport.Close(r);
            PipeTransport.Free(template);
            PipeTransport.Free(work);

            // The whole path, typed: Copy, send, receive in place (a loop's
            // step: the wrapper is reused), Dispose at the next step.
            Pipe.Create<T>(16, PipeOverflow.DropOldest, out PipeWriter<T> writer, out PipeReader<T> reader);
            var steps = reader.GetEnumerator();
            for (int i = 0; i < 50; i++) { writer.Copy(root); steps.MoveNext(); }
            ulong appBefore = SharpOS.Std.NoRuntime.GcHeap.AllocCount;
            ulong kernelBefore = Process.KernelAllocations();
            var clock0 = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++) { writer.Copy(root); steps.MoveNext(); }
            long full = clock0.ElapsedTicks;
            ulong appAllocs = SharpOS.Std.NoRuntime.GcHeap.AllocCount - appBefore;
            ulong kernelAllocs = Process.KernelAllocations() - kernelBefore;
            writer.Dispose();
            steps.Dispose();

            Console.WriteLine("[perf195] " + name + ": " + objects.ToString() + " objects, " + size.ToString() + " bytes, per message (ns)");
            Line(name, "lay", lay, n, objects);
            Line(name, "write", write, n, objects);
            Line(name, "alloc_free", allocFree, n, objects);
            Line(name, "send_receive", transport, n, objects);
            Line(name, "validate_views", validate, n, objects);
            Line(name, "resolve", resolve, n, objects);
            Line(name, "release", release, n, objects);
            Line(name, "to_heap", toHeap, n, objects);
            Line(name, "full_path", full, n, objects);
            Console.WriteLine("[perf195] " + name + ".allocs_per_message app=" + Per(appAllocs, n) + " kernel=" + Per(kernelAllocs, n));
            Check(name + ": measured", full > 0);
            return objects;
        }

        private static string Per(ulong count, int n)
            => (count / (ulong)n).ToString() + "." + ((count * 100 / (ulong)n) % 100).ToString("00");

        private static void Line(string graph, string phase, long ticks, int n, int objects)
        {
            long perMessage = ticks * 1000000000 / System.Diagnostics.Stopwatch.Frequency / n;
            Console.WriteLine("[perf195] " + graph + "." + phase + "_ns=" + perMessage.ToString()
                              + " per_object_ns=" + (perMessage / objects).ToString());
        }

        private static long Loop(int n, Action body)
        {
            for (int i = 0; i < 20; i++) body();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++) body();
            return clock.ElapsedTicks;
        }

        // ---- the same graphs by hand ----

        private static readonly MemoryStream s_stream = new MemoryStream();

        private static void Baseline<T>(string name, int n, int objects, Action<BinaryWriter, T> write, Func<BinaryReader, T> read, T root)
        {
            var writer = new BinaryWriter(s_stream);
            var reader = new BinaryReader(s_stream);
            for (int i = 0; i < 20; i++) { s_stream.Position = 0; write(writer, root); s_stream.Position = 0; read(reader); }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                s_stream.Position = 0;
                write(writer, root);
                s_stream.Position = 0;
                read(reader);
            }
            Line(name, "binary_writer_reader", clock.ElapsedTicks, n, objects);
        }

        private static void WriteSmall(BinaryWriter w, Small s) { w.Write(s.A); w.Write(s.B); w.Write(s.S); }
        private static Small ReadSmall(BinaryReader r) => new Small { A = r.ReadInt32(), B = r.ReadInt32(), S = r.ReadString() };

        private static void WriteHolder(BinaryWriter w, Holder h)
        {
            w.Write(h.Id);
            w.Write(h.Items.Length);
            foreach (Item it in h.Items) { w.Write(it.X); w.Write(it.Y); }
        }

        private static Holder ReadHolder(BinaryReader r)
        {
            var h = new Holder { Id = r.ReadInt32() };
            h.Items = new Item[r.ReadInt32()];
            for (int i = 0; i < h.Items.Length; i++) h.Items[i] = new Item { X = r.ReadInt32(), Y = r.ReadInt32() };
            return h;
        }

        private static void WriteBag(BinaryWriter w, Expando x)
        {
            w.Write(x.Count);
            foreach (var pair in x)
            {
                w.Write(pair.Key);
                if (pair.Value is int i) { w.Write((byte)0); w.Write(i); }
                else { w.Write((byte)1); w.Write((string)pair.Value); }
            }
        }

        private static Expando ReadBag(BinaryReader r)
        {
            var x = new Expando();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string key = r.ReadString();
                if (r.ReadByte() == 0) x[key] = r.ReadInt32();
                else x[key] = r.ReadString();
            }
            return x;
        }
    }
}
