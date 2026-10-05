using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OS.Hal;
using OS.Kernel.Memory;
using OS.Kernel.Pipes;
using OS.Kernel.Threading;
using SharpOS.Std.Exchange;
using SharpOS.Std.Exchange.Probe;
using SharpOS.Std.Pipes;
using SharpOS.Std.Pipes.Probe;

namespace SharpOS.Probe
{
    // The kernel's types for the connection test (pipe spec Р22). AotTests has
    // a Versioned of its own under the same full name with another layout, and
    // no Different at all.
    [Message]
    public sealed class Versioned
    {
        public long A;
        public int B;
    }

    [Message]
    public sealed class Different
    {
        public int Z;
    }
}

namespace OS.Kernel.Diagnostics
{
    // Native pipe tests, kernel side (pipe spec, "родная труба", tests 1–6,
    // 9, 13). AotTests drives them through one service, PipeProbe:
    //
    //   op 1  arm a kernel reader thread for test <arg>: 1 journal (untyped),
    //         21/22 writer death (normal exit / code 134), 3 limit, 6 echo back
    //   op 2  verdict of test <arg>: done, count, ok, final status, extra
    //   op 3  feed: the kernel writes <arg> probe graphs to "probe.feed"
    //   op 4  flood: the kernel writes <arg> notes to "probe.flood", never waiting
    //   op 5  echo out: the kernel writes one EchoMessage to "probe.echo.out"
    //   op 6  exchange blocks live, pipes live
    //   op 7  kernel collector load on (1) or off (0)
    //   op 8  typed readers on "probe.typed.layout" / ".other" on (1), closed (0)
    //
    // Readers are kernel threads that sleep on an empty queue; writers run in
    // the service call itself — the kernel never waits on a pipe (Р45), so a
    // service may write.
    internal static unsafe class PipeProbe
    {
        private const int Tests = 32;

        private struct Verdict
        {
            public int Done;
            public int Count;
            public int Ok;
            public int Status;
            public long Extra;
        }

        private static Verdict[] s_verdicts;
        private static volatile int s_armTest;
        private static object s_armed;
        private static volatile bool s_gcLoad;
        private static volatile int s_gcThreads;
        private static int s_typedLayout;
        private static int s_typedOther;

        [UnmanagedCallersOnly]
        public static int Service(int op, ulong argument, ulong* answer)
        {
            try
            {
                return Run(op, argument, answer);
            }
            catch (Exception e)
            {
                Say("op " + op.ToString() + " threw: " + e.Message);
                return (int)PipeStatus.Refused;
            }
        }

        private static int Run(int op, ulong argument, ulong* answer)
        {
            s_verdicts ??= new Verdict[Tests];
            switch (op)
            {
                case 1: return Arm((int)argument);
                case 2:
                {
                    int t = (int)argument;
                    if (t < 0 || t >= Tests || answer == null) return (int)PipeStatus.BadHandle;
                    Preemption.Suppress();
                    Verdict v = s_verdicts[t];
                    Preemption.Allow();
                    answer[0] = (ulong)v.Done;
                    answer[1] = (ulong)v.Count;
                    answer[2] = (ulong)v.Ok;
                    answer[3] = (ulong)(long)v.Status;
                    answer[4] = (ulong)v.Extra;
                    return 0;
                }
                case 3: return Feed((int)argument);
                case 4: return Flood((int)argument, answer);
                case 5: return EchoOut();
                case 15: return StdOut();
                case 6:
                    if (answer == null) return (int)PipeStatus.BadHandle;
                    answer[0] = ExchangeHeap.LiveBlocks;
                    answer[1] = KernelPipes.LivePipes;
                    answer[2] = ExchangeHeap.CarvedPages;
                    ulong alive = 0, runnable = 0, kernel = 0;
                    Preemption.Suppress();
                    for (OS.Kernel.Threading.Thread th = Scheduler.AllThreads; th != null; th = th.AllNext)
                    {
                        if (th.State == ThreadState.Exited) continue;
                        alive++;
                        if (th.State == ThreadState.Runnable || th.State == ThreadState.Running) runnable++;
                        if (th.AppGeneration == 0) kernel++;
                    }
                    Preemption.Allow();
                    answer[3] = alive;
                    answer[4] = runnable;
                    answer[5] = kernel;
                    return 0;
                case 7: return GcLoad(argument != 0);
                case 8: return Typed(argument != 0, answer);
                case 9:
                    KernelGcPreciseWalk.TraceWalks = (int)argument;
                    return 0;
                case 10:
                    WriteWatch.Arm(argument);
                    return 0;
                case 13:
                    // The kernel's GC stress, read only: an app's waits on
                    // kernel work stretch when it is on.
                    if (answer == null) return (int)PipeStatus.BadHandle;
                    answer[0] = SharpOS.Std.NoRuntime.GcStress.Every;
                    return 0;
                case 12:
                    // The byref barrier test, run by the kernel on its own pipe.
                    return ByRefProbe.RunOnPipe();
                case 11:
                    // GC stress on the kernel heap for the caller's run: the low
                    // half is N (0 off); the answer is the collections so far
                    // and the N it replaces, for the caller to put back. The
                    // walk lent to apps stops reporting each collection while
                    // either side is stressed.
                    if (answer != null)
                    {
                        answer[0] = SharpOS.Std.NoRuntime.GcStress.Collections;
                        answer[1] = SharpOS.Std.NoRuntime.GcStress.Every;
                    }
                    KernelGC.Stress((uint)argument);
                    AppGcService.Quiet = (argument >> 32) != 0;
                    return 0;
                default: return (int)PipeStatus.Unsupported;
            }
        }

        // ---- readers: kernel threads ----

        // The reader connects here, in the service call, before the app can
        // write: a writer whose pipe never had a reader takes the name and its
        // messages with it when it ends (KernelPipes.CloseEnd), and a reader
        // thread that connected after that waited for a writer that was gone.
        private static int Arm(int test)
        {
            if (test <= 0 || test >= Tests) return (int)PipeStatus.BadHandle;
            object reader = null;
            PipeStatus connected;
            string error;
            switch (test)
            {
                case 1:
                    connected = RawPipeReader.Connect("probe.journal", out RawPipeReader journal, out error);
                    reader = journal;
                    break;
                case 21:
                case 22:
                    connected = PipeReader<Note>.Connect(test == 21 ? "probe.death.normal" : "probe.death.crash",
                                                         out PipeReader<Note> death, out error);
                    reader = death;
                    break;
                case 3:
                    connected = PipeReader<Note>.Connect("probe.limit", out PipeReader<Note> limit, out error, capacity: 4);
                    reader = limit;
                    break;
                case 6:
                    connected = PipeReader<EchoMessage>.Connect("probe.echo.back", out PipeReader<EchoMessage> echo, out error);
                    reader = echo;
                    break;
                case 15:
                    connected = PipeReader<string>.Connect("probe.std.text", out PipeReader<string> text, out error);
                    reader = text;
                    break;
                case 16:
                    connected = PipeReader<byte[]>.Connect("probe.std.bytes", out PipeReader<byte[]> bytes, out error);
                    reader = bytes;
                    break;
                case 17:
                    connected = PipeReader<Expando>.Connect("probe.std.expando", out PipeReader<Expando> expando, out error);
                    reader = expando;
                    break;
                default:
                    return (int)PipeStatus.BadHandle;
            }
            if (connected != PipeStatus.Ok)
            {
                Say("arm " + test.ToString() + ": connect failed " + ((int)connected).ToString() + " " + error);
                return (int)connected;
            }

            Preemption.Suppress();
            s_verdicts[test] = default;
            s_armed = reader;
            s_armTest = test;
            Preemption.Allow();
            delegate* unmanaged<void> entry = &ReaderEntry;
            if (Scheduler.Spawn(entry, 64 * 1024) == null) return (int)PipeStatus.NoMemory;
            // The thread takes its test before anything else can arm another.
            while (s_armTest != 0) Scheduler.Yield();
            return 0;
        }

        [UnmanagedCallersOnly]
        private static void ReaderEntry()
        {
            int test = s_armTest;
            object reader = s_armed;
            s_armed = null;
            s_armTest = 0;
            try
            {
                switch (test)
                {
                    case 1: Journal((RawPipeReader)reader); break;
                    case 21:
                    case 22: Death(test, (PipeReader<Note>)reader); break;
                    case 3: Limit((PipeReader<Note>)reader); break;
                    case 6: EchoBack((PipeReader<EchoMessage>)reader); break;
                    case 15: StdText((PipeReader<string>)reader); break;
                    case 16: StdBytes((PipeReader<byte[]>)reader); break;
                    case 17: StdExpando((PipeReader<Expando>)reader); break;
                }
            }
            catch (Exception e)
            {
                Say("reader " + test.ToString() + " threw: " + e.Message);
                Finish(test, 0, false, PipeStatus.Refused, 0);
            }
            Scheduler.Exit();
        }

        // Test 1: the app's journal, of a type the kernel does not have. Read
        // by the pipe's description: printed, and Seq and Text checked by name.
        private static void Journal(RawPipeReader reader)
        {
            int count = 0;
            bool ok = true;
            using (reader)
            {
                RawRegion region;
                while ((region = reader.Receive()) != null)
                {
                    using (region)
                    {
                        if (count < 3)
                            region.Print(line => Console.WriteLine("[pipe-journal] " + line), out _);
                        ok &= ReadInt(region, "Seq") == count
                              && ReadString(region, "Text") == "entry " + count.ToString();
                        count++;
                    }
                }
                Say($"journal: {count} entries by the description, content {(ok ? "matches" : "DIFFERS")}, end {(int)reader.Status}");
                Finish(1, count, ok, reader.Status, 0);
            }
        }

        // Tests 2: the writer dies with messages queued. The reader waits for
        // that to happen, then drains, then sees the event.
        private static void Death(int test, PipeReader<Note> reader)
        {
            string name = test == 21 ? "probe.death.normal" : "probe.death.crash";
            using (reader)
            {
                int handle = reader.Handle;
                ulong waited = 0;
                while (!KernelPipes.PeerGone(ExchangeHeap.OwnerKernel, handle) && waited < 10_000)
                {
                    Scheduler.Sleep(5);
                    waited += 5;
                }
                int count = 0;
                bool ordered = true;
                Region<Note> region;
                while ((region = reader.Receive()) != null)
                {
                    using (region)
                        ordered &= region.Root.Seq == count++;
                }
                Say($"death ({name}): {count} drained after the writer was gone, then {(int)reader.Status}");
                Finish(test, count, ordered, reader.Status, 0);
            }
        }

        // Test 3: a slow reader behind a queue of four.
        private static void Limit(PipeReader<Note> reader)
        {
            using (reader)
            {
                int count = 0;
                bool ordered = true;
                long dropped = 0;
                Region<Note> region;
                while ((region = reader.Receive()) != null)
                {
                    using (region)
                    {
                        ordered &= region.Root.Seq == count++;
                        dropped += region.DroppedBefore;
                    }
                    Scheduler.Sleep(3);
                }
                Say($"limit: {count} in order {ordered}, dropped {dropped}, end {(int)reader.Status}");
                Finish(3, count, ordered && dropped == 0, reader.Status, dropped);
            }
        }

        // Test 6: the echo comes back moved, changed and sorted.
        private static void EchoBack(PipeReader<EchoMessage> reader)
        {
            using (reader)
            {
                int count = 0;
                bool ok = false;
                Region<EchoMessage> region;
                while ((region = reader.Receive()) != null)
                {
                    using (region)
                    {
                        EchoMessage m = region.Root;
                        ok = m.Value == 42 && m.Tag == "echo" && m.Items != null && m.Items.Length == EchoItems;
                        for (int i = 0; ok && i < m.Items.Length; i++)
                            ok = m.Items[i].Key == 2 * i + 1 && m.Items[i].Name == "k" + m.Items[i].Key.ToString();
                        count++;
                    }
                }
                Say($"echo back: {count} message(s), value and order {(ok ? "as sent back" : "WRONG")}, end {(int)reader.Status}");
                Finish(6, count, ok, reader.Status, 0);
            }
        }

        // Tests 15-17: std types the app sends and the kernel never declared.
        // A string, a byte array and an Expando are in every image's catalog
        // under one key, so a typed reader on each side just works.
        private static void StdText(PipeReader<string> reader)
        {
            using (reader)
            {
                int count = 0;
                bool ok = true;
                Region<string> region;
                while ((region = reader.Receive()) != null)
                {
                    using (region)
                        ok &= region.Root == StdProbe.Text(count++);
                }
                Say($"std text: {count} string(s), {(ok ? "as sent" : "WRONG")}, end {(int)reader.Status}");
                Finish(15, count, ok, reader.Status, 0);
            }
        }

        private static void StdBytes(PipeReader<byte[]> reader)
        {
            using (reader)
            {
                int count = 0;
                bool ok = true;
                Region<byte[]> region;
                while ((region = reader.Receive()) != null)
                {
                    using (region)
                        ok &= StdProbe.BytesOk(region.Root, StdProbe.ByteLength(count++));
                }
                Say($"std bytes: {count} array(s), {(ok ? "as sent" : "WRONG")}, end {(int)reader.Status}");
                Finish(16, count, ok, reader.Status, 0);
            }
        }

        private static void StdExpando(PipeReader<Expando> reader)
        {
            using (reader)
            {
                int count = 0;
                string inPlace = null, copied = null;
                Region<Expando> region;
                while ((region = reader.Receive()) != null)
                {
                    using (region)
                    {
                        inPlace ??= StdProbe.Check(region.Root, "app");
                        copied ??= StdProbe.Check(region.ToHeap(), "app");
                        if (count == 0) Say("std expando: " + region.Root.ToString());
                        count++;
                    }
                }
                Say($"std expando: {count} received; in place {inPlace ?? "ok"}, copied {copied ?? "ok"}, end {(int)reader.Status}");
                Finish(17, count, inPlace == null && copied == null, reader.Status, 0);
            }
        }

        // ---- writers: in the service call ----

        private static int Feed(int count)
        {
            if (PipeWriter<Bag>.Connect("probe.feed", out PipeWriter<Bag> writer, out string error) != PipeStatus.Ok)
                return (int)PipeStatus.Refused;
            using (writer)
            {
                RegionProbeGraph.Declare();
                for (int i = 0; i < count; i++)
                {
                    PipeStatus status = writer.Copy((Bag)RegionProbeGraph.Build(RegionProbeGraph.Numbers));
                    if (status != PipeStatus.Ok) return (int)status;
                }
            }
            return 0;
        }

        private static int Flood(int count, ulong* answer)
        {
            if (PipeWriter<Note>.Connect("probe.flood", out PipeWriter<Note> writer, out string error) != PipeStatus.Ok)
                return (int)PipeStatus.Refused;
            int sent = 0;
            using (writer)
            {
                for (int i = 0; i < count; i++)
                {
                    if (writer.Copy(new Note { Seq = i, Text = "flood" }) == PipeStatus.Ok)
                        sent++;
                }
            }
            if (answer != null) answer[0] = (ulong)sent;
            return 0;
        }

        private const int EchoItems = 100;

        private static int EchoOut()
        {
            if (PipeWriter<EchoMessage>.Connect("probe.echo.out", out PipeWriter<EchoMessage> writer, out string error) != PipeStatus.Ok)
                return (int)PipeStatus.Refused;
            using (writer)
            {
                // 100 odd keys out of order: past the 16 elements below which
                // Array.Sort is an insertion sort, so the app's sort in the
                // region runs partitions and swaps as well.
                var items = new Item[EchoItems];
                for (int i = 0; i < items.Length; i++)
                {
                    int key = 2 * (i * 37 % EchoItems) + 1;
                    items[i] = new Item { Key = key, Name = "k" + key.ToString() };
                }
                return (int)writer.Copy(new EchoMessage { Value = 41, Tag = "echo", Items = items });
            }
        }

        // The other direction of tests 15-17: an Expando and a string the
        // kernel builds, for the app's typed readers.
        private static int StdOut()
        {
            if (PipeWriter<Expando>.Connect("probe.std.out", out PipeWriter<Expando> expando, out _) != PipeStatus.Ok)
                return (int)PipeStatus.Refused;
            using (expando)
            {
                PipeStatus s = expando.Copy(StdProbe.Sample("kernel"));
                if (s != PipeStatus.Ok) return (int)s;
            }
            if (PipeWriter<string>.Connect("probe.std.textout", out PipeWriter<string> text, out _) != PipeStatus.Ok)
                return (int)PipeStatus.Refused;
            using (text)
                return (int)text.Copy(StdProbe.Text(1));
        }

        // Test 9: pending typed readers the app's writer meets.
        private static int Typed(bool on, ulong* answer)
        {
            if (!on)
            {
                if (s_typedLayout != 0) KernelPipes.Close(ExchangeHeap.OwnerKernel, s_typedLayout);
                if (s_typedOther != 0) KernelPipes.Close(ExchangeHeap.OwnerKernel, s_typedOther);
                s_typedLayout = s_typedOther = 0;
                return 0;
            }
            PipeStatus a = PipeReader<SharpOS.Probe.Versioned>.Connect("probe.typed.layout", out PipeReader<SharpOS.Probe.Versioned> layout, out _);
            PipeStatus b = PipeReader<SharpOS.Probe.Different>.Connect("probe.typed.other", out PipeReader<SharpOS.Probe.Different> other, out _);
            s_typedLayout = layout == null ? 0 : layout.Handle;
            s_typedOther = other == null ? 0 : other.Handle;
            return a == PipeStatus.Ok && b == PipeStatus.Ok ? 0 : (int)PipeStatus.Refused;
        }

        // ---- the kernel collector under the exchange ----

        private static int GcLoad(bool on)
        {
            if (!on)
            {
                s_gcLoad = false;
                while (s_gcThreads > 0) Scheduler.Yield();
                return 0;
            }
            if (s_gcLoad) return 0;
            s_gcLoad = true;
            s_gcThreads = 1;
            delegate* unmanaged<void> entry = &GcEntry;
            if (Scheduler.Spawn(entry, 32 * 1024) == null)
            {
                s_gcLoad = false;
                s_gcThreads = 0;
                return (int)PipeStatus.NoMemory;
            }
            return 0;
        }

        [UnmanagedCallersOnly]
        private static void GcEntry()
        {
            int n = 0;
            while (s_gcLoad)
            {
                string garbage = new string('p', 16 + n % 32);
                if (garbage.Length > 0 && (n++ & 7) == 0)
                    KernelGC.Collect();
                Scheduler.Sleep(2);
            }
            Preemption.Suppress();
            s_gcThreads--;
            Preemption.Allow();
            Scheduler.Exit();
        }

        // ---- helpers ----

        private static void Finish(int test, int count, bool ok, PipeStatus status, long extra)
        {
            Preemption.Suppress();
            s_verdicts[test] = new Verdict { Done = 1, Count = count, Ok = ok ? 1 : 0, Status = (int)status, Extra = extra };
            Preemption.Allow();
        }

        // A field of the root record by the pipe's description.
        private static int ReadInt(RawRegion region, string field)
        {
            if (!FieldAt(region, field, out ulong at)) return int.MinValue;
            return *(int*)at;
        }

        private static string ReadString(RawRegion region, string field)
        {
            if (!FieldAt(region, field, out ulong at)) return null;
            ulong offset = *(ulong*)at;
            if (offset == 0 || offset >= region.Length) return null;
            ulong text = (ulong)region.Block + offset;
            int length = *(int*)(text + 8);
            return new string(new ReadOnlySpan<char>((void*)(text + 12), length));
        }

        private static bool FieldAt(RawRegion region, string field, out ulong at)
        {
            at = 0;
            Dictionary<ulong, TypeKeys.Description> types = RegionSchema.Parse(region.Schema, out _);
            ulong root = (ulong)region.Block + (ulong)Region.HeaderSize;
            if (types == null || !types.TryGetValue(*(ulong*)root, out TypeKeys.Description d)) return false;
            foreach (TypeKeys.Field f in d.Fields)
                if (f.Name == field)
                {
                    at = root + (ulong)f.Offset;
                    return true;
                }
            return false;
        }

        private static void Say(string line) => Console.WriteLine("[pipe] " + line);
    }
}
