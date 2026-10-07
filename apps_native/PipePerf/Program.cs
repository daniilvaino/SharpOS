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
            MessageCatalog.Ensure();
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

            Tests();

            Console.WriteLine("[pipeperf] done: passed " + s_passed.ToString() + ", failed " + s_failed.ToString());
            return s_passed;
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
