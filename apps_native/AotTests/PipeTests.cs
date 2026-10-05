using System;
using System.Diagnostics;
using SharpOS.AppSdk;
using SharpOS.Std.Exchange;
using SharpOS.Std.Exchange.Probe;
using SharpOS.Std.Pipes;
using SharpOS.Std.Pipes.Probe;

namespace AotTests
{
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

        private static void PipeConnection()
        {
            Probe(8, 1, null);
            PipeStatus layout = PipeWriter<SharpOS.Probe.Versioned>.Connect("probe.typed.layout", out PipeWriter<SharpOS.Probe.Versioned> w1, out string e1);
            PipeStatus other = PipeWriter<SharpOS.Probe.Versioned>.Connect("probe.typed.other", out PipeWriter<SharpOS.Probe.Versioned> w2, out string e2);
            Probe(8, 0, null);
            w1?.Dispose();
            w2?.Dispose();
            AppHost.WriteString("[pipe] connection, same type, other layout: " + (e1 ?? "(no error)") + "\n");
            AppHost.WriteString("[pipe] connection, other type: " + (e2 ?? "(no error)") + "\n");
            Check("pipe 9: same type, other layout: refused, naming the type and the first field on both sides",
                  layout == PipeStatus.TypeMismatch
                  && e1 == "type SharpOS.Probe.Versioned: field #0 differs: writer A:System.Int32@8, reader A:System.Int64@8");
            Check("pipe 9: another type: refused, naming both",
                  other == PipeStatus.TypeMismatch
                  && e2 == "different types: writer sends SharpOS.Probe.Versioned, reader expects SharpOS.Probe.Different");
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
