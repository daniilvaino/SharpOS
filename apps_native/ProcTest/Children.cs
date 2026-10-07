using System;
using System.Threading;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // The other side of a test: PROCTEST.EXE --child ROLE ..., started by the
    // tests in Program.cs. Each role is a few lines; the exit code is what the
    // test checks, beside what went through the pipes.
    internal static unsafe partial class AppEntry
    {
        private static int s_marker;

        private static int Child(string[] args)
        {
            string role = args.Length > 1 ? args[1] : "";
            string a2 = args.Length > 2 ? args[2] : "";
            string a3 = args.Length > 3 ? args[3] : "";

            switch (role)
            {
                case "nop":
                    return 0;

                // Test 1: where this process lives.
                case "where":
                {
                    object heap = new int[4];
                    using var o = Pipe.Write<Where>();
                    o.Copy(new Where
                    {
                        Image = SharpOS.Std.NoRuntime.GcStaticsInit.ImageBase,
                        Static = (ulong)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_marker),
                        Heap = System.Runtime.CompilerServices.Unsafe.As<object, ulong>(ref heap),
                        Id = Process.CurrentId,
                    });
                    return heap != null ? 0 : 1;
                }

                // Test 3: writers by name — a class with methods, the record, an Expando.
                case "name-square":
                {
                    using var w = Pipe.Write<Square>(a2);
                    int n = int.Parse(a3);
                    for (int i = 0; i < n; i++) w.Copy(new Square { Sides = 4, Name = "sq" + i.ToString(), Side = i });
                    return 0;
                }
                case "name-seq":
                {
                    using var w = Pipe.Write<Seq>(a2);
                    int n = int.Parse(a3);
                    for (int i = 0; i < n; i++) w.Copy(new Seq { N = i, Text = "n" + i.ToString() });
                    return 0;
                }
                case "name-expando":
                {
                    using var w = Pipe.Write<Expando>(a2);
                    int n = int.Parse(a3);
                    for (int i = 0; i < n; i++)
                    {
                        var x = new Expando();
                        x["N"] = i;
                        x["Text"] = "x" + i.ToString();
                        w.Copy(x);
                    }
                    return 0;
                }

                // Test 5: n numbers to the standard output; exit 0 when the
                // writer had to wait for its reader — one write, past the full
                // queue, took 20 ms or more (the reader starts 300 ms late) —
                // and 3 when none did.
                case "seq":
                {
                    int n = int.Parse(a2);
                    long longest = 0;
                    using (var o = Pipe.Write<Seq>())
                        for (int i = 0; i < n; i++)
                        {
                            var clock = System.Diagnostics.Stopwatch.StartNew();
                            o.Copy(new Seq { N = i });
                            long took = clock.ElapsedMilliseconds;
                            if (took > longest) longest = took;
                        }
                    return longest >= 20 ? 0 : 3;
                }

                // Test 6: ten records queued, then the writer goes — by exit,
                // by an exception, or by Kill while it waits.
                case "write10":
                {
                    var o = Pipe.Write<Seq>();
                    for (int i = 0; i < 10; i++) o.Copy(new Seq { N = i });
                    if (a2 == "exit") return 0;
                    if (a2 == "throw") throw new InvalidOperationException("the writer dies with ten queued");
                    using (var ready = Pipe.Write<Seq>("t6.ready")) ready.Copy(new Seq { N = 10 });
                    foreach (View v in Pipe.Read()) { }       // nobody writes: waits until killed
                    return 0;
                }

                // Test 6: a reader that goes, by exit or by closing, once told.
                case "reader-go":
                {
                    foreach (Seq s in Pipe.Read<Seq>("t6.go")) break;
                    if (a2 == "close")
                    {
                        Pipe.Read().Dispose();
                        Thread.Sleep(200);
                    }
                    return 0;
                }

                // Test 7: a worker throws while the main thread waits on a
                // pipe, sleeps, or computes.
                case "worker-throws":
                {
                    // Left open: the process's end, not a close, ends the stream.
                    var o = Pipe.Write<Seq>();
                    for (int i = 0; i < 3; i++) o.Copy(new Seq { N = i });
                    AppThreads.Spawn(&WorkerThrows);
                    if (a2 == "pipe") foreach (View v in Pipe.Read()) { }
                    else if (a2 == "sleep") Thread.Sleep(60000);
                    else { long n = 0; while (true) n++; }
                    return 0;
                }

                // Test 8: workers in the app's code, on a pipe, asleep, and in
                // the file service; then Kill, or the main thread returns.
                case "busy":
                {
                    AppThreads.Spawn(&Spin);
                    AppThreads.Spawn(&WaitOnInput);
                    AppThreads.Spawn(&SleepLong);
                    AppThreads.Spawn(&ReadFileForever);
                    Thread.Sleep(100);
                    using (var ready = Pipe.Write<Seq>("t8.ready")) ready.Copy(new Seq { N = 8 });
                    if (a2 == "exit") return 5;
                    Thread.Sleep(600000);
                    return 0;
                }
                case "readfile":
                    return System.IO.File.ReadAllBytes(BigFile).Length > 0 ? 0 : 1;

                // Test 9.
                case "reopen":
                {
                    Pipe.Read().Dispose();
                    try { Pipe.Read(); }
                    catch (InvalidOperationException) { return 0; }
                    return 1;
                }
                case "no-input":
                {
                    int n = 0;
                    foreach (Seq s in Pipe.Read<Seq>()) n++;
                    return 10 + n;
                }

                // Test 10: doubles what comes in.
                case "echo":
                {
                    using var o = Pipe.Write<Seq>();
                    foreach (Seq s in Pipe.Read<Seq>()) o.Copy(new Seq { N = s.N * 2, Text = s.Text.ToHeap() });
                    return 0;
                }
                case "orphan-launcher":
                {
                    Process child = Process.Start("PROCTEST.EXE", StressArgs.Pass("--child", "orphan-child"));
                    return (int)child.Id > 0 ? 0 : 1;
                }
                case "orphan-child":
                {
                    Thread.Sleep(300);
                    using var w = Pipe.Write<Seq>("t10.orphan");
                    w.Copy(new Seq { N = 77 });
                    return 0;
                }

                // Test 11: three stages; the first says where its block is.
                case "t11-gen":
                {
                    ulong block;
                    using (var o = Pipe.Write<Square>())
                    {
                        o.Copy(new Square { Sides = 4, Name = "eleven", Side = 11 });
                        block = o.LastBlock;
                    }
                    using (var w = Pipe.Write<Address>("t11.addr")) w.Copy(new Address { Block = block });
                    return 0;
                }
                case "t11-class":
                    Pipe.Read<Square>().Where(s => s.Area() > 0).WriteTo();
                    return 0;
                case "t11-raw":
                    Pipe.Read().WriteTo();
                    return 0;

                // Test 12: lives until its input closes.
                case "wait-stdin":
                    foreach (View v in Pipe.Read()) { }
                    return 0;

                // Test 14: n records to the standard output, as fast as they
                // go; the exit code is the nanoseconds a write took on average.
                case "flood":
                {
                    int n = int.Parse(a2);
                    using var o = Pipe.Write<Seq>();
                    var s = new Seq { N = 0, Text = "f" };
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < n; i++) { s.N = i; o.Copy(s); }
                    return (int)(clock.ElapsedTicks * 1000000000 / System.Diagnostics.Stopwatch.Frequency / n);
                }
            }

            Console.WriteLine("[proctest] unknown child role " + role);
            return 2;
        }

        // Thread entries: threads of the app's own, not of the task pool —
        // an exception in a task is the task's, not unhandled.
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WorkerThrows()
        {
            Thread.Sleep(50);
            throw new InvalidOperationException("a worker's unhandled exception");
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void Spin()
        {
            long n = 0;
            while (true) n++;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WaitOnInput()
        {
            foreach (View v in Pipe.Read()) { }
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void SleepLong() => Thread.Sleep(600000);

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void ReadFileForever()
        {
            while (true) System.IO.File.ReadAllBytes(BigFile);
        }

        // A file large enough that reading it takes a while in the kernel.
        private const string BigFile = "\\apps\\AOTTESTS.EXE";
    }
}
