using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime;
using System.Text;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace DataApps
{
    // DATATEST.EXE [--quick] — external data (step197): READ, WRITE, CONVERT,
    // `>`/`>>`, bytes and text over a pipe, Into<T> from an Expando, pipelines
    // from code. Exit code = checks passed. --quick leaves out the big files
    // (100 MiB of JSON, 64 MiB copy); --gc-stress N runs everything here
    // stressed. DATATEST --child ROLE is a stage the tests start (Children.cs);
    // DATATEST --perf prints the step's [perf197] measurements.
    internal static unsafe partial class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run(PipeApps.StressArgs.Apply(AppHost.Arguments));
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run(AppHost.Arguments);

        private static int s_passed, s_failed;
        private static bool s_quick;

        private static void Check(string name, bool ok)
        {
            if (ok) s_passed++; else s_failed++;
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
        }

        private static int Run(string[] args)
        {
            TaskBackendInstaller.Install();
            if (args.Length > 0 && args[0] == "--child") return Child(args);
            if (args.Length > 0 && args[0] == "--perf") return Perf();
            s_quick = args.Length > 0 && args[0] == "--quick";
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--only") s_only = args[i + 1];
                if (args[i] == "--case") s_case = int.Parse(args[i + 1]);
            }
            Console.WriteLine("[datatest] begin" + (s_quick ? " (quick)" : "")
                              + (PipeApps.StressArgs.Every != 0 ? ", gc-stress " + PipeApps.StressArgs.Every.ToString() : ""));
            ulong[] before = Process.KernelStats();

            Test("1 the target pipeline", TargetPipeline);
            Test("2 JSON there and back", JsonRoundTrip);
            Test("3 a class through JSON and Into<T>", ClassThroughJson);
            Test("4 JSON errors", JsonErrors);
            if (!s_quick) Test("5 a 100 MiB file of a million objects", BigFile);
            Test("6 a class stage after CONVERT --from json", ClassStageRefused);
            Test("7 copies, > and >>", Copies);
            Test("8 READ on the screen", ReadToScreen);
            Test("9 bytes and text from programs", BytesFromPrograms);
            Test("10 quiet breaks", QuietBreaks);
            Test("11 pipelines from code", PipelinesFromCode);
            Test("12 an exception thrown in a finally, caught outside, again and again", ThrowFromFinally);
            Test("13 the same from a delegate in a struct enumerator's Dispose", ThrowFromEnumeratorDispose);
            Test("14 the same after kernel services in the Dispose", ThrowAfterServices);
            Test("15 test 4 with the wait in Dispose (the old reader)", OldReader);

            // The last stages leave on their own time (slowly under --gc-stress):
            // the counters settle within seconds, not at once.
            ulong[] after = Process.KernelStats();
            for (int wait = 0; wait < 100 && !Settled(before, after); wait++)
            {
                System.Threading.Thread.Sleep(100);
                after = Process.KernelStats();
            }
            Check("all: processes, records, pipes, threads as before the tests"
                  + (Settled(before, after) ? "" : " (before/after " + Stats(before) + " / " + Stats(after) + ")"),
                  Settled(before, after));
            Console.WriteLine("[datatest] done: passed " + s_passed.ToString() + ", failed " + s_failed.ToString());
            return s_passed;
        }

        private static bool Settled(ulong[] before, ulong[] after)
            => before[1] == after[1] && before[2] == after[2] && before[4] == after[4] && before[6] == after[6];

        private static string Stats(ulong[] s)
            => s[1].ToString() + "," + s[2].ToString() + "," + s[4].ToString() + "," + s[6].ToString();

        // --only N: just test N; --case K: just case K of a test with cases (test 4).
        private static string s_only;
        private static int s_case = -1;

        private static void Test(string name, Action test)
        {
            if (s_only != null && !name.StartsWith(s_only + " ")) return;
            Console.WriteLine("[datatest] " + name);
            try { test(); }
            catch (Exception e) { Check(name + ": threw " + e.Message, false); }
        }

        // ---- helpers ----

        // A line through the shell, as typed at its prompt; its exit code.
        private static int Sh(string line)
        {
            var shell = Process.Start("\\apps\\SHELL.EXE", new[] { "-c", line });
            shell.WaitForExit();
            int code = shell.ExitCode;
            shell.Dispose();
            return code;
        }

        // A line with a stage of this program, stressed as this one is.
        private static string Self(string role)
            => "DATATEST" + (PipeApps.StressArgs.Every != 0 ? " --gc-stress " + PipeApps.StressArgs.Every.ToString() : "") + " --child " + role;

        private static string Text(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));

        // The string messages of a pipeline's output.
        private static List<string> Lines(string pipeline)
        {
            var lines = new List<string>();
            foreach (View v in Pipe.From(pipeline)) lines.Add((string)v);
            return lines;
        }

        private static string Escaped(string s)
        {
            var b = new StringBuilder();
            foreach (char c in s)
            {
                if (c < 0x80) b.Append(c);
                else b.Append("\\u").Append(((int)c).ToString("X4"));
            }
            return b.ToString();
        }

        private static string Join(IList<string> lines)
        {
            var b = new StringBuilder();
            foreach (string l in lines) b.Append(l).Append('\n');
            return b.ToString();
        }

        // ---- 1 ----

        private static void TargetPipeline()
        {
            Check("1: PIPEGEN 5 | CONVERT --to json > t1in.json", Sh("PIPEGEN 5 | CONVERT --to json > t1in.json") == 0);
            Check("1: READ | CONVERT --from json | VIEWFILT 3 | CONVERT --to json > t1out.json",
                  Sh("READ t1in.json | CONVERT --from json | VIEWFILT 3 | CONVERT --to json > t1out.json") == 0);
            string expected = Join(new[]
            {
                "[",
                "{\"Level\":3,\"Text\":\"text 3\"},",
                "{\"Level\":4,\"Text\":\"text 4\"}",
                "]",
            });
            string actual = Text("t1out.json");
            Check("1: t1out.json is the expected text", actual == expected);
            if (actual != expected) Console.WriteLine(actual);
        }

        // ---- 2 ----

        private const string Tricky =
            "[{\"nested\":{\"list\":[1,2,{\"deep\":[[],{}]}],\"empty\":{},\"none\":[]},"
            + "\"text\":\"кириллица и \\\"кавычки\\\", \\\\ \\/ \\b\\f\\n\\r\\t \\u0001 \\u00e9 \\ud83d\\ude00 😀\","
            + "\"ключ\":\"значение\","
            + "\"big\":12345678901234567890,\"min\":-9223372036854775808,\"max\":9223372036854775807,"
            + "\"frac\":0.1,\"tiny\":5e-324,\"huge\":1.7976931348623157e308,\"neg\":-0.5,\"exp\":1.5e300,"
            + "\"yes\":true,\"no\":false,\"nothing\":null},"
            + "{\"second\":[\"a\",null,true,1.25]}]";

        private static void JsonRoundTrip()
        {
            File.WriteAllText("t2in.json", Tricky);
            Check("2: READ t2in.json | CONVERT --from json | CONVERT --to json > t2out.json",
                  Sh("READ t2in.json | CONVERT --from json | CONVERT --to json > t2out.json") == 0);
            List<object> a = JsonValues.ParseAll(Encoding.UTF8.GetBytes(Tricky));
            List<object> b = JsonValues.ParseAll(File.ReadAllBytes("t2out.json"));
            // The input is one array of two objects; the output an array of them.
            string differ = JsonValues.Differ(a, b, "$");
            Check("2: the values are the same" + (differ != null ? " (" + differ + ")" : ""), differ == null);
        }

        // ---- 3 ----

        private static Record MakeRecord(int i) => new Record
        {
            Id = i,
            Color = i % 2 == 0 ? Shade.Blue : Shade.Green,
            When = new DateTime(2026, 10, 8, 13, 45, 30 + i).AddTicks(1234567),
            Key = Guid.Parse("01234567-89ab-cdef-0123-456789abcde" + i.ToString("x")),
            Span = new TimeSpan(3 * TimeSpan.TicksPerDay + 4 * TimeSpan.TicksPerHour + 5 * TimeSpan.TicksPerMinute + 6 * TimeSpan.TicksPerSecond + 7),
            Big = long.MaxValue - i,
            Flag = i % 2 == 1,
            Note = "запись № " + i.ToString() + " \"в кавычках\"",
            Child = new Inner { Name = "вложенный " + i.ToString(), Weight = 0.25 * i },
            Numbers = new[] { i, -i, int.MaxValue },
            Items = new[] { new Inner { Name = "a", Weight = 1.5 }, new Inner { Name = "b", Weight = -2 } },
        };

        // The first field that differs; null when the records are equal.
        private static string Differs(Record a, Record b)
        {
            if (a.Id != b.Id) return "Id";
            if (a.Color != b.Color) return "Color";
            if (a.When.Ticks != b.When.Ticks) return "When";
            if (a.Key.ToString() != b.Key.ToString()) return "Key";
            if (a.Span.Ticks != b.Span.Ticks) return "Span";
            if (a.Big != b.Big) return "Big";
            if (a.Flag != b.Flag) return "Flag";
            if (a.Note != b.Note) return "Note";
            if (b.Child == null || a.Child.Name != b.Child.Name || a.Child.Weight != b.Child.Weight) return "Child";
            if (b.Numbers == null || a.Numbers.Length != b.Numbers.Length) return "Numbers";
            for (int i = 0; i < a.Numbers.Length; i++)
                if (a.Numbers[i] != b.Numbers[i]) return "Numbers";
            if (b.Items == null || a.Items.Length != b.Items.Length) return "Items";
            for (int i = 0; i < a.Items.Length; i++)
                if (b.Items[i] == null || a.Items[i].Name != b.Items[i].Name || a.Items[i].Weight != b.Items[i].Weight) return "Items";
            return null;
        }

        private static void ClassThroughJson()
        {
            using (PipeWriter<Record> w = Pipe.To("CONVERT --to json | WRITE t3.json").Write<Record>())
                for (int i = 0; i < 3; i++) w.Copy(MakeRecord(i));
            Console.WriteLine(Text("t3.json"));

            int n = 0;
            string differs = null;
            foreach (Record r in Pipe.From("READ t3.json | CONVERT --from json").Into<Record>())
            {
                string d = Differs(MakeRecord(n), r);
                if (d != null && differs == null) differs = "record " + n.ToString() + ", " + d;
                n++;
            }
            Check("3: three records came back", n == 3);
            Check("3: equal field by field — enum, DateTime, Guid, TimeSpan, nested, arrays"
                  + (differs == null ? "" : " (differs: " + differs + ")"), differs == null);
        }

        // ---- 4 ----

        private static void JsonErrors()
        {
            var deep = new StringBuilder();
            for (int i = 0; i < 70; i++) deep.Append("{\"a\":");
            deep.Append('1');
            for (int i = 0; i < 70; i++) deep.Append('}');
            string[] bad =
            {
                "[{\"a\":1},{\"b\":\"half",          // cut in the middle of a value
                "[{\"a\":1,}]",                     // a comma too many
                "[" + deep.ToString() + "]",        // deeper than the limit
                "[{\"a\":1},2]",                    // an element that is not an object
            };
            string[] what = { "cut short", "extra comma", "too deep", "scalar element" };
            for (int i = 0; i < bad.Length; i++)
            {
                if (s_case >= 0 && i != s_case) continue;
                string path = "t4bad" + i.ToString() + ".json";
                File.WriteAllText(path, bad[i]);
                PipelineException failed = null;
                try
                {
                    foreach (View v in Pipe.From("READ " + path + " | CONVERT --from json | VIEWFILT 0")) { }
                }
                catch (PipelineException e) { failed = e; }
                Check("4: " + what[i] + ": CONVERT fails with 1, the stage after it quietly",
                      failed != null && failed.Stage == "CONVERT" && failed.ExitCode == 1);
            }
        }

        // ---- 5 ----

        private static void BigFile()
        {
            const int Rows = 1000000;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Check("5: a million rows to t5.json", Sh(Self("gen " + Rows.ToString()) + " | CONVERT --to json --lines > t5.json") == 0);
            long writeMs = clock.ElapsedMilliseconds;
            List<string> size = Lines("READ t5.json | " + Self("bytes-sum"));
            long bytes = size.Count == 1 ? long.Parse(size[0].Split(' ')[0]) : 0;
            Check("5: the file is over 100 MiB (" + (bytes >> 20).ToString() + " MiB)", bytes >= 100L << 20);
            clock.Restart();
            List<string> count = Lines("READ t5.json | CONVERT --from json | " + Self("count"));
            long readMs = clock.ElapsedMilliseconds;
            Check("5: a million objects back through a heap of 64 MiB", count.Count == 1 && count[0] == Rows.ToString());
            Console.WriteLine("[perf197] big.to_json_ms=" + writeMs.ToString() + " from_json_ms=" + readMs.ToString()
                              + " bytes=" + bytes.ToString());
            File.WriteAllBytes("t5.json", new byte[0]);     // no delete in the FAT driver: cut, the clusters go free
        }

        // ---- --perf: the step's measurements ----

        // [perf197] lines: a copy READ | WRITE in MiB/s; JSON out and in, in
        // MiB/s and ns per object; Into<T> per object, all of it and above
        // the reading alone. Setup (the files) is not timed.
        private static int Perf()
        {
            const long CopyBytes = 64L << 20;
            const int Rows = 200000;
            Sh(Self("bytes-gen " + CopyBytes.ToString()) + " > tp.bin");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            int copied = Sh("READ tp.bin | WRITE tp2.bin");
            double copyMs = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            int wrote = Sh(Self("gen " + Rows.ToString()) + " | CONVERT --to json --lines > tp.json");
            double toMs = clock.Elapsed.TotalMilliseconds;
            List<string> size = Lines("READ tp.json | " + Self("bytes-sum"));
            long jsonBytes = size.Count == 1 ? long.Parse(size[0].Split(' ')[0]) : 0;

            clock.Restart();
            List<string> count = Lines("READ tp.json | CONVERT --from json | " + Self("count"));
            double fromMs = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            long into = 0;
            foreach (Row r in Pipe.From("READ tp.json | CONVERT --from json").Into<Row>()) into += r.Level;
            double intoMs = clock.Elapsed.TotalMilliseconds;

            bool ok = copied == 0 && wrote == 0 && count.Count == 1 && count[0] == Rows.ToString();
            Console.WriteLine("[perf197] read_write_mib_s=" + MiBs(CopyBytes, copyMs) + " bytes=" + CopyBytes.ToString()
                              + (ok ? "" : " (a stage failed)"));
            Console.WriteLine("[perf197] to_json_mib_s=" + MiBs(jsonBytes, toMs) + " to_json_ns_per_object=" + Ns(toMs, Rows)
                              + " from_json_mib_s=" + MiBs(jsonBytes, fromMs) + " from_json_ns_per_object=" + Ns(fromMs, Rows)
                              + " objects=" + Rows.ToString() + " json_bytes=" + jsonBytes.ToString());
            Console.WriteLine("[perf197] into_ns_per_object=" + Ns(intoMs, Rows) + " into_over_reading_ns=" + Ns(intoMs - fromMs, Rows));
            // No delete in the FAT driver yet: cut to nothing, the clusters go free.
            File.WriteAllBytes("tp.bin", new byte[0]);
            File.WriteAllBytes("tp2.bin", new byte[0]);
            File.WriteAllBytes("tp.json", new byte[0]);
            return ok ? 0 : 1;
        }

        private static string MiBs(long bytes, double ms) => ms <= 0 ? "?" : ((long)(bytes / 1048576.0 / (ms / 1000.0))).ToString();
        private static string Ns(double ms, long n) => ((long)(ms * 1000000.0 / n)).ToString();

        // ---- 6 ----

        private static void ClassStageRefused()
        {
            Sh("PIPEGEN 5 | CONVERT --to json > t6.json");
            PipelineException failed = null;
            try { foreach (View v in Pipe.From("READ t6.json | CONVERT --from json | PIPEFILT 0")) { } }
            catch (PipelineException e) { failed = e; }
            Check("6: a stage reading a class refuses the Expando (PIPEFILT fails)", failed != null && failed.Stage == "PIPEFILT");
            int n = 0;
            bool texts = true;
            foreach (LogEntry e in Pipe.From("READ t6.json | CONVERT --from json").Into<LogEntry>())
            {
                texts &= e.Level == n && e.Text == "text " + n.ToString();
                n++;
            }
            Check("6: the same through Into<T> works", n == 5 && texts);
        }

        // ---- 7 ----

        private static bool SameFile(string a, string b, long length, bool pattern)
        {
            using AppFile fa = AppFile.Open(a, AppFile.ModeRead);
            using AppFile fb = AppFile.Open(b, AppFile.ModeRead);
            var pa = new byte[64 * 1024];
            var pb = new byte[64 * 1024];
            long at = 0;
            while (true)
            {
                int na = Fill(fa, pa), nb = Fill(fb, pb);
                if (na != nb) return false;
                if (na == 0) break;
                for (int i = 0; i < na; i++)
                {
                    if (pa[i] != pb[i]) return false;
                    if (pattern && pa[i] != Pattern(at + i)) return false;
                }
                at += na;
            }
            return at == length;
        }

        private static int Fill(AppFile f, byte[] into)
        {
            int got = 0;
            while (got < into.Length)
            {
                int n = f.Read(new Span<byte>(into, got, into.Length - got));
                if (n == 0) break;
                got += n;
            }
            return got;
        }

        private static void Copies()
        {
            long[] sizes = { 0, 1, s_quick ? 1L << 20 : 64L << 20 };
            foreach (long size in sizes)
            {
                string a = "t7a" + size.ToString() + ".bin", b = "t7b" + size.ToString() + ".bin";
                bool made = Sh(Self("bytes-gen " + size.ToString()) + " | WRITE " + a) == 0;
                bool copied = Sh("READ " + a + " | WRITE " + b) == 0;
                Check("7: " + size.ToString() + " B: READ a | WRITE b is equal byte for byte", made && copied && SameFile(a, b, size, true));
            }
            Sh("PIPEGEN 3 > t7.txt");
            Sh("PIPEGEN 2 >> t7.txt");
            var lines = new List<string>();
            for (int i = 0; i < 3; i++) lines.Add("PipeApps.LogEntry { Level = " + i.ToString() + ", Text = \"text " + i.ToString() + "\" }");
            for (int i = 0; i < 2; i++) lines.Add("PipeApps.LogEntry { Level = " + i.ToString() + ", Text = \"text " + i.ToString() + "\" }");
            Check("7: > and >> from an object program: the lines the screen shows", Text("t7.txt") == Join(lines));
        }

        // ---- 8 ----

        private static void ReadToScreen()
        {
            Check("8: READ of a text file on the screen", Sh("READ t7.txt") == 0);
            Check("8: READ of a binary file on the screen", Sh("READ t7a1.bin") == 0);
            Check("8: READ of a file that is not there: 1", Sh("READ NOSUCH.TXT") == 1);
        }

        // ---- 9 ----

        private static string Sum(byte[] data)
        {
            ulong sum = 0;
            foreach (byte b in data) sum = sum * 31 + b;
            return data.Length.ToString() + " " + sum.ToString();
        }

        // What bytes-sum answers for the first `size` bytes of the pattern.
        private static string PatternSum(long size)
        {
            ulong sum = 0;
            for (long i = 0; i < size; i++) sum = sum * 31 + Pattern(i);
            return size.ToString() + " " + sum.ToString();
        }

        private static void BytesFromPrograms()
        {
            long size = s_quick ? 1L << 20 : 64L << 20;
            List<string> fromRead = Lines("READ t7a" + size.ToString() + ".bin --chunk 5000 | " + Self("bytes-sum"));
            Check("9: Pipe.ReadBytes after READ", fromRead.Count == 1 && fromRead[0] == PatternSum(size));

            List<string> fromLines = Lines("READ t7.txt | CONVERT --from lines | " + Self("bytes-sum"));
            Check("9: Pipe.ReadBytes after a stage writing strings", fromLines.Count == 1 && fromLines[0] == Sum(File.ReadAllBytes("t7.txt")));

            Check("9: Pipe.WriteBytes before WRITE (test 7 made its files that way)", SameFile("t7a1.bin", "t7b1.bin", 1, true));
            File.WriteAllBytes("t7a" + size.ToString() + ".bin", new byte[0]);
            File.WriteAllBytes("t7b" + size.ToString() + ".bin", new byte[0]);

            List<string> text = Lines(Self("text"));
            Check("9: Pipe.WriteText: a string per line",
                  text.Count == 3 && text[0] == "first line" && text[1] == "second line" && text[2] == "third line");
        }

        // ---- 10 ----

        private static int[] Stages(string first, string[] firstArgs, string second, string[] secondArgs)
        {
            var pair = Pipe.Create();
            var a = Process.Start(first, firstArgs, null, pair.WriteEnd);
            var b = Process.Start(second, secondArgs, pair.ReadEnd, null);
            a.WaitForExit();
            b.WaitForExit();
            var codes = new[] { a.ExitCode, b.ExitCode };
            a.Dispose();
            b.Dispose();
            return codes;
        }

        private static void QuietBreaks()
        {
            int[] failed = Stages("DATATEST.EXE", new[] { "--child", "fail" }, "VIEWFILT.EXE", new[] { "0" });
            Check("10: the first stage fails (134), the next ends quietly with 141", failed[0] == 134 && failed[1] == 141);
            int[] early = Stages("PIPEGEN.EXE", new[] { "1000000" }, "DATATEST.EXE", new[] { "--child", "take", "5" });
            Check("10: the last stage leaves after 5 of a million (0), the first ends quietly with 141", early[0] == 141 && early[1] == 0);
            Check("10: the shell's code is the failed stage's", Sh(Self("fail") + " | VIEWFILT 0") == 134);
            Check("10: and 0 when only 141s are left", Sh("PIPEGEN 1000000 | " + Self("take 5")) == 0);
        }

        // ---- 12 ----

        private sealed class ThrowsOnDispose : IDisposable
        {
            public int Disposed;
            public void Dispose()
            {
                Disposed++;
                throw new InvalidOperationException("thrown by Dispose");
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int UseAndDispose(ThrowsOnDispose d, int i)
        {
            int sum = i;
            using (d) { sum += i * 3; }
            return sum;
        }

        private static void ThrowFromFinally()
        {
            var d = new ThrowsOnDispose();
            int caught = 0;
            string marker = "still here";
            for (int i = 0; i < 6; i++)
            {
                try { UseAndDispose(d, i); }
                catch (InvalidOperationException) { caught++; }
            }
            Check("12: six exceptions from Dispose in a using's finally, all caught", caught == 6 && d.Disposed == 6);
            Check("12: the caller's locals intact afterwards", marker.Length == 10);
        }

        // ---- 13: the shape a pipeline's reader had (Dispose ran Closed) ----

        private sealed class Source
        {
            public Action Closed;
            public int Left;

            public Steps GetEnumerator() => new Steps(this);

            public struct Steps : IDisposable
            {
                private readonly Source _source;
                internal Steps(Source source) => _source = source;
                public int Current => _source.Left;
                public bool MoveNext() => _source.Left-- > 0;
                public void Dispose()
                {
                    Action run = _source.Closed;
                    _source.Closed = null;
                    run?.Invoke();
                }
            }
        }

        private sealed class Waiter
        {
            private readonly string _name;
            public Waiter(string name) => _name = name;
            public void Finish()
            {
                System.Threading.Thread.Sleep(1);
                throw new PipelineException(_name, "STAGE", 1);
            }
        }

        private static void ThrowFromEnumeratorDispose()
        {
            int caught = 0;
            for (int i = 0; i < 6; i++)
            {
                var source = new Source { Left = 2, Closed = new Waiter("w" + i.ToString()).Finish };
                PipelineException failed = null;
                try
                {
                    foreach (int v in source) { }
                }
                catch (PipelineException e) { failed = e; }
                if (failed != null && failed.Stage == "STAGE") caught++;
            }
            Check("13: six pipeline exceptions from Dispose, all caught", caught == 6);
        }

        // ---- 14: as the reader of Pipe.From did it before the fix: a pipe
        // closed, a process waited for, then the throw — all inside the
        // enumerator's Dispose, the finally at the end of a normal foreach ----

        private sealed class ServiceWaiter
        {
            public void Finish()
            {
                var p = Process.Start("PIPEGEN.EXE", new[] { "0" });
                p.WaitForExit();
                p.Dispose();
                throw new PipelineException("model", "STAGE", 1);
            }
        }

        private static void ThrowAfterServices()
        {
            int caught = 0;
            for (int i = 0; i < 6; i++)
            {
                var source = new Source { Left = 2, Closed = new ServiceWaiter().Finish };
                PipelineException failed = null;
                try
                {
                    foreach (int v in source)
                    {
                        PipePair pair = Pipe.Create();
                        pair.WriteEnd.Dispose();
                        pair.ReadEnd.Dispose();
                    }
                }
                catch (PipelineException e) { failed = e; }
                if (failed != null) caught++;
            }
            Check("14: six exceptions after services, all caught", caught == 6);
        }

        private static void OldReader()
        {
            PipeClosing.WaitInDispose = true;
            try { JsonErrors(); }
            finally { PipeClosing.WaitInDispose = false; }
        }

        // ---- 11 ----

        private static void PipelinesFromCode()
        {
            Sh("PIPEGEN 5 | CONVERT --to json > t11.json");
            int n = 0;
            foreach (LogEntry e in Pipe.From("READ t11.json | CONVERT --from json").Into<LogEntry>()) n++;
            Check("11: foreach … in Pipe.From(\"READ … | CONVERT --from json\").Into<T>()", n == 5);

            var gen = Process.Start("PIPEGEN.EXE", new[] { "3", "t11log" });
            Pipe.Read("t11log").WriteTo(Pipe.To("CONVERT --to json | WRITE t11err.json"));
            gen.WaitForExit();
            gen.Dispose();
            List<object> written = JsonValues.ParseAll(File.ReadAllBytes("t11err.json"));
            Check("11: Pipe.Read(name).WriteTo(Pipe.To(\"CONVERT --to json | WRITE …\"))",
                  written.Count == 1 && written[0] is List<object> l && l.Count == 3);

            PipelineException missing = null;
            try { foreach (View v in Pipe.From("NOSUCHPROG | VIEWFILT 0")) { } }
            catch (PipelineException e) { missing = e; }
            Check("11: a program that is not there: an exception naming it", missing != null && missing.Stage == "NOSUCHPROG" && missing.ExitCode == 127);

            PipelineException fails = null;
            try { foreach (View v in Pipe.From(Self("fail") + " | VIEWFILT 0")) { } }
            catch (PipelineException e) { fails = e; }
            Check("11: a stage that fails: an exception with its name and code", fails != null && fails.Stage == "DATATEST" && fails.ExitCode == 134);
        }
    }
}
