using System;
using System.Collections.Generic;
using SharpOS.AppSdk;
using SharpOS.Std.Exchange;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    [Message]
    public sealed class Node
    {
        public int Id;
        public Node A;
        public Node B;
    }

    [Message]
    public sealed class Envelope
    {
        public int Type;
        public int Seq;
        public object Item;
    }

    // The checks of step195: no allocation per message, big and deep graphs,
    // the reverse pass's bytes, a refusal that changes nothing, fifty types in
    // one stream.
    internal static unsafe partial class AppEntry
    {
        private static void Tests()
        {
            Run("2 allocations", Allocations);
            Run("3 big graphs", BigGraphs);
            Run("4 bytes back", BytesBack);
            Run("5 refusal", Refusal);
            Run("6 fifty types", FiftyTypes);
        }

        private static void Run(string name, Action test)
        {
            Console.WriteLine("[pipeperf] " + name);
            try { test(); }
            catch (Exception e) { Check(name + ": threw " + e.Message, false); }
        }

        // ---- 2 ----

        // Allocations of a path over n messages, in the app and in the kernel.
        // Each path is run twice, with 1 000 and with 11 000 messages: what it
        // costs once (tasks, buffers growing) is in both and cancels.
        private static void Allocations()
        {
            var small = new Small { A = 1, B = 2, S = "small" };
            Path("Copy and receive in place", n => CopyAndReceive(small, n));
            Path("Move on", n => MoveOn(small, n));
            Path("read by views", n => ReadViews(small, n));
            Path("WriteTo, untranslated", n => WriteToRaw(small, n));
            Path("WriteTo, translated", n => WriteToTyped(small, n));
        }

        private static void Path(string name, Action<int> run)
        {
            run(200);
            ulong a0 = SharpOS.Std.NoRuntime.GcHeap.AllocCount, k0 = Process.KernelAllocations();
            run(1000);
            ulong a1 = SharpOS.Std.NoRuntime.GcHeap.AllocCount, k1 = Process.KernelAllocations();
            run(11000);
            ulong a2 = SharpOS.Std.NoRuntime.GcHeap.AllocCount, k2 = Process.KernelAllocations();
            long app = (long)(a2 - a1) - (long)(a1 - a0);
            long kernel = (long)(k2 - k1) - (long)(k1 - k0);
            Console.WriteLine("[pipeperf] " + name + ": allocations for 10 000 more messages: app " + app.ToString()
                              + ", kernel " + kernel.ToString());
            Check("2: " + name + ": no allocation per message, app and kernel", app <= 0 && kernel <= 0);
        }

        private static void CopyAndReceive(Small m, int n)
        {
            Pipe.Create<Small>(16, PipeOverflow.DropOldest, out var w, out var r);
            var steps = r.GetEnumerator();
            int sum = 0;
            for (int i = 0; i < n; i++)
            {
                w.Copy(m);
                steps.MoveNext();
                sum += steps.Current.A;
            }
            w.Dispose();
            steps.Dispose();
            if (sum != n) throw new InvalidOperationException("wrong values");
        }

        private static void MoveOn(Small m, int n)
        {
            Pipe.Create<Small>(16, PipeOverflow.DropOldest, out var w1, out var r1);
            Pipe.Create<Small>(16, PipeOverflow.DropOldest, out var w2, out var r2);
            var steps = r2.GetEnumerator();
            Region<Small> region = null;
            int sum = 0;
            for (int i = 0; i < n; i++)
            {
                w1.Copy(m);
                region = r1.Receive(region);
                w2.Move(region);
                steps.MoveNext();
                sum += steps.Current.B;
            }
            w1.Dispose(); r1.Dispose(); w2.Dispose();
            steps.Dispose();
            if (sum != 2 * n) throw new InvalidOperationException("wrong values");
        }

        private static void ReadViews(Small m, int n)
        {
            Pipe.Create<Small>(16, PipeOverflow.DropOldest, out var w, out var typed);
            var raw = new RawPipeReader(typed.Handle);
            var steps = raw.GetEnumerator();
            long sum = 0;
            for (int i = 0; i < n; i++)
            {
                w.Copy(m);
                steps.MoveNext();
                sum += (int)steps.Current["A"] + (int)steps.Current["B"];
            }
            w.Dispose();
            steps.Dispose();
            if (sum != 3L * n) throw new InvalidOperationException("wrong values");
        }

        // A writer task, the pump (WriteTo by name), and this thread reading the far end.
        private static void WriteToRaw(Small m, int n) => Pumped(m, n, raw: true);

        private static void WriteToTyped(Small m, int n) => Pumped(m, n, raw: false);

        private static int s_pumps;

        private static void Pumped(Small m, int n, bool raw)
        {
            string name = "perf.pump." + (s_pumps++).ToString();
            PipeReader<Small> far = Pipe.Read<Small>(name);
            Pipe.Create<Small>(16, PipeOverflow.DropOldest, out var w, out var r);
            var pump = System.Threading.Tasks.Task.Run(() =>
            {
                if (raw) new RawPipeReader(r.Handle).WriteTo(name);
                else r.WriteTo(name);
            });
            var writer = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < n; i++) w.Copy(m);
                w.Dispose();
            });
            int count = 0;
            foreach (Small s in far) count += s.A;
            writer.Wait();
            pump.Wait();
            if (count != n) throw new InvalidOperationException("wrong count " + count.ToString());
        }

        // ---- 3 ----

        // The app's heap is a 64 MiB pool cut into 256 KiB segments: after a
        // while nothing larger fits in one piece, so the test keeps its
        // 100 000 of everything in chunks.
        private const int Chunk = 8192;

        private static T[][] Chunks<T>(int count)
        {
            var chunks = new T[(count + Chunk - 1) / Chunk][];
            for (int i = 0; i < chunks.Length; i++) chunks[i] = new T[Chunk];
            return chunks;
        }

        private static void BigGraphs()
        {
            const int Count = 100000;
            Node[][] nodes = Chunks<Node>(Count);
            for (int i = 0; i < Count; i++) nodes[i / Chunk][i % Chunk] = new Node { Id = i };
            uint seed = 12345;
            for (int i = 0; i < Count; i++)
            {
                Node n = nodes[i / Chunk][i % Chunk];
                seed = seed * 1103515245 + 12345;
                int a = (int)((seed >> 8) % Count);
                n.A = nodes[a / Chunk][a % Chunk];
                seed = seed * 1103515245 + 12345;
                int b = (int)((seed >> 8) % Count);
                n.B = (seed & 3) == 0 ? null : nodes[b / Chunk][b % Chunk];
            }
            // Every node reachable from the root: a spine through all of them.
            for (int i = 0; i + 1 < Count; i++)
            {
                Node n = nodes[i / Chunk][i % Chunk];
                Node next = nodes[(i + 1) / Chunk][(i + 1) % Chunk];
                if ((i & 1) == 0) n.B = next; else n.A = next;
            }
            Check("3: a graph of 100 000 objects with shared references and cycles: the copy is isomorphic",
                  Isomorphic(nodes, Count));

            var list = new Node { Id = 0 };
            Node tail = list;
            for (int i = 1; i < Count; i++) { tail.A = new Node { Id = i }; tail = tail.A; }
            Check("3: a list of 100 000 nodes copied and read: no stack overflow, all there", ListThrough(list, Count));
        }

        // Ids are 0..count-1, one per node: the copy is isomorphic when each
        // id has exactly one copy and every edge of the original joins the
        // copies of its ends. The copies are walked from the root with a
        // stack of addresses, not references (SOSR002: a region reference may
        // not be kept in a heap collection).
        private static bool Isomorphic(Node[][] nodes, int count)
        {
            Pipe.Create<Node>(1, PipeOverflow.DropOldest, out var w, out var r);
            if (w.Copy(nodes[0][0]) != PipeStatus.Ok) return false;
            using Region<Node> region = r.Receive();
            w.Dispose();
            r.Dispose();

            ulong[][] copyOf = Chunks<ulong>(count);
            ulong[][] stack = Chunks<ulong>(2 * count + 1);
            long top = 0;
            stack[0][0] = AddressOf(region.Root);
            top = 1;
            while (top > 0)
            {
                top--;
                ulong at = stack[top / Chunk][top % Chunk];
                Node c = System.Runtime.CompilerServices.Unsafe.As<ulong, Node>(ref at);
                if (c.Id < 0 || c.Id >= count) return false;
                ulong known = copyOf[c.Id / Chunk][c.Id % Chunk];
                if (known != 0)
                {
                    if (known != at) return false;
                    continue;
                }
                copyOf[c.Id / Chunk][c.Id % Chunk] = at;
                if (c.A != null) { stack[top / Chunk][top % Chunk] = AddressOf(c.A); top++; }
                if (c.B != null) { stack[top / Chunk][top % Chunk] = AddressOf(c.B); top++; }
            }

            for (int i = 0; i < count; i++)
            {
                Node o = nodes[i / Chunk][i % Chunk];
                ulong at = copyOf[i / Chunk][i % Chunk];
                if (at == 0) return false;
                Node c = System.Runtime.CompilerServices.Unsafe.As<ulong, Node>(ref at);
                if (!Edge(o.A, c.A, copyOf) || !Edge(o.B, c.B, copyOf)) return false;
            }
            return true;
        }

        private static ulong AddressOf(Node n) => System.Runtime.CompilerServices.Unsafe.As<Node, ulong>(ref n);

        private static bool Edge(Node original, Node copy, ulong[][] copyOf)
        {
            if (original == null || copy == null) return original == null && copy == null;
            return copyOf[original.Id / Chunk][original.Id % Chunk] == AddressOf(copy);
        }

        private static bool ListThrough(Node list, int count)
        {
            Pipe.Create<Node>(1, PipeOverflow.DropOldest, out var w, out var r);
            if (w.Copy(list) != PipeStatus.Ok) return false;
            using Region<Node> region = r.Receive();
            w.Dispose();
            r.Dispose();
            int n = 0;
            for (Node at = region.Root; at != null; at = at.A)
                if (at.Id != n++) return false;
            return n == count;
        }

        // ---- 4 ----

        private static void BytesBack()
        {
            var holder = new Holder { Id = 3, Items = new Item[100] };
            for (int i = 0; i < 100; i++) holder.Items[i] = new Item { X = i, Y = -i };
            var bag = new Expando();
            for (int i = 0; i < 8; i++) bag["k" + i.ToString()] = (i & 1) == 0 ? (object)i : "v" + i.ToString();
            Check("4: copy, translation, reverse: the block is byte for byte what was written (array of 100)", RoundTrip(holder));
            Check("4: the same for an Expando of 8", RoundTrip(bag));
        }

        private static bool RoundTrip(object root)
        {
            var writer = new RegionWriter();
            var reader = new RegionReader();
            if (!writer.Lay(root)) return false;
            ulong size = writer.Size;
            byte* block = (byte*)PipeTransport.Allocate(size);
            byte[] before = new byte[size];
            writer.Write(block);
            for (ulong i = 0; i < size; i++) before[i] = block[i];
            bool ok = reader.Resolve(block, size, out _) && reader.Release(block, size);
            for (ulong i = 0; i < size && ok; i++) ok = block[i] == before[i];
            PipeTransport.Free(block);
            return ok;
        }

        // ---- 5 ----

        private static void Refusal()
        {
            var holder = new Holder { Id = 5, Items = new Item[10] };
            for (int i = 0; i < 10; i++) holder.Items[i] = new Item { X = i };
            var writer = new RegionWriter();
            writer.Lay(holder);
            ulong size = writer.Size;
            byte* block = (byte*)PipeTransport.Allocate(size);
            writer.Write(block);

            // The last record's key, made one nobody declared.
            ulong last = 0;
            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = (ulong)block + cursor + Region.HeaderSize;
                last = objectAt;
                cursor += Region.HeaderSize + TypePlans.ByKey(*(ulong*)objectAt).PayloadSize(objectAt);
            }
            *(ulong*)last = 0x8000_1234_5678_9AB1UL;
            byte[] before = new byte[size];
            for (ulong i = 0; i < size; i++) before[i] = block[i];

            var reader = new RegionReader();
            bool refused = !reader.Resolve(block, size, out _);
            bool same = true;
            for (ulong i = 0; i < size && same; i++) same = block[i] == before[i];
            PipeTransport.Free(block);
            Console.WriteLine("[pipeperf] refusal: " + (reader.Complaint ?? "none"));
            Check("5: an unknown key in the last record: refused, the key named, not a byte changed",
                  refused && same && reader.MissingKey == 0x8000_1234_5678_9AB1UL);
        }

        // ---- 6 ----

        private static void FiftyTypes()
        {
            const int Messages = 2000;
            Pipe.Create<Envelope>(16, PipeOverflow.DropOldest, out var w, out var r);
            var writer = System.Threading.Tasks.Task.Run(() =>
            {
                uint seed = 7;
                for (int i = 0; i < Messages; i++)
                {
                    seed = seed * 1103515245 + 12345;
                    int type = (int)((seed >> 8) % 50);
                    w.Copy(new Envelope { Type = type, Seq = i, Item = MixedTypes.Make(type, i) });
                }
                w.Dispose();
            });
            int n = 0, right = 0;
            var seen = new bool[50];
            foreach (Envelope e in r)
            {
                n++;
                seen[e.Type] = true;
                if (e.Seq == n - 1 && e.Item is Mixed m && m.Check(e.Seq)) right++;
            }
            writer.Wait();
            int kinds = 0;
            for (int i = 0; i < 50; i++) if (seen[i]) kinds++;
            Check("6: fifty types in one stream, " + n.ToString() + " messages of " + kinds.ToString()
                  + " types: every value right", n == Messages && right == Messages && kinds == 50);
        }
    }
}
