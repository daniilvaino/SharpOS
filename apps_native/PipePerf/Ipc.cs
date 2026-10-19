using System;
using System.IO;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // --ipc (step208): messages from this process to a child, the same three
    // graphs and the large one, three ways - a typed pipe read in place, the
    // same with each message copied into the child's heap, and BinaryWriter
    // frames down a byte pipe rebuilt with BinaryReader (what the host
    // reference, scratch hostipc, does over an OS pipe). The clock starts when
    // the child says it is ready and stops when it says it has read all.
    internal static unsafe partial class AppEntry
    {
        private static bool s_ipcSmallOnly;
        private static int s_ipcSmallCount = 20000;

        private static int Ipc(uint room)
        {
            var small = new Small { A = 1, B = 2, S = "small" };
            var holder = new Holder { Id = 7, Items = new Item[100] };
            for (int i = 0; i < 100; i++) holder.Items[i] = new Item { X = i, Y = i * 2 };
            var big = new BigBatch { Items = SharpOS.Micro.BigGraph.Build() };

            // --ipc ROOM small: the small message alone, typed and read in place -
            // for the kernel's counters of this run (switches, halts, waits).
            if (s_ipcSmallOnly)
            {
                IpcRun("small", "view", s_ipcSmallCount, 2, small, room);
                return 0;
            }
            foreach (string variant in new[] { "view", "heap", "bw" })
            {
                IpcRun("small", variant, 20000, 2, small, room);
                IpcRun("holder", variant, 2000, 102, holder, room);
                IpcRun("big", variant, 40, SharpOS.Micro.BigGraph.Objects + 1, big, room);
            }
            return 0;
        }

        private static void IpcRun(string graph, string variant, int count, int objects, object message, uint room)
        {
            var there = Pipe.Create(graph == "big" ? 4u : room);
            var back = Pipe.Create();
            Process child = Process.Start("PIPEPERF.EXE", new[] { "--ipc-child", graph, variant, count.ToString() },
                                          there.ReadEnd, back.WriteEnd);
            var answers = back.ReadEnd.Read<string>().GetEnumerator();
            if (!answers.MoveNext() || answers.Current != "ready") { Console.WriteLine("[ipc] no ready"); return; }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (variant == "bw")
            {
                using Stream bytes = there.WriteEnd.WriteBytes();
                var frame = new BinaryWriter(bytes);
                var body = new MemoryStream();
                var w = new BinaryWriter(body);
                for (int i = 0; i < count; i++)
                {
                    body.Position = 0;
                    body.SetLength(0);
                    WriteGraph(w, graph, message);
                    frame.Write((int)body.Length);
                    bytes.Write(body.GetBuffer(), 0, (int)body.Length);
                }
                bytes.Flush();
            }
            else if (graph == "small") Send(there.WriteEnd.Write<Small>(), (Small)message, count);
            else if (graph == "holder") Send(there.WriteEnd.Write<Holder>(), (Holder)message, count);
            else Send(there.WriteEnd.Write<BigBatch>(), (BigBatch)message, count);

            bool done = answers.MoveNext() && answers.Current == "done";
            long ticks = clock.ElapsedTicks;
            answers.Dispose();
            child.WaitForExit();
            child.Dispose();
            long ns = ticks * 1_000_000_000 / System.Diagnostics.Stopwatch.Frequency / count;
            Console.WriteLine("[ipc] room " + room.ToString() + " " + graph + "." + variant + " ns_per_message=" + ns.ToString()
                              + " per_object_ns=" + (ns / objects).ToString() + (done ? "" : " (child did not finish)"));
        }

        // --ipc-threads ROOM: the small message between two threads of this
        // process instead of two processes - what a switch costs without a
        // change of process (another image's code and data in the caches).
        private static int IpcThreads(uint room, int count)
        {
            int Count = count;
            var small = new Small { A = 1, B = 2, S = "small" };
            var pair = Pipe.Create(room);
            PipeReadEnd readEnd = pair.ReadEnd;
            long sink = 0;
            int got = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var reader = System.Threading.Tasks.Task.Run(() =>
            {
                foreach (Small m in readEnd.Read<Small>()) { sink += m.A; got++; }
            });
            Send(pair.WriteEnd.Write<Small>(), small, Count);
            reader.Wait();
            long ns = clock.ElapsedTicks * 1_000_000_000 / System.Diagnostics.Stopwatch.Frequency / Count;
            Console.WriteLine("[ipc] threads room " + room.ToString() + " small.view ns_per_message=" + ns.ToString()
                              + (got == Count ? "" : " (read " + got.ToString() + ")"));
            return (int)(sink & 1);
        }

        private static void Send<T>(PipeWriter<T> writer, T message, int count) where T : class
        {
            for (int i = 0; i < count; i++) writer.Copy(message);
            writer.Dispose();
        }

        private static void WriteGraph(BinaryWriter w, string graph, object message)
        {
            if (graph == "small") WriteSmall(w, (Small)message);
            else if (graph == "holder") WriteHolder(w, (Holder)message);
            else SharpOS.Micro.BigGraph.Write(w, ((BigBatch)message).Items);
        }

        private static int IpcChild(string graph, string variant, int count)
        {
            PipeWriter<string> back = Pipe.Write<string>();
            back.Copy("ready");
            long sink = 0;
            int got = 0;
            if (variant == "bw")
            {
                Stream bytes = Pipe.ReadBytes();
                var frames = new BinaryReader(bytes);
                byte[] buffer = new byte[1 << 16];
                var body = new MemoryStream();
                var r = new BinaryReader(body);
                for (; got < count; got++)
                {
                    int length = frames.ReadInt32();
                    if (buffer.Length < length) buffer = new byte[length];
                    int have = 0;
                    while (have < length)
                    {
                        int n = bytes.Read(buffer, have, length - have);
                        if (n == 0) break;
                        have += n;
                    }
                    body.Position = 0;
                    body.SetLength(0);
                    body.Write(buffer, 0, length);
                    body.Position = 0;
                    object rebuilt = graph == "small" ? ReadSmall(r)
                                   : graph == "holder" ? ReadHolder(r)
                                   : (object)SharpOS.Micro.BigGraph.Read(r);
                    sink += rebuilt.GetHashCode() & 1;
                }
            }
            else if (graph == "small")
                foreach (Small m in Pipe.Read<Small>()) { sink += variant == "heap" ? m.ToHeap().A : m.A; got++; }
            else if (graph == "holder")
                foreach (Holder m in Pipe.Read<Holder>()) { sink += variant == "heap" ? m.ToHeap().Id : m.Items[99].Y; got++; }
            else
                foreach (BigBatch m in Pipe.Read<BigBatch>()) { sink += variant == "heap" ? m.ToHeap().Items.Length : m.Items[4999].Id; got++; }

            back.Copy(got == count ? "done" : "short " + got.ToString());
            back.Dispose();
            return (int)(sink & 1);
        }
    }
}
