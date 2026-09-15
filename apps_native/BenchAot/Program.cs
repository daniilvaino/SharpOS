using SharpOS.AppSdk;
using System;
using System.Collections.Generic;
using System.Runtime;
using System.Text;

namespace BenchAot
{
    // Benchmarks for the NativeAOT app tier: a freestanding PE on our std,
    // doing the same work as apps_managed/Bench wherever this tier can, so that
    // docs/perf-progress.md sets the hosted runtime, our own NativeAOT and the
    // host side by side. Results are `[perf] aot.<name>=<value>` lines;
    // tools/perf_report.ps1 compares them with the host's and Linux's Bench
    // numbers for the same work.
    //
    // Differences from Bench, forced by the tier: no JIT benchmark (nothing is
    // compiled at run time), an array sort instead of List<T>.Sort (not in the
    // app std), 48 tasks in batches of 16 instead of 500 (kept from when a task
    // was a thread of its own), and one collection count instead of three
    // (AppGC has no generations). Timed with the HPET the kernel hands over.
    internal static unsafe class AppEntry
    {
        private static ulong* s_counter;
        private static ulong s_frequency;
        private static int s_tasksDone;

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run();
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run();

        private static int Run()
        {
            if (!AppHost.TryGetHpet(out ulong counter, out ulong frequency))
            {
                AppHost.WriteError("[benchaot] no HPET handed over - nothing to time with\n");
                return 1;
            }
            s_counter = (ulong*)counter;
            s_frequency = frequency;

            AppHost.WriteString("=== benchaot begin ===\n");

            // The same output again, before anything else has run: `output`
            // proper comes after the tasks, and 48 threads that finished may
            // still cost the scheduler something. The two apart say whether
            // what output pays is its own.
            Measure("output.first", Output);
            Measure("alloc.short", AllocShortLived);
            Measure("alloc.retained", AllocRetained);
            Measure("strings", Strings);
            Measure("collections", Collections);
            Measure("exceptions", Exceptions);
            if (AppThreads.IsAvailable && TaskBackendInstaller.Install())
            {
                Measure("tasks", Tasks);

                // Again, on the pool threads the first run started: the two
                // apart are what starting those threads cost.
                Measure("tasks.warm", Tasks);
            }
            Measure("output", Output);

            AppHost.WriteString("=== benchaot end ===\n");
            return 0;
        }

        // One read of the free-running counter. Not inlined: ILC may otherwise
        // treat two plain reads of the same address as one.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ulong Now() => *s_counter;

        private static void Measure(string name, Func<long> body)
        {
            uint collections = AppGC.Collections;

            ulong started = Now();
            long operations = body();
            ulong ticks = Now() - started;

            // ticks * 1e9 stays in range for anything under three minutes at
            // the QEMU HPET's 100 MHz; these run for well under a second.
            ulong nanoseconds = ticks * 1_000_000_000UL / s_frequency;
            Report(name + ".us", (long)(nanoseconds / 1000));
            Report(name + ".ops", operations);
            Report(name + ".ns_per_op", operations == 0 ? 0 : (long)(nanoseconds / (ulong)operations));
            Report(name + ".gc", AppGC.Collections - collections);
        }

        private static long AllocShortLived()
        {
            const int Count = 300_000;
            // Each array goes into a small ring of references, so it reaches
            // the heap and dies a few iterations later. Assigning them to one
            // local was not enough: every array but the last was a dead store,
            // and ILC dropped them — the first run measured 3 ns per
            // "allocation" and no collections at all.
            var ring = new object[64];
            for (int i = 0; i < Count; i++)
                ring[i & 63] = new byte[32];
            return ring[0] != null ? Count : 0;
        }

        private static long AllocRetained()
        {
            const int Count = 100_000;
            var keep = new List<object>(Count);
            for (int i = 0; i < Count; i++)
                keep.Add(new byte[64]);
            return keep.Count;
        }

        private static long Strings()
        {
            const int Count = 50_000;
            var builder = new StringBuilder();
            long length = 0;
            for (int i = 0; i < Count; i++)
            {
                string s = i.ToString() + ":" + (i * 7).ToString("X");
                length += s.Length;
                if (builder.Length > 4096) builder.Clear();
                builder.Append(s);
            }
            return length > 0 && builder.Length > 0 ? Count : 0;
        }

        private static long Collections()
        {
            const int Count = 50_000;
            var map = new Dictionary<int, int>();
            for (int i = 0; i < Count; i++)
                map[i * 31] = i;
            long sum = 0;
            for (int i = 0; i < Count; i++)
                if (map.TryGetValue(i * 31, out int v)) sum += v;

            var values = new int[Count];
            for (int i = 0; i < Count; i++)
                values[i] = (i * 7919) % Count;
            Array.Sort(values);
            return sum >= 0 ? Count * 3 : 0;
        }

        private static long Exceptions()
        {
            const int Count = 300;
            int caught = 0;
            for (int i = 0; i < Count; i++)
            {
                try { Throw(i); }
                catch (InvalidOperationException) { caught++; }
            }
            return caught;
        }

        private static void Throw(int i) => throw new InvalidOperationException(i.ToString());

        // In batches of 16, from when a task was a thread of its own and the
        // kernel queued at most 32 not yet started (50 at once faulted). Since
        // step174 tasks run on a pool and the limit is gone; the shape stays so
        // the numbers compare with earlier runs.
        private static long Tasks()
        {
            const int Batches = 3;
            const int Batch = 16;
            s_tasksDone = 0;
            var tasks = new System.Threading.Tasks.Task[Batch];
            for (int b = 0; b < Batches; b++)
            {
                for (int i = 0; i < Batch; i++)
                    tasks[i] = System.Threading.Tasks.Task.Run(() => System.Threading.Interlocked.Increment(ref s_tasksDone));
                for (int i = 0; i < Batch; i++)
                    tasks[i].Wait();
            }
            return s_tasksDone;
        }

        private static long Output()
        {
            const int Count = 200;
            for (int i = 0; i < Count; i++)
                AppHost.WriteString("benchaot output line " + i.ToString() + " ................................................\n");
            return Count;
        }

        private static void Report(string name, long value)
            => AppHost.WriteString("[perf] aot." + name + "=" + value.ToString() + "\n");
    }
}
