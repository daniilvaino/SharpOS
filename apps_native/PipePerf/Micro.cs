// The pieces of a BinaryWriter/BinaryReader round trip, one at a time
// (step208). The same file is compiled into PIPEPERF (NativeAOT over our std)
// and into apps_managed/Bench (stock CoreCLR), so a gap between the two lines
// of one piece is the runtime and the library, not the code. One line per
// piece: `[micro] <name> ns_per_op=<n>`.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SharpOS.Micro
{
    public static class MicroBench
    {
        private sealed class Item { public int X; public int Y; }

        private abstract class Shape { public abstract int Value(int x); }
        private sealed class Square : Shape { public override int Value(int x) => x * x; }

        private interface IValue { int Value(int x); }
        private sealed class Doubler : IValue { public int Value(int x) => x + x; }

        private const int N = 1_000_000;

        private static MemoryStream s_stream;
        private static BinaryWriter s_writer;
        private static BinaryReader s_reader;
        private static object s_sink;
        private static int s_intSink;

        public static void Run()
        {
            s_stream = new MemoryStream();
            s_writer = new BinaryWriter(s_stream);
            s_reader = new BinaryReader(s_stream);

            for (int round = 0; round < 2; round++)
            {
                bool print = round == 1;
                Time("loop", EmptyLoop, print);
                Time("position_set", PositionSet, print);
                Time("write_int", WriteInt, print);
                Time("read_int", ReadInt, print);
                Time("write_byte", WriteByte, print);
                Time("read_byte", ReadByte, print);
                Time("write_string5", WriteString, print);
                Time("read_string5", ReadString, print);
                Time("new_item", NewItem, print);
                Time("new_array100", NewArray, print);
                Time("new_string5", NewString, print);
                Time("virtual_call", VirtualCall, print);
                Time("interface_call", InterfaceCall, print);
                Time("dictionary8", Dictionary8, print);
                Time("span_copy4", SpanCopy, print);
            }
        }

        /// <summary>
        /// Counters of the runtime underneath, when it has any to offer (PIPEPERF
        /// sets the GC heap's): printed as their growth over each piece.
        /// </summary>
        public static Func<ulong[]> Counters;
        public static string[] CounterNames;

        private static void Time(string name, Func<long> body, bool print)
        {
            ulong[] before = Counters?.Invoke();
            long started = Stopwatch.GetTimestamp();
            long ops = body();
            long ticks = Stopwatch.GetTimestamp() - started;
            ulong[] after = Counters?.Invoke();
            if (!print) return;
            long ns10 = ticks * 10_000_000_000 / Stopwatch.Frequency / ops;
            string line = "[micro] " + name + " ns_per_op=" + (ns10 / 10).ToString() + "." + (ns10 % 10).ToString();
            if (before != null && after != null)
                for (int i = 0; i < before.Length; i++)
                    line += " " + CounterNames[i] + "=" + (after[i] - before[i]).ToString();
            Console.WriteLine(line);
        }

        private static long EmptyLoop()
        {
            int x = 0;
            for (int i = 0; i < N; i++) x += i;
            s_intSink = x;
            return N;
        }

        private static long PositionSet()
        {
            for (int i = 0; i < N; i++) s_stream.Position = 0;
            return N;
        }

        private static long WriteInt()
        {
            for (int i = 0; i < N; i++)
            {
                if ((i & 1023) == 0) s_stream.Position = 0;
                s_writer.Write(i);
            }
            return N;
        }

        private static long ReadInt()
        {
            s_stream.Position = 0;
            for (int i = 0; i < 1024; i++) s_writer.Write(i);
            int x = 0;
            for (int i = 0; i < N; i++)
            {
                if ((i & 1023) == 0) s_stream.Position = 0;
                x += s_reader.ReadInt32();
            }
            s_intSink = x;
            return N;
        }

        private static long WriteByte()
        {
            for (int i = 0; i < N; i++)
            {
                if ((i & 1023) == 0) s_stream.Position = 0;
                s_writer.Write((byte)i);
            }
            return N;
        }

        private static long ReadByte()
        {
            s_stream.Position = 0;
            for (int i = 0; i < 1024; i++) s_writer.Write((byte)i);
            int x = 0;
            for (int i = 0; i < N; i++)
            {
                if ((i & 1023) == 0) s_stream.Position = 0;
                x += s_reader.ReadByte();
            }
            s_intSink = x;
            return N;
        }

        private static long WriteString()
        {
            const int Count = N / 4;
            for (int i = 0; i < Count; i++)
            {
                if ((i & 255) == 0) s_stream.Position = 0;
                s_writer.Write("small");
            }
            return Count;
        }

        private static long ReadString()
        {
            const int Count = N / 4;
            s_stream.Position = 0;
            for (int i = 0; i < 256; i++) s_writer.Write("small");
            for (int i = 0; i < Count; i++)
            {
                if ((i & 255) == 0) s_stream.Position = 0;
                s_sink = s_reader.ReadString();
            }
            return Count;
        }

        private static long NewItem()
        {
            for (int i = 0; i < N; i++) s_sink = new Item { X = i, Y = i };
            return N;
        }

        private static long NewArray()
        {
            const int Count = N / 10;
            for (int i = 0; i < Count; i++) s_sink = new Item[100];
            return Count;
        }

        private static long NewString()
        {
            char[] chars = { 's', 'm', 'a', 'l', 'l' };
            for (int i = 0; i < N; i++) s_sink = new string(chars);
            return N;
        }

        private static long VirtualCall()
        {
            Shape shape = new Square();
            int x = 0;
            for (int i = 0; i < N; i++) x += shape.Value(i);
            s_intSink = x;
            return N;
        }

        private static long InterfaceCall()
        {
            IValue value = new Doubler();
            int x = 0;
            for (int i = 0; i < N; i++) x += value.Value(i);
            s_intSink = x;
            return N;
        }

        private static long Dictionary8()
        {
            const int Count = N / 20;
            string[] keys = { "k0", "k1", "k2", "k3", "k4", "k5", "k6", "k7" };
            int x = 0;
            for (int i = 0; i < Count; i++)
            {
                var d = new Dictionary<string, object>();
                for (int k = 0; k < 8; k++) d[keys[k]] = keys[k];
                if (d.TryGetValue("k5", out object v) && v != null) x++;
            }
            s_intSink = x;
            return Count;
        }

        private static long SpanCopy()
        {
            byte[] target = new byte[16];
            Span<byte> source = stackalloc byte[4];
            for (int i = 0; i < N; i++)
            {
                source[0] = (byte)i;
                source.CopyTo(new Span<byte>(target, i & 7, 4));
            }
            s_intSink = target[3];
            return N;
        }
    }
}
