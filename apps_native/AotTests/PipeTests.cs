using System;
using System.Collections.Generic;
using System.Diagnostics;
using SharpOS.AppSdk;
using SharpOS.Std.Exchange;
using SharpOS.Std.Exchange.Probe;
using SharpOS.Std.Pipes;
using SharpOS.Std.Pipes.Probe;

namespace AotTests
{
    [Message]
    public enum Shade { Pale = 1, Deep = 2, Dark = 3 }

    /// <summary>An array of an enum as a field: registered under its own name, not its underlying type's.</summary>
    [Message]
    public sealed class Shades
    {
        public Shade One;
        public Shade[] Many;
    }

    /// <summary>The journal entry only this app knows: the kernel reads it by the description.</summary>
    [Message]
    public sealed class JournalEntry
    {
        public int Seq;
        public string Text;
        public long Stamp;
        public int[] Values;
    }
}

namespace AotTests
{
    /// <summary>A report only the app has: the kernel reads it through a view (ViewProbe).</summary>
    [Message]
    public sealed class AppReport
    {
        public byte B;
        public sbyte SB;
        public short S;
        public ushort US;
        public int I;
        public uint UI;
        public long L;
        public ulong UL;
        public float F;
        public double D;
        public bool Flag;
        public char C;
        public string Text;
        public ProbeMood Mood;
        public ProbeMood[] Moods;
        public AppPlace Where;
        public AppPlace Nowhere;
        public int[] Numbers;
        public string[] Words;
        public AppPlace[] Places;
        public System.DateTime When;
        public AppReport Next;
    }

    [Message]
    public sealed class AppPlace
    {
        public string Name;
        public int Floor;
    }

    /// <summary>
    /// What Into lays the kernel's report into: fewer fields, another order,
    /// numbers widened (int to long, float to double, int[] to long[]), a field
    /// the source lacks, nesting, a cycle.
    /// </summary>
    [Message]
    public sealed class ReportLite
    {
        public string Text;
        public long I;
        public double F;
        public PlaceLite Where;
        public long[] Numbers;
        public ReportLite Next;
        public string Missing;
        public ProbeMood Mood;
        public System.DateTime When;
        public ProbeMood[] Moods;
    }

    [Message]
    public sealed class PlaceLite
    {
        public int Floor;
        public string Name;
    }

    /// <summary>A field whose type cannot take the source's: I is an int there.</summary>
    [Message]
    public sealed class IntoBad
    {
        public string I;
    }
}

namespace SharpOS.Probe
{
    /// <summary>The app's version of the nested-type test: the kernel's Inner has an int.</summary>
    [Message]
    public sealed class Outer
    {
        public Inner I;
        public int Tag;
    }

    [Message]
    public sealed class Inner
    {
        public long A;
    }
}

namespace SharpOS.Probe
{
    /// <summary>The kernel has a Versioned too, with A a long: the connection must be refused.</summary>
    [Message]
    public sealed class Versioned
    {
        public int A;
        public int B;
    }
}

namespace AotTests
{
    // Native pipe tests, app side (pipe spec, "родная труба"; the kernel side
    // is OS.Kernel.Diagnostics.PipeProbe). The main thread, preemption on, the
    // kernel's collector loaded and this app collecting on a task while the
    // pipes run. Every test ends with the exchange heap where it began.
    internal static unsafe partial class AppEntry
    {
        private static volatile int s_pipeLoad;
        private static volatile int s_pipeLoadRunning;
        private static volatile int s_pipeCollections;
        private static int s_pipeCollectMask = 15;
        private static volatile int s_pipeBroken;

        /// <summary>
        /// --pipe-stress N: tests 1–6 N times under a collector that runs on
        /// every load iteration, the heap and catalog checked around each
        /// test, with the heap walked before every mark and after every sweep.
        /// Exit: 0 clean, 1 a detector fired.
        /// </summary>
        private static int RunPipeStress(int rounds)
        {
            // The battery installs the task backend in its threads section; a
            // run that starts here would have Task.Run do nothing at all.
            SharpOS.AppSdk.TaskBackendInstaller.Install();
            SharpOS.Std.NoRuntime.GcStress.VerifyHeap = true;
            MessageCatalog.Ensure();
            nint statics = TypeKeys.StaticsAddress;
            AppHost.WriteString("[stress] TypeKeys statics at 0x" + ((ulong)statics).ToString("x") + " rooted "
                                + (SharpOS.Std.NoRuntime.GcRoots.CoversAddress(statics) ? "yes" : "NO") + ", slots "
                                + SharpOS.Std.NoRuntime.GcRoots.Count.ToString() + "\n");
            s_pipeCollectMask = 3;
            s_stress = true;
            for (int i = 0; i < rounds && s_pipeBroken == 0; i++)
            {
                s_round = i;
                ulong* a = stackalloc ulong[6];
                Probe(6, 0, a);
                AppHost.WriteString("[stress] round " + i.ToString() + " blocks " + a[0].ToString() + " pipes "
                                    + a[1].ToString() + " carved pages " + a[2].ToString() + " threads " + a[3].ToString()
                                    + " runnable " + a[4].ToString() + " kernel " + a[5].ToString() + "\n");
                CheckPipes();
            }
            AppHost.WriteString(s_pipeBroken == 0 ? "[stress] clean\n" : "[stress] BROKEN\n");
            return s_pipeBroken == 0 ? 0 : 1;
        }

        private static bool s_stress;
        private static int s_round;

        private static int Probe(int op, ulong argument, ulong* answer)
            => ((delegate* unmanaged<int, ulong, ulong*, int>)AppRuntime.Services->PipeProbeAddress)(op, argument, answer);

        private static ulong LiveBlocks()
        {
            ulong* answer = stackalloc ulong[6];
            Probe(6, 0, answer);
            return answer[0];
        }

        // done, count, ok, status, extra; waits until the kernel thread is done.
        private static bool Verdict(int test, out int count, out bool ok, out PipeStatus status, out long extra)
        {
            ulong* v = stackalloc ulong[5];
            var watch = Stopwatch.StartNew();
            do
            {
                Probe(2, (ulong)test, v);
                if (v[0] != 0) break;
                System.Threading.Thread.Sleep(5);
            } while (watch.ElapsedMilliseconds < (SlowRun ? 300_000 : 10_000));
            count = (int)v[1];
            ok = v[2] != 0;
            status = (PipeStatus)(int)(long)v[3];
            extra = (long)v[4];
            return v[0] != 0;
        }

        private static void CheckPipes()
        {
            bool offered = AppRuntime.Services->PipeProbeAddress != 0 && AppRuntime.Services->PipeSendAddress != 0;
            Check("pipe: services offered", offered);
            if (!offered) return;

            // Collectors on both sides for the whole exchange.
            SharpOS.Std.NoRuntime.GcSweep.PoisonFreed = !s_stress;
            Probe(7, 1, null);
            s_pipeLoad = 1;
            s_pipeLoadRunning = 1;
            System.Threading.Tasks.Task.Run(() =>
            {
                if (s_stress) AppHost.WriteString("[stress] load task started" + "\n");
                int n = 0;
                while (s_pipeLoad != 0)
                {
                    byte[] garbage = new byte[64 + n++ % 128];
                    if (garbage.Length > 0 && (n & s_pipeCollectMask) == 0)
                    {
                        if (s_stress && s_pipeCollections < 3) AppHost.WriteString("[stress] load collect" + "\n");
                        GC.Collect();
                        s_pipeCollections++;
                    }
                    System.Threading.Thread.Sleep(1);
                }
                if (s_stress) AppHost.WriteString("[stress] load task done, collections " + s_pipeCollections.ToString() + "\n");
                s_pipeLoadRunning = 0;
            });

            Clean("1 journal", PipeJournal);
            Clean("2 writer death", PipeDeath);
            Clean("3 limit", PipeLimit);
            Clean("4 kernel to app", PipeFeed);
            Clean("5 losses", PipeLosses);
            Clean("6 echo", PipeEcho);
            Clean("7 byref", PipeByRef);
            Clean("12 std types", PipeStdTypes);
            Clean("13 views", PipeViews);
            Clean("14 spoiled blocks", PipeBadBlocks);
            Clean("15 loops", PipeLoops);
            Clean("16 regions", PipeRegions);
            Clean("17 nested type", PipeNested);
            Clean("18 expando graph", PipeExpandoGraph);
            Clean("19 devirtualization", PipeDevirt);

            if (!s_stress)
            {
                Clean("7 barrier", PipeBarrier);
                Clean("8 wrapper", PipeWrapper);
                Clean("9 connection", PipeConnection);
                Clean("10 refusal", PipeRefusal);
                Clean("11 keys", PipeKeys);
            }

            s_pipeLoad = 0;
            var wait = Stopwatch.StartNew();
            while (s_pipeLoadRunning != 0 && wait.ElapsedMilliseconds < (SlowRun ? 60000 : 2000))
                System.Threading.Thread.Sleep(1);
            Probe(7, 0, null);
            Check("pipe: collections ran in the app during the exchange", s_pipeCollections > 0);

            // Timings mean nothing with a collection per allocation.
            if (!s_stress && !SlowRun)
                PipeBench();
        }

        // Test 13: whatever a test took from the exchange heap, it gave back.
        private static void Clean(string name, Action test)
        {
            if (s_pipeBroken != 0) return;
            ulong before = LiveBlocks();
            CatalogWhole("before " + name);
            HeapWhole("before " + name);
            test();
            CatalogWhole("after " + name);
            HeapWhole("after " + name);
            ulong after = LiveBlocks();
            if (after != before)
                AppHost.WriteString("[pipe] " + name + ": blocks " + before.ToString() + " -> " + after.ToString() + "\n");
            Check("pipe " + name + ": exchange blocks back where they were", after == before);
        }

        private static void CatalogWhole(string when)
        {
            int bad = MessageCatalog.Verify(out ulong address, out ulong word);
            if (bad >= 0)
                s_pipeBroken = 1;
            if (bad >= 0)
                AppHost.WriteString("[pipe] CATALOG BROKEN " + when + ": entry " + bad.ToString() + " at 0x"
                                    + address.ToString("x") + " table 0x" + word.ToString("x") + "\n");
        }

        // Segment by segment, object by object, as the sweep walks; the first
        // object without a table of this image or with an impossible size.
        private static void HeapWhole(string when)
        {
            AppPreemption.Suppress();
            ulong bad = 0, word = 0, prev = 0, prevMt = 0;
            uint objects = 0, prevSize = 0;
            try
            {
                var seg = SharpOS.Std.NoRuntime.GcHeap.FirstSegment;
                ulong low = (ulong)SharpOS.Std.NoRuntime.GcMark.MethodTableLow;
                ulong high = (ulong)SharpOS.Std.NoRuntime.GcMark.MethodTableHigh;
                while (seg != null && bad == 0)
                {
                    nint p = seg->ObjectStart;
                    nint end = seg->Current;
                    while (p < end)
                    {
                        var o = (SharpOS.Std.NoRuntime.GcObject*)p;
                        ulong mt = (ulong)o->MethodTable;
                        bool free = o->MethodTable == SharpOS.Std.NoRuntime.GcSweep.FreeObjectMt;
                        if (mt == 0 || (!free && (mt < low || mt >= high)))
                        {
                            bad = (ulong)p;
                            word = mt;
                            break;
                        }
                        uint size = o->ComputeSize();
                        if (size < 16 || size > 64 * 1024 * 1024)
                        {
                            bad = (ulong)p;
                            word = mt;
                            break;
                        }
                        prev = (ulong)p;
                        prevMt = mt;
                        prevSize = size;
                        p += (nint)((size + 15u) & ~15u);
                        objects++;
                    }
                    if (bad == 0 && p != end) { bad = (ulong)p; word = 1; }
                    seg = seg->Next;
                }
            }
            finally
            {
                AppPreemption.Allow();
            }
            if (bad != 0)
                s_pipeBroken = 1;
            if (SharpOS.Std.NoRuntime.GcStress.FirstBroken != 0)
                AppHost.WriteString("[pipe] collector check: first broken 0x" + ((ulong)SharpOS.Std.NoRuntime.GcStress.FirstBroken).ToString("x")
                                    + (SharpOS.Std.NoRuntime.GcStress.BrokenPhase == 1 ? " found BEFORE a mark (the program wrote it)"
                                                              : " found AFTER a sweep (the collector wrote it)") + "\n");
            if (bad != 0)
            {
                AppHost.WriteString("[pipe] HEAP BROKEN " + when + ": object 0x" + bad.ToString("x") + " table 0x"
                                    + word.ToString("x") + " after " + objects.ToString() + " objects\n");
                AppHost.WriteString("[pipe]   words: " + (*(ulong*)bad).ToString("x") + " " + (*(ulong*)(bad + 8)).ToString("x")
                                    + " " + (*(ulong*)(bad + 16)).ToString("x") + " " + (*(ulong*)(bad + 24)).ToString("x")
                                    + "; previous 0x" + prev.ToString("x") + " table 0x" + prevMt.ToString("x")
                                    + " size " + prevSize.ToString() + " free table 0x"
                                    + ((ulong)SharpOS.Std.NoRuntime.GcSweep.FreeObjectMt).ToString("x") + "\n");
            }
        }

        private static void PipeJournal()
        {
            Probe(1, 1, null);
            PipeStatus status = PipeWriter<JournalEntry>.Connect("probe.journal", out PipeWriter<JournalEntry> writer, out string error);
            Check("pipe 1: journal connected", status == PipeStatus.Ok);
            if (writer == null) return;
            int sent = 0;
            PipeStatus firstBad = PipeStatus.Ok;
            for (int i = 0; i < 40; i++)
            {
                var entry = new JournalEntry { Seq = i, Text = "entry " + i.ToString(), Stamp = i * 1000L, Values = new[] { i, 2 * i } };
                PipeStatus s = writer.Copy(entry);
                if (s == PipeStatus.Ok) sent++;
                else if (firstBad == PipeStatus.Ok) firstBad = s;
            }
            writer.Dispose();
            bool done = Verdict(1, out int count, out bool ok, out PipeStatus end, out _);
            if (!(done && sent == 40 && count == 40 && ok && end == PipeStatus.EndOfStream))
            {
                ulong* blocks = stackalloc ulong[6];
                Probe(6, 0, blocks);
                AppHost.WriteString("[pipe] journal: done " + (done ? "1" : "0") + " sent " + sent.ToString() + " first refusal "
                                    + ((int)firstBad).ToString() + " " + (writer.LastError ?? "") + " count " + count.ToString()
                                    + " ok " + (ok ? "1" : "0") + " end " + ((int)end).ToString() + " blocks " + blocks[0].ToString()
                                    + " carved pages " + blocks[2].ToString() + "\n");
            }
            Check("pipe 1: the kernel printed 40 entries of a type it lacks, content by name matches, then end of stream",
                  done && sent == 40 && count == 40 && ok && end == PipeStatus.EndOfStream);
        }

        // Test 2: the runs with --pipe-writer-dies already happened (autorun);
        // their verdicts stay in the kernel.
        private static void PipeDeath()
        {
            bool normal = Verdict(21, out int c1, out bool o1, out PipeStatus e1, out _);
            bool crash = Verdict(22, out int c2, out bool o2, out PipeStatus e2, out _);
            Check("pipe 2: normal exit with 10 queued: all 10 drained, then end of stream",
                  normal && c1 == 10 && o1 && e1 == PipeStatus.EndOfStream);
            Check("pipe 2: exit 134 with 10 queued: all 10 drained, then broken",
                  crash && c2 == 10 && o2 && e2 == PipeStatus.Broken);
        }

        /// <summary>--pipe-writer-dies normal|crash: queue ten notes and end without closing.</summary>
        private static int RunWriterDies(bool crash)
        {
            Probe(1, crash ? 22UL : 21UL, null);
            if (PipeWriter<Note>.Connect(crash ? "probe.death.crash" : "probe.death.normal",
                                         out PipeWriter<Note> writer, out _) != PipeStatus.Ok)
                return 3;
            for (int i = 0; i < 10; i++)
                writer.Copy(new Note { Seq = i, Text = "last words" });
            if (crash)
                throw new InvalidOperationException("the writer dies with its queue full (pipe test 2)");
            return 0;
        }

        private static void PipeLimit()
        {
            Probe(1, 3, null);
            if (PipeWriter<Note>.Connect("probe.limit", out PipeWriter<Note> writer, out _, capacity: 4) != PipeStatus.Ok)
            {
                Check("pipe 3: limit connected", false);
                return;
            }
            int waited = 0;
            int sent = 0;
            for (int i = 0; i < 60; i++)
            {
                var watch = Stopwatch.StartNew();
                if (writer.Copy(new Note { Seq = i, Text = "limited" }) == PipeStatus.Ok) sent++;
                if (watch.ElapsedMilliseconds >= 1) waited++;
            }
            writer.Dispose();
            bool done = Verdict(3, out int count, out bool ok, out PipeStatus end, out long dropped);
            AppHost.WriteString("[pipe] limit: " + waited.ToString() + " of 60 sends waited\n");
            Check("pipe 3: the writer waited at the limit, nothing lost, order kept",
                  done && sent == 60 && waited > 0 && count == 60 && ok && dropped == 0 && end == PipeStatus.EndOfStream);
        }

        private static void PipeFeed()
        {
            if (PipeReader<Bag>.Connect("probe.feed", out PipeReader<Bag> reader, out _) != PipeStatus.Ok)
            {
                Check("pipe 4: feed connected", false);
                return;
            }
            int fed = Probe(3, 3, null);
            bool read = true;
            Bag copy = null;
            int received = 0;
            Region<Bag> region;
            while ((region = reader.Receive()) != null)
            {
                read &= RegionProbeGraph.Check(region.Root, RegionProbeGraph.Numbers, null) == 0;
                if (copy == null)
                    copy = region.ToHeap();
                region.Dispose();
                received++;
            }
            GC.Collect();
            Check("pipe 4: three graphs from the kernel read in place (virtual, interface calls), then end of stream",
                  fed == 0 && received == 3 && read && reader.Status == PipeStatus.EndOfStream);
            Check("pipe 4: ToHeap copy survives Dispose and a collection",
                  copy != null && RegionProbeGraph.Check(copy, RegionProbeGraph.Numbers, null) == 0);
            reader.Dispose();
        }

        private static void PipeLosses()
        {
            LossesFor(PipeOverflow.DropOldest);
            LossesFor(PipeOverflow.DropNewest);
        }

        private static void LossesFor(PipeOverflow policy)
        {
            string name = policy == PipeOverflow.DropOldest ? "oldest" : "newest";
            if (PipeReader<Note>.Connect("probe.flood", out PipeReader<Note> reader, out _, capacity: 8, overflow: policy) != PipeStatus.Ok)
            {
                Check("pipe 5: flood connected (" + name + ")", false);
                return;
            }
            ulong* answer = stackalloc ulong[1];
            Probe(4, 50, answer);
            int received = 0;
            long dropped = 0;
            int first = -1, last = -1;
            Region<Note> region;
            while ((region = reader.Receive()) != null)
            {
                using (region)
                {
                    dropped += region.DroppedBefore;
                    if (first < 0) first = region.Root.Seq;
                    last = region.Root.Seq;
                    received++;
                }
            }
            dropped += reader.DroppedAtEnd;
            AppHost.WriteString("[pipe] losses (" + name + "): sent " + answer[0].ToString() + ", received "
                                + received.ToString() + ", dropped " + dropped.ToString() + ", seq "
                                + first.ToString() + ".." + last.ToString() + "\n");
            bool window = policy == PipeOverflow.DropOldest ? first == 42 && last == 49 : first == 0 && last == 7;
            Check("pipe 5: drop " + name + ": received + dropped = sent, the right eight kept",
                  answer[0] == 50 && received == 8 && received + dropped == 50 && window);
            reader.Dispose();
        }

        private static void PipeEcho()
        {
            Probe(1, 6, null);
            PipeReader<EchoMessage>.Connect("probe.echo.out", out PipeReader<EchoMessage> input, out _);
            PipeWriter<EchoMessage>.Connect("probe.echo.back", out PipeWriter<EchoMessage> output, out _);
            if (input == null || output == null)
            {
                Check("pipe 6: echo connected", false);
                input?.Dispose();
                output?.Dispose();
                return;
            }
            Probe(5, 0, null);
            Region<EchoMessage> region = input.Receive();
            PipeStatus moved = PipeStatus.Refused;
            if (region != null)
            {
                EchoMessage m = region.Root;
                m.Value += 1;
                Array.Sort(m.Items, (a, b) => a.Key.CompareTo(b.Key));
                moved = output.Move(region);
                if (moved != PipeStatus.Ok)
                {
                    AppHost.WriteString("[pipe] echo move: " + output.LastError + "\n");
                    region.Dispose();
                }
            }
            output.Dispose();
            input.Dispose();
            bool done = Verdict(6, out int count, out bool ok, out PipeStatus end, out _);
            Check("pipe 6: kernel -> app -> kernel by Move; value changed and 100 objects sorted in place",
                  moved == PipeStatus.Ok && done && count == 1 && ok && end == PipeStatus.EndOfStream);
        }

        private static EchoMessage EchoSample(string tag)
        {
            var items = new Item[4];
            for (int i = 0; i < items.Length; i++)
                items[i] = new Item { Key = i, Name = tag + i.ToString() };
            return new EchoMessage { Value = 7, Tag = tag, Items = items };
        }

        private static bool Throws(Action write)
        {
            try { write(); }
            catch (RegionReferenceException) { return true; }
            return false;
        }

        // The misuse is the point here: each store is wrapped in a lambda so
        // the test can catch what the barrier throws, and the analyzer would
        // rightly refuse the capture (SOSR002).
#pragma warning disable SOSR002, SOSR006
        private static void PipeBarrier()
        {
            Pipe.Create<EchoMessage>(4, PipeOverflow.DropOldest, out PipeWriter<EchoMessage> w, out PipeReader<EchoMessage> r);
            w.Copy(EchoSample("a"));
            w.Copy(EchoSample("b"));
            w.Copy(EchoSample("c"));
            Region<EchoMessage> one = r.Receive();
            Region<EchoMessage> two = r.Receive();
            Region<EchoMessage> three = r.Receive();
            EchoMessage m = one.Root;
            EchoMessage other = two.Root;

            m.Value = 99;
            Check("pipe 7: values in a region can change", m.Value == 99);
            Check("pipe 7: a heap object into a field is refused", Throws(() => m.Tag = new string('h', 3)));
            Check("pipe 7: another region's object into a field is refused", Throws(() => m.Tag = other.Tag));
            Check("pipe 7: a heap object into an element is refused", Throws(() => m.Items[0] = new Item()));
            Check("pipe 7: another region's object into an element is refused", Throws(() => m.Items[0] = other.Items[1]));
            bool intact = m.Tag == "a" && m.Items[0].Name == "a0" && m.Items[3].Name == "a3"
                          && RegionProbeGraphIntact(m);
            Check("pipe 7: the region intact after the refusals", intact);
            bool allowed = !Throws(() => { m.Items[0] = m.Items[3]; m.Tag = null; m.Items[1] = null; });
            Check("pipe 7: null and an object of the same block are allowed",
                  allowed && m.Tag == null && ReferenceEquals(m.Items[0], m.Items[3]));

            // A literal is a frozen object of the image, and ILC stores one
            // with a plain move — no barrier call to refuse it. The region then
            // refers outside its block, which the reverse pass refuses: the
            // reference cannot leave this image.
            bool literalAtStore = Throws(() => LiteralInto(three.Root));
            PipeStatus literalMoved = w.Move(three);
            AppHost.WriteString("[pipe] literal into a region: " + (literalAtStore ? "refused at the store" : "stored")
                                + "; Move: " + ((int)literalMoved).ToString() + " " + (w.LastError ?? "") + "\n");
            Check("pipe 7: a literal into a field is refused (at the store, or at Move when ILC stores it without a barrier)",
                  literalAtStore || (literalMoved == PipeStatus.Refused && w.LastError != null && w.LastError.Contains("outside the block")));

            one.Dispose();
            two.Dispose();
            if (literalMoved != PipeStatus.Ok) three.Dispose();
            while (r.TryReceive(out Region<EchoMessage> left) == PipeStatus.Ok) left.Dispose();
            w.Dispose();
            r.Dispose();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void LiteralInto(EchoMessage m) => m.Tag = "literal";

#pragma warning restore SOSR002, SOSR006

        // Struct elements copied inside a region: the compiler copies the
        // reference with RhpByRefAssignRef, whose shellcode carries the
        // barrier. The same code runs in the kernel on its own pipe.
        private static void PipeByRef()
        {
            int app = ByRefProbe.RunOnPipe();
            int kernel = Probe(12, 0, null);
            Check("pipe 7: a struct copied between elements of a region (RhpByRefAssignRef) passes inside the block",
                  app > 0 && (app & ByRefProbe.InsidePassed) != 0);
            Check("pipe 7: a struct carrying a heap reference into a region element is refused, the reference unchanged",
                  app > 0 && (app & ByRefProbe.OutsideRefused) != 0);
            Check("pipe 7: the same two copies in the kernel",
                  kernel == (ByRefProbe.InsidePassed | ByRefProbe.OutsideRefused));
        }

        // Test 12: std types as messages. A string, a byte array and an
        // Expando carrying every std structure: in the app on both ends, then
        // to the kernel and back, neither side declaring anything.
        private static void PipeStdTypes()
        {
            Pipe.Create<string>(4, PipeOverflow.DropOldest, out PipeWriter<string> tw, out PipeReader<string> tr);
            tw.Copy(StdProbe.Text(1));
            bool textOk;
            using (Region<string> r = tr.Receive())
                textOk = r != null && r.Root == StdProbe.Text(1);
            tw.Dispose();
            tr.Dispose();
            Check("pipe 12: a string is a message", textOk);

            Pipe.Create<byte[]>(4, PipeOverflow.DropOldest, out PipeWriter<byte[]> bw, out PipeReader<byte[]> br);
            bw.Copy(StdProbe.Bytes(4096));
            bool bytesOk;
            using (Region<byte[]> r = br.Receive())
                bytesOk = r != null && StdProbe.BytesOk(r.Root, 4096);
            bw.Dispose();
            br.Dispose();
            Check("pipe 12: a byte array is a message", bytesOk);

            Pipe.Create<Expando>(4, PipeOverflow.DropOldest, out PipeWriter<Expando> ew, out PipeReader<Expando> er);
            ew.Copy(StdProbe.Sample("app"));
            Region<Expando> region = er.Receive();
            string inPlace = region == null ? "nothing received" : StdProbe.Check(region.Root, "app");
            Expando heap = region?.ToHeap();
            string copied = heap == null ? "no copy" : StdProbe.Check(heap, "app");
            bool refused = false, untouched = false;
            if (region != null)
            {
                try { WriteIntoExpando(region.Root); }
                catch (RegionReferenceException) { refused = true; }
                untouched = region.Root["Count"] is int c && c == 42;
                region.Dispose();
            }
            if (inPlace != null || copied != null)
                AppHost.WriteString("[pipe] std expando: in place " + (inPlace ?? "ok") + ", copied " + (copied ?? "ok") + "\n");
            Check("pipe 12: an Expando with every std structure arrives whole, read in place", inPlace == null);
            Check("pipe 12: ToHeap gives a real Expando", copied == null);
            Check("pipe 12: a new value into a received Expando is refused, the field unchanged", refused && untouched);

            bool resent = false;
            if (heap != null)
            {
                heap["Count"] = 43;
                heap.Remove("Nothing");
                ew.Copy(heap);
                using (Region<Expando> again = er.Receive())
                    resent = again != null && again.Root["Count"] is int n && n == 43 && again.Root.Count == 15;
            }
            ew.Dispose();
            er.Dispose();
            Check("pipe 12: a changed copy goes out again", resent);

            bool outsideRefused = false;
            try { new Expando()["x"] = new object(); }
            catch (ArgumentException) { outsideRefused = true; }
            bool missing = false;
            try { _ = new Expando()["missing"]; }
            catch (System.Collections.Generic.KeyNotFoundException) { missing = true; }
            Check("pipe 12: a value outside the catalog is refused when set; a missing name throws", outsideRefused && missing);

            // App -> kernel: three typed readers there, of types the kernel never declared.
            Probe(1, 15, null);
            Probe(1, 16, null);
            Probe(1, 17, null);
            PipeWriter<string>.Connect("probe.std.text", out PipeWriter<string> kt, out _);
            PipeWriter<byte[]>.Connect("probe.std.bytes", out PipeWriter<byte[]> kb, out _);
            PipeWriter<Expando>.Connect("probe.std.expando", out PipeWriter<Expando> ke, out _);
            if (kt != null) { for (int i = 0; i < StdProbe.Texts; i++) kt.Copy(StdProbe.Text(i)); kt.Dispose(); }
            if (kb != null) { for (int i = 0; i < StdProbe.ByteArrays; i++) kb.Copy(StdProbe.Bytes(StdProbe.ByteLength(i))); kb.Dispose(); }
            if (ke != null) { ke.Copy(StdProbe.Sample("app")); ke.Dispose(); }
            bool t15 = Verdict(15, out int n15, out bool ok15, out PipeStatus e15, out _) && n15 == StdProbe.Texts && ok15 && e15 == PipeStatus.EndOfStream;
            bool t16 = Verdict(16, out int n16, out bool ok16, out PipeStatus e16, out _) && n16 == StdProbe.ByteArrays && ok16 && e16 == PipeStatus.EndOfStream;
            bool t17 = Verdict(17, out int n17, out bool ok17, out PipeStatus e17, out _) && n17 == 1 && ok17 && e17 == PipeStatus.EndOfStream;
            Check("pipe 12: strings, byte arrays and an Expando from the app, read by the kernel's typed readers", t15 && t16 && t17);

            // Kernel -> app.
            PipeReader<Expando>.Connect("probe.std.out", out PipeReader<Expando> fromKernel, out _);
            PipeReader<string>.Connect("probe.std.textout", out PipeReader<string> textFromKernel, out _);
            int sent = Probe(15, 0, null);
            string kernelExpando = "nothing received";
            bool kernelText = false;
            if (fromKernel != null)
            {
                using (Region<Expando> r = fromKernel.Receive())
                    kernelExpando = r == null ? "nothing received" : StdProbe.Check(r.Root, "kernel");
                fromKernel.Dispose();
            }
            if (textFromKernel != null)
            {
                using (Region<string> r = textFromKernel.Receive())
                    kernelText = r != null && r.Root == StdProbe.Text(1);
                textFromKernel.Dispose();
            }
            if (kernelExpando != null)
                AppHost.WriteString("[pipe] std expando from the kernel: " + kernelExpando + "\n");
            Check("pipe 12: an Expando and a string from the kernel", sent == 0 && kernelExpando == null && kernelText);
        }

        // The analyzer refuses this where it sees the region (SOSR006); behind a
        // parameter it cannot, and the barrier does.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void WriteIntoExpando(Expando e) => e["Count"] = 9;

        // ---- step 192: views, Expando, Into, the convenient layer ----

        private static AppReport AppReportSample()
        {
            var r = new AppReport
            {
                B = ViewProbe.B, SB = ViewProbe.SB, S = ViewProbe.S, US = ViewProbe.US, I = ViewProbe.I, UI = ViewProbe.UI,
                L = ViewProbe.L, UL = ViewProbe.UL, F = ViewProbe.F, D = ViewProbe.D, Flag = true, C = ViewProbe.C,
                Text = ViewProbe.Text, Mood = ProbeMood.Deep, Moods = ViewProbe.Moods,
                Where = new AppPlace { Name = "hall", Floor = 3 },
                Numbers = ViewProbe.Numbers, Words = ViewProbe.Words,
                Places = new[] { new AppPlace { Name = "a", Floor = 4 }, new AppPlace { Name = "b", Floor = 5 } },
                When = ViewProbe.When,
            };
            r.Next = r;
            return r;
        }

        private static string CheckLite(ReportLite r)
        {
            if (r == null) return "null";
            if (r.I != ViewProbe.I) return "I";
            if (r.Text != ViewProbe.Text) return "Text";
            if (r.F != 1.5) return "F";
            if (r.Where == null || r.Where.Floor != 3 || r.Where.Name != "hall") return "Where";
            if (r.Numbers == null || r.Numbers.Length != 3 || r.Numbers[2] != 3L) return "Numbers";
            if (!ReferenceEquals(r.Next, r)) return "Next (cycle)";
            if (r.Missing != null) return "Missing";
            if (r.Mood != ProbeMood.Deep) return "Mood";
            if (r.When != ViewProbe.When) return "When";
            if (r.Moods == null || r.Moods.Length != 2 || r.Moods[1] != ProbeMood.Calm) return "Moods";
            return null;
        }

        private static void Report(string what, string bad)
        {
            if (bad != null) AppHost.WriteString("[pipe] " + what + ": " + bad + "\n");
        }

        // Tests 13: views over a class the app does not have; the same over an
        // Expando; ToExpando and Into; a view after its region; values written
        // through a view and the block forwarded; the app's own class and an
        // Expando read by the kernel through views.
#pragma warning disable SOSR002, SOSR003, SOSR006
        private static void PipeViews()
        {
            // The readers first: the kernel's writers then meet them.
            RawPipeReader report = Pipe.Read("probe.view.report");
            RawPipeReader bag = Pipe.Read("probe.view.expando");
            RawPipeReader into = Pipe.Read("probe.view.into");
            RawPipeReader gone = Pipe.Read("probe.view.gone");
            RawPipeReader bench = Pipe.Read("probe.view.bench");
            RawPipeReader edit = Pipe.Read("probe.view.edit");
            Probe(1, 25, null);
            int sent = Probe(16, 0, null);

            string fromKernel = "nothing received", typeName = null, intoOne = "nothing", writes = "nothing";
            bool incompatible = false;
            Expando copy = null;
            foreach (View v in report)
            {
                fromKernel = ViewProbe.Check(v);
                typeName = v.TypeName;
                intoOne = CheckLite(v.Into<ReportLite>());
                try { v.Into<IntoBad>(); }
                catch (InvalidCastException e) { incompatible = e.Message.Contains("'I'"); }
                copy = v;
                writes = WritesRefused(v);
                v["I"] = ViewProbe.I;             // a write on the loop's own variable
            }
            Report("view of the kernel's report", fromKernel);
            Check("pipe 13: a kernel class the app lacks, read through a view: every kind of field",
                  sent == 0 && fromKernel == null && typeName == "SharpOS.Probe.Kernel.KernelReport");

            string fromBag = "nothing received";
            foreach (View v in bag) fromBag = ViewProbe.Check(v);
            Report("view of the kernel's Expando", fromBag);
            Check("pipe 13: the same read from an Expando, by the same code", fromBag == null);

            bool copyOk = copy != null && copy["Where"] is Expando where && where["Floor"] is int floor && floor == 3
                          && ReferenceEquals(copy["Next"], copy)
                          && copy["Places"] is object[] places && places.Length == 2 && places[1] is Expando pb && pb["Floor"] is int pf && pf == 5
                          && copy["Mood"] is int mood && mood == 2
                          && copy["When"] is DateTime when && when == ViewProbe.When
                          && copy["Numbers"] is int[] numbers && numbers[2] == 3
                          && copy["Words"] is string[] words && words[1] == "b"
                          && copy["UL"] is ulong ul && ul == ViewProbe.UL;
            Check("pipe 13: a view into an Expando: a deep copy, the cycle kept, an enum as its number, a DateTime as itself", copyOk);
            Report("Into<ReportLite>", intoOne);
            Check("pipe 13: Into<T> by name: fewer fields, another order, widening, nesting, arrays, a cycle", intoOne == null);
            Check("pipe 13: Into<T> with an incompatible field throws, naming it", incompatible);
            Report("writes through a view", writes);
            Check("pipe 13: a reference, a wrong type, an out-of-range number through a view: refused, explained", writes == null);

            int intoCount = 0;
            string intoAll = null;
            foreach (ReportLite lite in into.Into<ReportLite>())
            {
                intoAll ??= CheckLite(lite);
                intoCount++;
            }
            System.GC.Collect();
            Check("pipe 13: a reader's Into<T>: the app's own objects", intoCount == 2 && intoAll == null);

            // Into with the writer's own class, and ToHeap of a nested object.
            RawPipeReader own = Pipe.Read("app.view.own");
            PipeReader<AppReport> ownTyped = Pipe.Read<AppReport>("app.view.owntyped");
            using (PipeWriter<AppReport> w = Pipe.Write<AppReport>("app.view.own")) w.Copy(AppReportSample());
            using (PipeWriter<AppReport> w = Pipe.Write<AppReport>("app.view.owntyped")) w.Copy(AppReportSample());
            AppReport mine = null;
            foreach (AppReport r in own.Into<AppReport>()) mine = r;
            AppPlace place = null;
            foreach (AppReport r in ownTyped) place = r.Where.ToHeap();
            Churn(32);
            System.GC.Collect();
            Check("pipe 13: Into<T> with the writer's own class: the same objects, in the app's heap",
                  mine != null && mine.I == ViewProbe.I && mine.UL == ViewProbe.UL && mine.Where.Floor == 3
                  && ReferenceEquals(mine.Next, mine) && mine.Places[1].Name == "b" && mine.Mood == ProbeMood.Deep);
            Check("pipe 13: ToHeap of a nested object outlives its step and a collection",
                  place != null && place.Floor == 3 && place.Name == "hall");

            View kept = default;
            bool goneOnStep = false, goneAfter = false;
            int step = 0;
            foreach (View v in gone)
            {
                if (step++ == 0) kept = v;
                else
                {
                    try { _ = kept["I"]; }
                    catch (ObjectDisposedException) { goneOnStep = true; }
                }
            }
            try { _ = kept["I"]; }
            catch (ObjectDisposedException) { goneAfter = true; }
            Check("pipe 13: a view at the next step and after the loop throws ObjectDisposedException", goneOnStep && goneAfter);

            foreach (View v in bench) BenchView(v);

            edit.Where(v =>
            {
                v["I"] = 77;
                View o = v["Where"];
                o["Floor"] = 9;
                v["Next"] = null;
                v["Flag"] = false;
                View numbers = v["Numbers"];
                numbers[0] = 100;
                return true;
            }).WriteTo("probe.view.back");
            bool back = Verdict(25, out int n25, out bool ok25, out PipeStatus e25, out _) && n25 == 1 && ok25 && e25 == PipeStatus.EndOfStream;
            Check("pipe 13: values written through a view, the block itself sent on, read by the kernel", back);

            // The other way: the app's own class and an Expando, read by the kernel.
            Probe(1, 23, null);
            Probe(1, 24, null);
            using (PipeWriter<AppReport> w = Pipe.Write<AppReport>("probe.app.report"))
                w.Copy(AppReportSample());
            using (PipeWriter<Expando> w = Pipe.Write<Expando>("probe.app.expando"))
                w.Copy(ViewProbe.Sample());
            bool t23 = Verdict(23, out int n23, out bool ok23, out PipeStatus e23, out _) && n23 == 1 && ok23 && e23 == PipeStatus.EndOfStream;
            bool t24 = Verdict(24, out int n24, out bool ok24, out PipeStatus e24, out _) && n24 == 1 && ok24 && e24 == PipeStatus.EndOfStream;
            Check("pipe 13: the app's class and an Expando read by the kernel through views", t23 && t24);
        }

        // Null when every write a view must refuse is refused with a reason.
        private static string WritesRefused(View v)
        {
            try { v["Text"] = "x"; return "a string into a reference went through"; }
            catch (InvalidOperationException) { }
            try { v["B"] = 300; return "300 into a byte went through"; }
            catch (OverflowException) { }
            try { v["I"] = 1.5; return "1.5 into an int went through"; }
            catch (InvalidCastException) { }
            try { v["Flag"] = 1; return "1 into a bool went through"; }
            catch (InvalidCastException) { }
            try { v["When"] = 0; return "a number into a struct went through"; }
            catch (InvalidOperationException) { }
            return (byte)v["B"] == ViewProbe.B && v["I"] == ViewProbe.I ? null : "a refused write changed a value";
        }

        // Measured, not asserted: a field through a view, Into, ToExpando — per object.
        private static void BenchView(View v)
        {
            const int Reads = 2000, Copies = 200;
            var watch = Stopwatch.StartNew();
            long sum = 0;
            for (int i = 0; i < Reads; i++) sum += (int)v["I"];
            long readNs = watch.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency / Reads;
            watch.Restart();
            for (int i = 0; i < Copies; i++) v.Into<ReportLite>();
            long intoNs = watch.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency / Copies;
            watch.Restart();
            for (int i = 0; i < Copies; i++) v.ToExpando();
            long expandoNs = watch.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency / Copies;
            AppHost.WriteString("[bench] view: field " + readNs.ToString() + " ns; Into<ReportLite> " + intoNs.ToString()
                                + " ns; ToExpando " + expandoNs.ToString() + " ns per report (22 fields, 5 objects, 4 arrays)"
                                + (sum == (long)ViewProbe.I * Reads ? "" : " SUM WRONG") + "\n");
        }

        // Test 14: spoiled blocks are refused at receive, each with the reason.
        private static void PipeBadBlocks()
        {
            RawPipeReader key = Pipe.Read("probe.bad.key");
            RawPipeReader size = Pipe.Read("probe.bad.size");
            RawPipeReader reference = Pipe.Read("probe.bad.ref");
            int sent = Probe(17, 0, null);
            string a = Refusal(key), b = Refusal(size), c = Refusal(reference);
            AppHost.WriteString("[pipe] spoiled blocks: " + a + " | " + b + " | " + c + "\n");
            Check("pipe 14: a spoiled block — unknown key, size past the end, reference outside — refused at receive",
                  sent == 0 && a != null && a.Contains("not in the pipe's description") && b != null && b.Contains("runs past")
                  && c != null && c.Contains("is not a record"));
        }

        private static string Refusal(RawPipeReader reader)
        {
            try
            {
                foreach (View v in reader) return null;
            }
            catch (PipeException e) when (e.Status == PipeStatus.Refused)
            {
                return e.Message;
            }
            return null;
        }

        // Test 15: the loop lets go of everything — break, an exception in the
        // body, the end, a broken writer.
        private static void PipeLoops()
        {
            ulong* before = stackalloc ulong[6];
            Probe(6, 0, before);

            PipeReader<Note> r = Pipe.Read<Note>("probe.loop.end");
            Probe(18, 0, null);
            int seen = 0;
            foreach (Note n in r)
            {
                seen++;
                if (n.Seq == 1) break;
            }
            Check("pipe 15: break leaves the loop, its reader closed, its blocks back", seen == 2 && Unchanged(before));

            r = Pipe.Read<Note>("probe.loop.end");
            Probe(18, 0, null);
            bool thrown = false;
            try
            {
                foreach (Note n in r)
                    if (n.Seq == 2) throw new InvalidOperationException("the body");
            }
            catch (InvalidOperationException) { thrown = true; }
            Check("pipe 15: an exception in the body: the reader closed, the blocks back", thrown && Unchanged(before));

            r = Pipe.Read<Note>("probe.loop.end");
            Probe(18, 0, null);
            int all = 0;
            bool ordered = true;
            foreach (Note n in r) ordered &= n.Seq == all++ && n.Text == "loop " + n.Seq.ToString();
            Check("pipe 15: the end of the stream ends the loop: all five, in order, nothing lost",
                  all == 5 && ordered && r.Dropped == 0 && Unchanged(before));

            r = Pipe.Read<Note>("probe.loop.broken");
            Probe(18, 1, null);
            int drained = 0;
            PipeStatus broke = PipeStatus.Ok;
            try
            {
                foreach (Note n in r) drained++;
            }
            catch (PipeException e) { broke = e.Status; }
            Check("pipe 15: a broken writer: the queue drained, then PipeException", drained == 5 && broke == PipeStatus.Broken && Unchanged(before));

            // A condition in a loop, and a condition sending what passes on as it is.
            r = Pipe.Read<Note>("probe.loop.end");
            Probe(18, 0, null);
            int odd = 0;
            foreach (Note n in r.Where(n => n.Seq % 2 == 1)) odd += n.Seq;
            PipeReader<Note> evens = Pipe.Read<Note>("app.loop.evens");
            r = Pipe.Read<Note>("probe.loop.end");
            Probe(18, 0, null);
            r.Where(n => n.Seq % 2 == 0).WriteTo("app.loop.evens");
            int even = 0, count = 0;
            foreach (Note n in evens)
            {
                even += n.Seq;
                count++;
            }
            Check("pipe 15: Where in a loop; Where.WriteTo sends what passes on, then ends the output",
                  odd == 1 + 3 && count == 3 && even == 0 + 2 + 4 && Unchanged(before));
        }

        private static bool Unchanged(ulong* before)
        {
            ulong* now = stackalloc ulong[6];
            Probe(6, 0, now);
            bool same = now[0] == before[0] && now[1] == before[1];
            if (!same)
                AppHost.WriteString("[pipe] loop left blocks " + before[0].ToString() + " -> " + now[0].ToString()
                                    + ", pipes " + before[1].ToString() + " -> " + now[1].ToString() + "\n");
            return same;
        }

        // Test 16: a loop over regions — Move, ToHeap, nothing — and copies that
        // outlive their step and a collection.
        private static void PipeRegions()
        {
            var keptRoots = new List<Note>();
            var keptTexts = new List<string>();
            var keptValues = new List<int[]>();
            Pipe.Create<Note>(8, PipeOverflow.DropOldest, out PipeWriter<Note> movedW, out PipeReader<Note> movedR);
            PipeReader<Note> input = Pipe.Read<Note>("probe.loop.regions");
            Probe(18, 2, null);
            foreach (Region<Note> msg in input.Regions)
            {
                int seq = msg.Root.Seq;
                if (seq % 3 == 0) movedW.Move(msg);
                else if (seq % 3 == 1)
                {
                    keptRoots.Add(msg.ToHeap());
                    keptTexts.Add(msg.Root.Text.ToHeap());
                    keptValues.Add(msg.Root.Values.ToHeap());
                }
            }
            Churn(64);
            System.GC.Collect();
            Churn(64);
            bool kept = keptRoots.Count == 2 && keptRoots[0].Seq == 1 && keptRoots[1].Text == "loop 4" && keptRoots[1].Values[1] == 8
                        && keptTexts[0] == "loop 1" && keptTexts[1] == "loop 4" && keptValues[1][0] == 4;
            movedW.Dispose();
            int moved = 0;
            bool movedOk = true;
            foreach (Note n in movedR)
            {
                movedOk &= n.Seq % 3 == 0;
                moved++;
            }
            Check("pipe 16: Regions — two moved on, two copied out, two let go", moved == 2 && movedOk);
            Check("pipe 16: ToHeap of a root, a string and an array outlives its step and a collection", kept);
        }

        // Test 17: a nested type that differs from the writer's: the first such message throws, naming it.
        private static void PipeNested()
        {
            PipeReader<SharpOS.Probe.Outer> r = Pipe.Read<SharpOS.Probe.Outer>("probe.nested");
            Probe(19, 0, null);
            string message = null;
            try
            {
                foreach (SharpOS.Probe.Outer o in r) { }
            }
            catch (PipeException e) { message = e.Message; }
            Report("nested type", message);
            Check("pipe 17: a nested type unlike the writer's: PipeException naming it",
                  message != null && message.Contains("SharpOS.Probe.Inner"));
        }

        // Test 18: an Expando graph — nested, shared, a cycle, arrays — read in
        // place with its class and through a view.
        private static void PipeExpandoGraph()
        {
            var shared = new Expando();
            shared["N"] = 1;
            var root = new Expando();
            root["A"] = shared;
            root["B"] = shared;
            root["Self"] = root;
            root["List"] = new object[] { shared, root };
            root["Ints"] = new[] { 1, 2 };

            PipeReader<Expando> typed = Pipe.Read<Expando>("app.expando.typed");
            RawPipeReader raw = Pipe.Read("app.expando.raw");
            using (PipeWriter<Expando> w = Pipe.Write<Expando>("app.expando.typed")) w.Copy(root);
            using (PipeWriter<Expando> w = Pipe.Write<Expando>("app.expando.raw")) w.Copy(root);

            bool inPlace = false, refused = false, heapOk = false;
            foreach (Expando e in typed)
            {
                inPlace = ReferenceEquals(e["A"], e["B"]) && ReferenceEquals(e["Self"], e)
                          && e["List"] is object[] list && ReferenceEquals(list[1], e) && e["Ints"] is int[] ints && ints[1] == 2;
                try { e["Added"] = 5; }
                catch (RegionReferenceException) { refused = true; }
                Expando h = e.ToHeap();
                heapOk = ReferenceEquals(h["A"], h["B"]) && ReferenceEquals(h["Self"], h) && !ReferenceEquals(h, e);
            }
            bool viewed = false;
            foreach (View v in raw)
            {
                Expando x = v;
                viewed = ReferenceEquals(x["A"], x["B"]) && ReferenceEquals(x["Self"], x)
                         && x["List"] is object[] list && ReferenceEquals(list[1], x) && v["A"]["N"] == 1;
            }
            Check("pipe 18: an Expando graph read in place: shared and cyclic references are the same objects", inPlace);
            Check("pipe 18: writing a new value into a received Expando: RegionReferenceException", refused);
            Check("pipe 18: ToHeap and a view's Expando keep sharing and the cycle", heapOk && viewed);

            var bags = new List<Expando>();
            RawPipeReader bagReader = Pipe.Read("app.expando.bags");
            using (PipeWriter<Expando> w = Pipe.Write<Expando>("app.expando.bags"))
            {
                w.Copy(root);
                w.Copy(shared);
            }
            foreach (Expando x in bagReader) bags.Add(x);
            System.GC.Collect();
            Check("pipe 18: foreach (Expando x in Pipe.Read(...)) — each message a copy of its own",
                  bags.Count == 2 && ReferenceEquals(bags[0]["Self"], bags[0]) && bags[1]["N"] is int one && one == 1);
        }
#pragma warning restore SOSR002, SOSR003, SOSR006

        // Opaque (Program.cs) hides the static type from the compiler: no cast
        // or call below can be decided by what the variable was declared as.
        private static string DescribeDevirt(object o)
        {
            string text = o == null ? "null" : "";
            if (o == null) return text;
            text += "isA=" + (o is DevirtA ? "1" : "0") + " isB=" + (o is DevirtB ? "1" : "0") + " isC=" + (o is DevirtC ? "1" : "0");
            text += " asBase=" + ((o as DevirtBase) != null ? "1" : "0") + " asI=" + ((o as IDevirt) != null ? "1" : "0");
            if (o is DevirtBase b) text += " Value=" + b.Value().ToString() + " Name=" + b.Name();
            if (o is IDevirt i) text += " Kind=" + i.Kind().ToString();
            return text;
        }

        // Test 19: a type this image never constructs, only receives — through
        // a typed pipe and through Into — called through its base, its
        // interface, and tested with is/as. The app constructs only DevirtA.
        private static void PipeDevirt()
        {
            string a = DescribeDevirt(Opaque(new DevirtA { X = 1 }));
            PipeReader<DevirtB> typed = Pipe.Read<DevirtB>("probe.devirt");
            RawPipeReader raw = Pipe.Read("probe.devirt.raw");
            int sent = Probe(20, 0, null);
            string b = "nothing", c = "nothing";
            foreach (DevirtB received in typed) b = DescribeDevirt(Opaque(received));
            foreach (View v in raw) c = DescribeDevirt(Opaque(v.Into<DevirtC>()));
            AppHost.WriteString("[devirt] constructed A: " + a + "\n[devirt] received B: " + b + "\n[devirt] Into C: " + c + "\n");
            Check("pipe 19: a constructed type answers its own (control)",
                  a == "isA=1 isB=0 isC=0 asBase=1 asI=1 Value=101 Name=A Kind=1");
            Check("pipe 19: a type only received through a pipe: is, as, its own virtual and interface methods",
                  sent == 0 && b == "isA=0 isB=1 isC=0 asBase=1 asI=1 Value=207 Name=B Kind=2");
            Check("pipe 19: a type only produced by Into: is, as, its own virtual and interface methods",
                  c == "isA=0 isB=0 isC=1 asBase=1 asI=1 Value=307 Name=C Kind=3");
        }

        private static bool RegionProbeGraphIntact(EchoMessage m)
            => m.Items.Length == 4 && m.Items[1].Key == 1 && m.Items[2].Name == "a2";

        // Reading a region after Dispose and Move is what this test checks at
        // run time; the analyzer refuses it at compile time (SOSR003).
#pragma warning disable SOSR003
        private static void PipeWrapper()
        {
            Pipe.Create<EchoMessage>(4, PipeOverflow.DropOldest, out PipeWriter<EchoMessage> w, out PipeReader<EchoMessage> r);
            Pipe.Create<EchoMessage>(4, PipeOverflow.DropOldest, out PipeWriter<EchoMessage> w2, out PipeReader<EchoMessage> r2);
            for (int i = 0; i < 3; i++) w.Copy(EchoSample("w"));

            Region<EchoMessage> disposed = r.Receive();
            disposed.Dispose();
            bool afterDispose = false;
            try { _ = disposed.Root; } catch (ObjectDisposedException) { afterDispose = true; }
            bool twice = true;
            try { disposed.Dispose(); } catch (Exception) { twice = false; }
            Check("pipe 8: Root after Dispose throws; a second Dispose is harmless", afterDispose && twice);

            Region<EchoMessage> moved = r.Receive();
            w2.Move(moved);
            bool afterMove = false;
            try { _ = moved.Root; } catch (ObjectDisposedException) { afterMove = true; }
            Check("pipe 8: Root after Move throws", afterMove);
            using (Region<EchoMessage> arrived = r2.Receive())
                Check("pipe 8: the moved region arrives whole", arrived != null && arrived.Root.Tag == "w" && arrived.Root.Items[3].Name == "w3");

            bool scrubBefore = Pipe.ScrubOnDispose;
            Pipe.ScrubOnDispose = true;
            Region<EchoMessage> scrubbed = r.Receive();
            byte* block = scrubbed.Block;
            scrubbed.Dispose();
            Pipe.ScrubOnDispose = scrubBefore;
            ulong word = *(ulong*)(block + Region.HeaderSize);
            Check("pipe 8: a disposed block's table words are scrubbed non-canonical",
                  word == Region.ScrubWord && TypeKeys.IsKeyWord(word) && !TypeKeys.TryTable(word, out _));

            w.Dispose(); r.Dispose(); w2.Dispose(); r2.Dispose();
        }

#pragma warning restore SOSR003

        // The kernel's readers wait first with their own types; this writer
        // comes second with another. The type is the writer's: it is not
        // refused, and each reader is, at its receive (step196).
        private static void PipeConnection()
        {
            Probe(8, 1, null);
            PipeStatus layout = PipeWriter<SharpOS.Probe.Versioned>.Connect("probe.typed.layout", out PipeWriter<SharpOS.Probe.Versioned> w1, out string e1);
            PipeStatus other = PipeWriter<SharpOS.Probe.Versioned>.Connect("probe.typed.other", out PipeWriter<SharpOS.Probe.Versioned> w2, out string e2);
            ulong* refused = stackalloc ulong[2];
            Probe(8, 0, refused);
            w1?.Dispose();
            w2?.Dispose();
            Check("pipe 9: same type, other layout: the writer is not refused; the reader is, naming the type and the first field on both sides",
                  layout == PipeStatus.Ok && refused[0] == 1);
            Check("pipe 9: another type: the writer is not refused; the reader is, naming both",
                  other == PipeStatus.Ok && refused[1] == 1);
        }

        private static void PipeRefusal()
        {
            Pipe.Create<Envelope>(4, PipeOverflow.DropOldest, out PipeWriter<Envelope> w, out PipeReader<Envelope> r);
            PipeStatus stranger = w.Copy(new Envelope { Payload = new System.Text.StringBuilder("x") });
            string strangerWhy = w.LastError;
            Action code = () => s_pipeCollections++;
            PipeStatus withDelegate = w.Copy(new Envelope { Payload = code });
            string delegateWhy = w.LastError;
            AppHost.WriteString("[pipe] refusal: " + strangerWhy + " / " + delegateWhy + "\n");
            Check("pipe 10: a type outside the catalog is refused at send",
                  stranger == PipeStatus.Refused && strangerWhy != null && strangerWhy.StartsWith("type outside the catalog"));
            Check("pipe 10: a delegate is refused at send",
                  withDelegate == PipeStatus.Refused && delegateWhy != null && delegateWhy.StartsWith("a delegate"));
            Check("pipe 10: the queue untouched by the refusals", r.TryReceive(out _) == PipeStatus.Empty);
            w.Dispose();
            r.Dispose();
        }

        private static void PipeKeys()
        {
            ulong a = MessageCatalog.KeyOf(typeof(SharpOS.Std.Pipes.Probe.A.Twin));
            ulong b = MessageCatalog.KeyOf(typeof(SharpOS.Std.Pipes.Probe.B.Twin));
            Check("pipe 11: same short name and layout, other namespace: different keys", a != 0 && b != 0 && a != b);

            Pipe.Create<Envelope>(4, PipeOverflow.DropOldest, out PipeWriter<Envelope> w, out PipeReader<Envelope> r);
            var grid = new int[2, 3];
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 3; j++)
                    grid[i, j] = 10 * i + j;
            PipeStatus sent = w.Copy(new Envelope { Grid = grid, Payload = new int[2, 2] { { 1, 2 }, { 3, 4 } } });
            bool ok = false;
            using (Region<Envelope> region = r.Receive())
            {
                int[,] g = region?.Root.Grid;
                ok = g != null && g.GetLength(0) == 2 && g.GetLength(1) == 3 && g[1, 2] == 12 && g[0, 1] == 1
                     && region.Root.Payload is int[,] p && p[1, 0] == 3;
            }
            Check("pipe 11: multi-dimensional arrays pass (field and object)", sent == PipeStatus.Ok && ok);
            w.Dispose();
            r.Dispose();

            Pipe.Create<Shades>(4, PipeOverflow.DropOldest, out PipeWriter<Shades> sw, out PipeReader<Shades> sr);
            PipeStatus shadesSent = sw.Copy(new Shades { One = Shade.Deep, Many = new[] { Shade.Dark, Shade.Pale } });
            bool shadesOk = false;
            if (shadesSent == PipeStatus.Ok)
                using (Region<Shades> region = sr.Receive())
                    shadesOk = region != null && region.Root.One == Shade.Deep && region.Root.Many.Length == 2
                               && region.Root.Many[0] == Shade.Dark && region.Root.Many[1] == Shade.Pale;
            else
                AppHost.WriteString("[pipe] enum array: " + ((int)shadesSent).ToString() + " " + (sw.LastError ?? "") + "\n");
            sw.Dispose();
            sr.Dispose();
            Check("pipe 11: an array of an enum passes as a field", shadesOk);

            for (int i = 0; i < MessageCatalog.Problems.Count; i++)
                AppHost.WriteString("[pipe] catalog problem: " + MessageCatalog.Problems[i] + "\n");
            Check("pipe 11: the catalog registered every type without a collision", MessageCatalog.Problems.Count == 0);
        }

        // Test 14: measured, not asserted.
        private static void PipeBench()
        {
            Pipe.Create<EchoMessage>(4, PipeOverflow.DropOldest, out PipeWriter<EchoMessage> w, out PipeReader<EchoMessage> r);
            var items = new Item[1000];
            for (int i = 0; i < items.Length; i++)
                items[i] = new Item { Key = i, Name = "n" + i.ToString() };
            var message = new EchoMessage { Value = 1, Tag = "bench", Items = items };
            const int Rounds = 20;
            int objects = 3 + 2 * items.Length;

            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Rounds; i++)
            {
                w.Copy(message);
                using (Region<EchoMessage> region = r.Receive()) { }
            }
            long ticks = Stopwatch.GetTimestamp() - start;
            long ns = ticks * 1_000_000_000L / Stopwatch.Frequency / ((long)Rounds * objects);
            AppHost.WriteString("[bench] copy -> send -> receive -> dispose: " + ns.ToString() + " ns per object ("
                                + objects.ToString() + " objects x " + Rounds.ToString() + ")\n");
            w.Dispose();
            r.Dispose();

            var holder = new Item[1];
            var target = new Item();
            const int Stores = 1_000_000;
            start = Stopwatch.GetTimestamp();
            StoreLoop(holder, target, Stores);
            ticks = Stopwatch.GetTimestamp() - start;
            long ps = ticks * 1_000_000_000_000L / Stopwatch.Frequency / Stores;
            var box = new EchoMessage();
            start = Stopwatch.GetTimestamp();
            FieldLoop(box, "s", Stores);
            ticks = Stopwatch.GetTimestamp() - start;
            long psField = ticks * 1_000_000_000_000L / Stopwatch.Frequency / Stores;
            AppHost.WriteString("[bench] heap reference store: element " + ps.ToString() + " ps, field "
                                + psField.ToString() + " ps\n");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void StoreLoop(Item[] holder, Item target, int n)
        {
            for (int i = 0; i < n; i++) holder[0] = target;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void FieldLoop(EchoMessage box, string value, int n)
        {
            for (int i = 0; i < n; i++) box.Tag = value;
        }
    }
}
