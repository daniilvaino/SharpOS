using System;
using System.Runtime;
using System.Threading;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // PROCTEST.EXE — processes and the pipes between them (step194 §7).
    //
    //   PROCTEST.EXE [--gc-stress N] [--quick]   the tests; exit code = passed
    //   PROCTEST.EXE --child ROLE ...            one side of a test (Children.cs)
    //
    // Under --gc-stress the children this program starts of itself run with
    // the same N, and so do PIPEGEN, PIPEFILT and PIPECNT (test 13). --quick
    // leaves out the long runs (100 000 messages, 200 launches).
    internal static unsafe partial class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run(StressArgs.Apply(AppHost.Arguments));
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
        private static bool s_quick;

        private static void Check(string name, bool ok)
        {
            if (ok) s_passed++; else s_failed++;
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
        }

        private static int Run(string[] args)
        {
            // Thread.Sleep and Task.Run need the app's thread services.
            TaskBackendInstaller.Install();
            if (args.Length > 0 && args[0] == "--child")
                return Child(args);
            s_quick = args.Length > 0 && args[0] == "--quick";

            Console.WriteLine("[proctest] begin, process " + Process.CurrentId.ToString()
                              + (StressArgs.Every != 0 ? ", gc-stress " + StressArgs.Every.ToString() : ""));
            // The task pool's thread is this program's for good: started
            // before the count, not counted as something a test left behind.
            System.Threading.Tasks.Task.Run(() => { }).Wait();
            ulong[] before = Process.KernelStats();

            // Under --gc-stress (test 13): 2-6 and 11 as the task asks, with
            // 1, 9 and 10 that cost little. Not 7, 8, 12, 14: a child whose
            // threads collect back to back holds preemption off for most of
            // every tick, and its other threads starve (a scheduling window
            // the step's report names); 12 and 14 are long and measure.
            bool full = StressArgs.Every == 0;
            Run("1 addresses", Addresses);
            Run("1 two batteries", TwoBatteries);
            Run("2 target code", TargetCode);
            Run("3 by name", ByName);
            Run("4 type mismatch", TypeMismatch);
            Run("5 queue limit", QueueLimit);
            Run("6 side deaths", SideDeaths);
            if (full) Run("7 worker exceptions", WorkerExceptions);
            if (full) Run("8 kill", KillWithWorkers);
            Run("9 ends", Ends);
            Run("10 stdin and stdout", LauncherTalks);
            Run("11 WriteTo across three", WriteToAcrossThree);
            if (full) Run("12 resources back", ResourcesBack);
            if (full) Run("14 measurements", Measurements);

            Settle();
            ulong[] after = Process.KernelStats();
            Print("before the tests", before);
            Print("after the tests", after);
            Check("all: pages, processes, records, blocks, pipes, names, threads as before the tests",
                  SameStats(before, after));

            Console.WriteLine("[proctest] done: passed " + s_passed.ToString() + ", failed " + s_failed.ToString());
            return s_passed;
        }

        // A test that throws fails, and the next one runs.
        private static void Run(string name, Action test)
        {
            Console.WriteLine("[proctest] " + name);
            try { test(); }
            catch (Exception e) { Check(name + ": threw " + e.Message, false); }
        }

        private static Process Start(string path, string[] args = null, PipeReadEnd input = null, PipeWriteEnd output = null)
        {
            if (path == "PROCTEST.EXE" || path.StartsWith("PIPE"))
                args = StressArgs.Pass(args ?? new string[0]);
            return Process.Start(path, args, input, output);
        }

        private static string[] A(params string[] a) => a;

        private static int Finish(Process p)
        {
            p.WaitForExit();
            int code = p.ExitCode;
            p.Dispose();
            return code;
        }

        // ---- 1 ----

        private static void Addresses()
        {
            var p1 = Pipe.Create(); var p2 = Pipe.Create();
            Process a = Start("PROCTEST.EXE", A("--child", "where"), output: p1.WriteEnd);
            Process b = Start("PROCTEST.EXE", A("--child", "where"), output: p2.WriteEnd);
            Where wa = null, wb = null;
            foreach (Where w in p1.ReadEnd.Read<Where>()) wa = w.ToHeap();
            foreach (Where w in p2.ReadEnd.Read<Where>()) wb = w.ToHeap();
            int ca = Finish(a), cb = Finish(b);
            Check("1: two instances of one image ran at once, codes 0", ca == 0 && cb == 0 && wa != null && wb != null);
            if (wa == null || wb == null) return;
            Console.WriteLine("[proctest] images 0x" + wa.Image.ToString("x") + " and 0x" + wb.Image.ToString("x")
                              + ", statics 0x" + wa.Static.ToString("x") + " and 0x" + wb.Static.ToString("x")
                              + ", heaps 0x" + wa.Heap.ToString("x") + " and 0x" + wb.Heap.ToString("x"));
            Check("1: images at different addresses", wa.Image != wb.Image);
            Check("1: each one's static and heap inside its own range",
                  Inside(wa.Static, wa.Image) && Inside(wa.Heap, wa.Image) && Inside(wb.Static, wb.Image) && Inside(wb.Heap, wb.Image));
            Check("1: different ids", wa.Id != wb.Id && wa.Id == a.Id && wb.Id == b.Id);
        }

        // A process's range: 1 GiB from its image base (AppProcesses.ImageRegionStride).
        private static bool Inside(ulong address, ulong image) => address >= image && address < image + 0x40000000UL;

        private static void TwoBatteries()
        {
            string[] args = StressArgs.Every != 0
                ? A("--gc-stress", StressArgs.Every.ToString(), "0", "--concurrent")
                : A("--concurrent");
            Process a = Process.Start("AOTTESTS.EXE", args);
            Process b = Process.Start("AOTTESTS.EXE", args);
            int ca = Finish(a), cb = Finish(b);
            Check("1: AOTTESTS --concurrent twice at once: no check failed in either (" + ca.ToString() + ", " + cb.ToString() + ")",
                  ca == 0 && cb == 0);
        }

        // ---- 2 ----

        private static void TargetCode()
        {
            var p1 = Pipe.Create(); var p2 = Pipe.Create();
            Process a = Start("PIPEGEN.EXE", A("1000"), output: p1.WriteEnd);
            Process b = Start("PIPEFILT.EXE", A("3"), input: p1.ReadEnd, output: p2.WriteEnd);
            Process c = Start("PIPECNT.EXE", null, input: p2.ReadEnd);
            a.WaitForExit(); b.WaitForExit(); c.WaitForExit();
            Check("2: PIPEGEN 1000 | PIPEFILT 3 | PIPECNT by Process.Start: codes 0 (PIPECNT prints 400)",
                  a.ExitCode == 0 && b.ExitCode == 0 && c.ExitCode == 0);
            Check("2: the handed ends are gone from the launcher",
                  !p1.WriteEnd.IsAvailable && !p1.ReadEnd.IsAvailable && !p2.WriteEnd.IsAvailable && !p2.ReadEnd.IsAvailable);
            Check("2: three processes, three ids", a.Id != b.Id && b.Id != c.Id && a.Id != c.Id);
            a.Dispose(); b.Dispose(); c.Dispose();

            // The count read here instead of printed: the last stage's output is this program's.
            var q1 = Pipe.Create(); var q2 = Pipe.Create();
            Process g = Start("PIPEGEN.EXE", A("1000"), output: q1.WriteEnd);
            Process f = Start("PIPEFILT.EXE", A("3"), input: q1.ReadEnd, output: q2.WriteEnd);
            int n = 0, levels = 0;
            foreach (View v in q2.ReadEnd.Read()) { n++; if ((int)v["Level"] >= 3) levels++; }
            Check("2: PIPEGEN 1000 | PIPEFILT 3 read here: 400, all of level 3 and up", n == 400 && levels == 400);
            Check("2: and their codes 0", Finish(g) == 0 && Finish(f) == 0);
        }

        // ---- 3 ----

        private static void ByName()
        {
            // The reader first: it waits for the writer.
            int corners = 0, area = 0, n = 0;
            PipeReader<Square> squares = Pipe.Read<Square>("t3.class");
            Process w = Start("PROCTEST.EXE", A("--child", "name-square", "t3.class", "5"));
            foreach (Square s in squares)
            {
                n++;
                corners += s.Corners();
                area += ((IHasArea)s).Area();
            }
            Check("3: class, reader first: 5 read in place, virtual and interface calls answer the writer's values",
                  Finish(w) == 0 && n == 5 && corners == 20 && area == 0 + 1 + 4 + 9 + 16);

            // The writer first: 40 is more than the queue holds, so it waits on
            // the full queue for a reader rather than closing without one.
            w = Start("PROCTEST.EXE", A("--child", "name-seq", "t3.view", "40"));
            Thread.Sleep(100);
            n = 0;
            int sum = 0;
            foreach (View v in Pipe.Read("t3.view")) { n++; sum += (int)v["N"]; }
            Check("3: view, writer first: 40 in order", Finish(w) == 0 && n == 40 && sum == 39 * 40 / 2);

            w = Start("PROCTEST.EXE", A("--child", "name-seq", "t3.dynamic", "40"));
            n = 0;
            string last = null;
            foreach (dynamic d in Pipe.Read("t3.dynamic")) { n++; last = (string)d.Text; }
            Check("3: dynamic: 40, the last one's text", Finish(w) == 0 && n == 40 && last == "n39");

            PipeReader<Expando> bags = Pipe.Read<Expando>("t3.expando");
            w = Start("PROCTEST.EXE", A("--child", "name-expando", "t3.expando", "6"));
            n = 0;
            sum = 0;
            foreach (Expando x in bags) { n++; sum += (int)x["N"]; }
            Check("3: Expando: 6 bags, their numbers", Finish(w) == 0 && n == 6 && sum == 15);

            w = Start("PROCTEST.EXE", A("--child", "name-seq", "t3.into", "40"));
            n = 0;
            sum = 0;
            foreach (Seq s in Pipe.Read("t3.into").Into<Seq>()) { n++; sum += s.N; }
            Check("3: Into<Seq>: 40 copies of this program's own", Finish(w) == 0 && n == 40 && sum == 39 * 40 / 2);

            // A writer that closes before any reader came: what it wrote is lost.
            ulong blocks = Process.KernelStats()[3];
            w = Start("PROCTEST.EXE", A("--child", "name-seq", "t3.lost", "5"));
            int lostCode = Finish(w);
            bool back = Process.KernelStats()[3] == blocks;
            PipeReader<Seq> late = Pipe.Read<Seq>("t3.lost");
            Process again = Start("PROCTEST.EXE", A("--child", "name-seq", "t3.lost", "3"));
            n = 0;
            foreach (Seq s in late) n++;
            Check("3: a writer gone before its reader: its 5 lost, the blocks back, the reader gets the next writer's 3",
                  lostCode == 0 && back && Finish(again) == 0 && n == 3);
        }

        // ---- 4 ----

        private static void TypeMismatch()
        {
            // By name, the reader waiting: PIPEGEN, second, cannot write.
            var waiting = Pipe.Read<LogEntry>("t4.name");
            Process g = Start("PIPEGEN.EXE", A("3", "t4.name"));
            Check("4: by name, the writer second: its write end throws, exit 134", Finish(g) == 134);
            waiting.Dispose();

            // By name, the writer waiting on a full queue: the reader here throws.
            g = Start("PIPEGEN.EXE", A("100", "t4.name2"));
            WaitForNames(1);
            string message = null;
            try { Pipe.Read<LogEntry>("t4.name2"); }
            catch (PipeException e) { message = e.Message; }
            Console.WriteLine("[proctest] " + (message ?? "no exception"));
            g.Kill();
            Check("4: by name, the reader second: PipeException naming the type and the field",
                  Mentions(message, "PipeApps.LogEntry", "field") && Finish(g) == 137);

            // Standard ends: this end typed first, PIPEGEN's open fails.
            var pair = Pipe.Create();
            var reader = pair.ReadEnd.Read<LogEntry>();
            g = Start("PIPEGEN.EXE", A("3"), output: pair.WriteEnd);
            Check("4: standard output typed second: PIPEGEN ends with 134", Finish(g) == 134);
            reader.Dispose();

            // Standard ends: PIPEGEN first, this end second.
            pair = Pipe.Create();
            g = Start("PIPEGEN.EXE", A("3"), output: pair.WriteEnd);
            int code = Finish(g);
            message = null;
            try { pair.ReadEnd.Read<LogEntry>(); }
            catch (PipeException e) { message = e.Message; }
            Console.WriteLine("[proctest] " + (message ?? "no exception"));
            Check("4: an end of a pair typed second: PipeException naming the type and the field",
                  code == 0 && Mentions(message, "PipeApps.LogEntry", "field"));
        }

        // Until n pipes wait by name for their second end: the other process
        // has connected. A test may poll; the kernel does not.
        private static void WaitForNames(ulong n)
        {
            for (int i = 0; i < 2000 && Process.KernelStats()[5] < n; i++) Thread.Sleep(5);
        }

        private static bool Mentions(string text, string a, string b)
            => text != null && text.Contains(a) && text.Contains(b);

        // ---- 5 ----

        private static void QueueLimit()
        {
            int count = s_quick || StressArgs.Every != 0 ? 5000 : 100000;
            var pair = Pipe.Create();
            Process w = Start("PROCTEST.EXE", A("--child", "seq", count.ToString()), output: pair.WriteEnd);
            Thread.Sleep(300);                       // the writer fills the queue and waits
            int n = 0, expect = 0;
            bool ordered = true;
            var reader = pair.ReadEnd.Read<Seq>();
            foreach (Seq s in reader)
            {
                if (s.N != expect++) ordered = false;
                n++;
                if ((n & 1023) == 0) Thread.Sleep(1);   // and a slow reader keeps it waiting
            }
            int code = Finish(w);
            Check("5: " + count.ToString() + " through a queue of 16 to a slow reader: all, in order, none dropped",
                  n == count && ordered && reader.Dropped == 0);
            Check("5: the writer waited on the full queue (exit 0, not 3)", code == 0);
        }

        // ---- 6 ----

        private static void SideDeaths()
        {
            WriterGoes("exit", 0, PipeStatus.EndOfStream);
            WriterGoes("throw", 134, PipeStatus.Broken);
            WriterGoes("kill", 137, PipeStatus.Broken);
            ReaderGoes("exit");
            ReaderGoes("close");
        }

        private static void WriterGoes(string how, int code, PipeStatus end)
        {
            var pair = Pipe.Create();
            var input = Pipe.Create();
            // The reader of the signal first: a writer that closes before its
            // reader comes loses what it wrote.
            PipeReader<Seq> ready = how == "kill" ? Pipe.Read<Seq>("t6.ready") : null;
            Process w = Start("PROCTEST.EXE", A("--child", "write10", how == "kill" ? "wait" : how),
                              input: input.ReadEnd, output: pair.WriteEnd);
            if (how == "kill")
            {
                foreach (Seq s in ready) break;
                w.Kill();
            }
            int exit = Finish(w);
            input.WriteEnd.Dispose();

            int n = 0;
            PipeStatus status = PipeStatus.Ok;
            try { foreach (Seq s in pair.ReadEnd.Read<Seq>()) n++; status = PipeStatus.EndOfStream; }
            catch (PipeException e) { status = e.Status; }
            Check("6: the writer gone by " + how + " with 10 queued: exit " + exit.ToString() + ", 10 read, then "
                  + (end == PipeStatus.Broken ? "broken" : "end of stream"),
                  exit == code && n == 10 && status == end);
        }

        private static void ReaderGoes(string how)
        {
            var pair = Pipe.Create();
            // Open until the child has read it: closed before its reader came, it would be lost.
            PipeWriter<Seq> go = Pipe.Write<Seq>("t6.go");
            Process r = Start("PROCTEST.EXE", A("--child", "reader-go", how), input: pair.ReadEnd);
            var writer = pair.WriteEnd.Write<Seq>();
            PipeStatus got = PipeStatus.Ok;
            int written = 0;
            var t = System.Threading.Tasks.Task.Run(() =>
            {
                try { for (int i = 0; i < 100; i++) { writer.Copy(new Seq { N = i }); written++; } }
                catch (PipeException e) { got = e.Status; }
            });
            Thread.Sleep(100);                       // the writer is waiting on a full queue now
            go.Copy(new Seq { N = 1 });
            t.Wait();
            int code = Finish(r);
            go.Dispose();
            Check("6: the reader " + (how == "exit" ? "exits" : "closes its end") + ": the writer waiting on a full queue gets broken ("
                  + written.ToString() + " written)",
                  code == 0 && got == PipeStatus.Broken && written >= 16 && written < 100);
            writer.Dispose();
        }

        // ---- 7 ----

        private static void WorkerExceptions()
        {
            foreach (string main in new[] { "pipe", "sleep", "spin" })
            {
                var pair = Pipe.Create();
                var input = Pipe.Create();
                Process p = Start("PROCTEST.EXE", A("--child", "worker-throws", main), input: input.ReadEnd, output: pair.WriteEnd);
                int code = Finish(p);
                input.WriteEnd.Dispose();
                int n = 0;
                PipeStatus status = PipeStatus.Ok;
                try { foreach (Seq s in pair.ReadEnd.Read<Seq>()) n++; status = PipeStatus.EndOfStream; }
                catch (PipeException e) { status = e.Status; }
                Check("7: a worker's unhandled exception, the main thread " + (main == "pipe" ? "waiting on a pipe" : main == "sleep" ? "asleep" : "computing")
                      + ": exit 134, 3 read, then broken", code == 134 && n == 3 && status == PipeStatus.Broken);
            }
        }

        // ---- 8 ----

        private static void KillWithWorkers()
        {
            foreach (string how in new[] { "kill", "exit" })
            {
                var input = Pipe.Create();
                ulong threads = Process.KernelStats()[6];
                PipeReader<Seq> ready = Pipe.Read<Seq>("t8.ready");
                Process p = Start("PROCTEST.EXE", A("--child", "busy", how), input: input.ReadEnd);
                foreach (Seq s in ready) break;
                if (how == "kill") p.Kill();
                int code = Finish(p);
                input.WriteEnd.Dispose();
                Settle();
                Check("8: " + (how == "kill" ? "Kill" : "the main thread returns") + " with workers in app code, on a pipe, asleep, in a file read: exit "
                      + code.ToString() + ", their threads gone",
                      code == (how == "kill" ? 137 : 5) && Process.KernelStats()[6] == threads);
                Process again = Start("PROCTEST.EXE", A("--child", "readfile"));
                Check("8: and another process reads the same file", Finish(again) == 0);
            }
        }

        // ---- 9 ----

        private static void Ends()
        {
            var pair = Pipe.Create();
            Process p = Start("PROCTEST.EXE", A("--child", "nop"), output: pair.WriteEnd);
            int code = Finish(p);
            int n = 0;
            foreach (View v in pair.ReadEnd.Read()) n++;
            Check("9: an output handed over and never opened closes at exit: an empty stream", code == 0 && n == 0);

            Check("9: the standard input opened twice: InvalidOperationException in the child",
                  Finish(Start("PROCTEST.EXE", A("--child", "reopen"))) == 0);
            Check("9: no input handed over: an empty stream",
                  Finish(Start("PROCTEST.EXE", A("--child", "no-input"))) == 10);

            pair = Pipe.Create();
            bool refused = false;
            try { Process.Start("NOSUCH.EXE", null, pair.ReadEnd, pair.WriteEnd); }
            catch (InvalidOperationException) { refused = true; }
            Check("9: a start that fails leaves the ends with the launcher", refused && pair.ReadEnd.IsAvailable && pair.WriteEnd.IsAvailable);
            var w = pair.WriteEnd.Write<Seq>();
            var r = pair.ReadEnd.Read<Seq>();
            w.Copy(new Seq { N = 9 });
            w.Dispose();
            n = 0;
            foreach (Seq s in r) n += s.N;
            Check("9: and it opens them itself: one message through", n == 9);

            bool twice = false;
            pair = Pipe.Create();
            pair.ReadEnd.Read().Dispose();
            try { pair.ReadEnd.Read(); }
            catch (InvalidOperationException) { twice = true; }
            pair.WriteEnd.Dispose();
            Check("9: an end of a pair opened twice: InvalidOperationException", twice);
        }

        // ---- 10 ----

        private static void LauncherTalks()
        {
            var input = Pipe.Create(); var output = Pipe.Create();
            Process p = Start("PROCTEST.EXE", A("--child", "echo"), input: input.ReadEnd, output: output.WriteEnd);
            var writer = input.WriteEnd.Write<Seq>();
            var t = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 100; i++) writer.Copy(new Seq { N = i, Text = "e" + i.ToString() });
                writer.Dispose();
            });
            int n = 0;
            bool right = true;
            foreach (Seq s in output.ReadEnd.Read<Seq>())
            {
                if (s.N != n * 2 || s.Text != "e" + n.ToString()) right = false;
                n++;
            }
            t.Wait();
            Check("10: 100 written to a child's input, 100 doubled read from its output", Finish(p) == 0 && n == 100 && right);

            PipeReader<Seq> orphan = Pipe.Read<Seq>("t10.orphan");
            Process launcher = Start("PROCTEST.EXE", A("--child", "orphan-launcher"));
            int launcherCode = Finish(launcher);
            int got = 0;
            foreach (Seq s in orphan) got = s.N;
            Check("10: a launcher that ended first: its child went on and wrote", launcherCode == 0 && got == 77);
        }

        // ---- 11 ----

        private static void WriteToAcrossThree()
        {
            PipeReader<Address> where = Pipe.Read<Address>("t11.addr");
            var p1 = Pipe.Create(); var p2 = Pipe.Create(); var p3 = Pipe.Create();
            Process a = Start("PROCTEST.EXE", A("--child", "t11-gen"), output: p1.WriteEnd);
            Process b = Start("PROCTEST.EXE", A("--child", "t11-class"), input: p1.ReadEnd, output: p2.WriteEnd);
            Process c = Start("PROCTEST.EXE", A("--child", "t11-raw"), input: p2.ReadEnd, output: p3.WriteEnd);
            ulong sent = 0, arrived = 0;
            foreach (Address x in where) sent = x.Block;
            int side = 0;
            RawPipeReader last = p3.ReadEnd.Read();
            RawRegion region;
            while ((region = last.Receive()) != null)
            {
                arrived = (ulong)region.Block;
                side = (int)region.Root["Side"];
                region.Dispose();
            }
            last.Dispose();
            Console.WriteLine("[proctest] block sent 0x" + sent.ToString("x") + ", arrived 0x" + arrived.ToString("x"));
            Check("11: a class stage and a view stage pass the block on: the third app reads it at the first one's address",
                  Finish(a) == 0 && Finish(b) == 0 && Finish(c) == 0 && side == 11 && sent != 0 && sent == arrived);
        }

        // ---- 12 ----

        private static void ResourcesBack()
        {
            Settle();
            ulong[] before = Process.KernelStats();
            int launches = s_quick || StressArgs.Every != 0 ? 20 : 200;
            int pipelines = s_quick || StressArgs.Every != 0 ? 5 : 50;
            int bad = 0;
            for (int i = 0; i < launches; i++)
                if (Finish(Start("PROCTEST.EXE", A("--child", "nop"))) != 0) bad++;
            for (int i = 0; i < pipelines; i++)
            {
                var p1 = Pipe.Create(); var p2 = Pipe.Create();
                Process a = Start("PIPEGEN.EXE", A("20"), output: p1.WriteEnd);
                Process b = Start("PIPEFILT.EXE", A("3"), input: p1.ReadEnd, output: p2.WriteEnd);
                int n = 0;
                foreach (View v in p2.ReadEnd.Read()) n++;
                if (Finish(a) != 0 || Finish(b) != 0 || n != 8) bad++;
            }
            Settle();
            ulong[] after = Process.KernelStats();
            Print("before", before);
            Print("after", after);
            Check("12: " + launches.ToString() + " launches and " + pipelines.ToString() + " pipelines, all fine", bad == 0);
            Check("12: memory, processes, records, exchange blocks, pipes, names, threads back where they were", SameStats(before, after));

            // The limit: start until refused, end one, start again.
            var waiting = new System.Collections.Generic.List<Process>();
            var inputs = new System.Collections.Generic.List<PipePair>();
            string refusal = null;
            while (waiting.Count < 32)
            {
                var pair = Pipe.Create();
                try { waiting.Add(Start("PROCTEST.EXE", A("--child", "wait-stdin"), input: pair.ReadEnd)); inputs.Add(pair); }
                catch (InvalidOperationException e) { refusal = e.Message; pair.WriteEnd.Dispose(); pair.ReadEnd.Dispose(); break; }
            }
            Console.WriteLine("[proctest] started " + waiting.Count.ToString() + " before: " + (refusal ?? "no refusal"));
            inputs[0].WriteEnd.Dispose();
            Finish(waiting[0]);
            bool again = false;
            var extra = Pipe.Create();
            try { waiting[0] = Start("PROCTEST.EXE", A("--child", "wait-stdin"), input: extra.ReadEnd); inputs[0] = extra; again = true; }
            catch (InvalidOperationException) { }
            for (int i = 0; i < waiting.Count; i++) { inputs[i].WriteEnd.Dispose(); if (i > 0 || again) Finish(waiting[i]); }
            Check("12: past the process limit Start throws (" + (refusal ?? "-") + ")", refusal != null && waiting.Count >= 8);
            Check("12: after one ends its slot is used again", again);
        }

        private static void Print(string what, ulong[] s)
        {
            Console.WriteLine("[proctest] " + what + ": pages " + s[0].ToString() + ", running " + s[1].ToString()
                              + ", records " + s[2].ToString() + ", blocks " + s[3].ToString() + ", pipes " + s[4].ToString()
                              + ", names " + s[5].ToString() + ", threads " + s[6].ToString());
        }

        // Pages may move by what this program's own heap and the kernel's
        // took meanwhile (strings, lists); the rest must match exactly.
        private static bool SameStats(ulong[] a, ulong[] b)
        {
            long pages = (long)b[0] - (long)a[0];
            return pages >= -64 && pages <= 256 && a[1] == b[1] && a[2] == b[2] && a[3] == b[3]
                   && a[4] == b[4] && a[5] == b[5] && a[6] == b[6];
        }

        // Threads of ended processes leave the machine a moment after their
        // process is reported ended; their stacks go at the next start.
        private static void Settle()
        {
            Thread.Sleep(50);
        }

        // ---- 14 ----

        private static void Measurements()
        {
            const int Starts = 20;
            double[] t0 = Process.StartTimes();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < Starts; i++) Finish(Start("PROCTEST.EXE", A("--child", "nop")));
            long startUs = clock.ElapsedTicks * 1000000 / System.Diagnostics.Stopwatch.Frequency / Starts;
            double[] t1 = Process.StartTimes();
            Console.WriteLine("[perf] proc.start_and_exit_us=" + startUs.ToString());
            string[] phases = { "", "read", "load", "build", "sync_low", "rest" };
            for (int i = 1; i < 6; i++)
                Console.WriteLine("[perf] proc.start." + phases[i] + "_us=" + ((long)((t1[i] - t0[i]) * 1000 / Starts)).ToString());

            // One message — an object with a number and a string — three ways,
            // each side timed apart: 2000 written into a queue that holds them
            // all, then read (received, translated, let go). The child times
            // its own writes and answers the nanoseconds per message as its
            // exit code; the kernel's writes are timed around the probe call.
            const int Messages = 2000;
            const uint Room = 4096;

            var pair = Pipe.Create(Room);
            Process f = Start("PROCTEST.EXE", A("--child", "flood", Messages.ToString()), output: pair.WriteEnd);
            long appWrite = Finish(f);
            long appRead = TimeReads(pair.ReadEnd.Read(), Messages, out int n);
            Console.WriteLine("[perf] pipe.app_to_app.write_ns=" + appWrite.ToString() + " read_ns=" + appRead.ToString()
                              + " (" + n.ToString() + " messages)");
            bool ok = n == Messages;

            var own = Pipe.Create(Room);
            var writer = own.WriteEnd.Write<Seq>();
            var seq = new Seq { N = 0, Text = "f" };
            clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < Messages; i++) { seq.N = i; writer.Copy(seq); }
            long ownWrite = Ns(clock, Messages);
            writer.Dispose();
            long ownRead = TimeReads(own.ReadEnd.Read(), Messages, out n);
            Console.WriteLine("[perf] pipe.in_app.write_ns=" + ownWrite.ToString() + " read_ns=" + ownRead.ToString()
                              + " (" + n.ToString() + " messages)");
            ok &= n == Messages;

            if (AppRuntime.Services->PipeProbeAddress != 0)
            {
                RawPipeReader.Connect("probe.flood", out RawPipeReader fromKernel, out _, Room);
                ulong* answer = stackalloc ulong[2];
                clock = System.Diagnostics.Stopwatch.StartNew();
                ((delegate* unmanaged<int, ulong, ulong*, int>)AppRuntime.Services->PipeProbeAddress)(4, Messages, answer);
                long kernelWrite = Ns(clock, Messages);
                long kernelRead = TimeReads(fromKernel, Messages, out n);
                Console.WriteLine("[perf] pipe.kernel_to_app.write_ns=" + kernelWrite.ToString() + " read_ns=" + kernelRead.ToString()
                                  + " (" + n.ToString() + " messages, kernel side)");
                ok &= n == Messages;
            }

            var p1 = Pipe.Create();
            clock = System.Diagnostics.Stopwatch.StartNew();
            Process g = Start("PIPEGEN.EXE", A("100000"), output: p1.WriteEnd);
            Process c = Start("PIPECNT.EXE", null, input: p1.ReadEnd);
            Finish(g);
            Finish(c);
            Console.WriteLine("[perf] pipe.pipegen100000_pipecnt_ms=" + clock.ElapsedMilliseconds.ToString());
            Check("14: measurements taken, every message arrived (see [perf] lines)", ok);
        }

        private static long Ns(System.Diagnostics.Stopwatch clock, int count)
            => clock.ElapsedTicks * 1000000000 / System.Diagnostics.Stopwatch.Frequency / count;

        // Reads what is queued, each message translated and let go, and the
        // time per message; the stream ends when the writer is gone.
        private static long TimeReads(RawPipeReader reader, int expected, out int n)
        {
            n = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (View v in reader) n++;
            return Ns(clock, n == 0 ? 1 : n);
        }
    }
}
