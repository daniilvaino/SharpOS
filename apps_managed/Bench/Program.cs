// Benchmarks for the hosted tier (CoreCLR on SharpOS).
//
// A stock .NET console program: the same DLL runs under `dotnet Bench.dll` on
// a desktop, which gives a reference for every number. Each result is one line
// `[perf] bench.<name>=<value>`; tools/perf_report.ps1 collects them together
// with the kernel's own counters for this run (`[perf] run.Bench.*`) and
// compares against the previous run.
//
// Started from the launcher (\SHARPOS\Bench.dll), not at every boot: the
// point is a number taken on purpose, before and after a change.
using System.Diagnostics;

Console.WriteLine("=== bench begin ===");
Clock();
Console.WriteLine("=== bench end ===");
return 0;

// The clock goes first because every other benchmark times itself with it.
//
// Two questions: does it only move forward, and what does a read cost. The
// cost is not measured here — timing a clock with itself answers nothing —
// but by the kernel around this run (run.Bench.clock.calls / avg_ns). What
// this side can see is going backwards, and how far its own idea of the
// elapsed time is from the kernel's (clock.self_timed_ms against
// run.Bench.wall_ms).
static void Clock()
{
    const int Reads = 20_000;

    Stopwatch self = Stopwatch.StartNew();

    long previous = Stopwatch.GetTimestamp();
    long backsteps = 0;
    long largestBackstep = 0;
    for (int i = 0; i < Reads; i++)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < previous)
        {
            backsteps++;
            largestBackstep = Math.Max(largestBackstep, previous - now);
        }
        previous = now;
    }

    DateTime previousUtc = DateTime.UtcNow;
    long utcBacksteps = 0;
    for (int i = 0; i < Reads; i++)
    {
        DateTime now = DateTime.UtcNow;
        if (now < previousUtc)
            utcBacksteps++;
        previousUtc = now;
    }

    self.Stop();

    Report("clock.reads", Reads * 2);
    Report("clock.stopwatch.backsteps", backsteps);
    Report("clock.stopwatch.max_backstep_us", largestBackstep * 1_000_000 / Stopwatch.Frequency);
    Report("clock.utcnow.backsteps", utcBacksteps);
    Report("clock.frequency_hz", Stopwatch.Frequency);
    Report("clock.self_timed_ms", self.ElapsedMilliseconds);
}

static void Report(string name, long value)
    => Console.WriteLine($"[perf] bench.{name}={value}");
