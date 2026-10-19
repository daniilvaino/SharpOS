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
//
// One benchmark per suspect in work/perf-suspects.md, sized to take well under
// a second each under QEMU's software CPU. Timed with Stopwatch — trustworthy
// since step168; the clock benchmark stays first to keep it that way.
using System.Diagnostics;
using System.Reflection.Emit;
using System.Text;

Console.WriteLine("=== bench begin ===");

Clock();
Measure("alloc.short", AllocShortLived);
Measure("alloc.retained", AllocRetained);
Measure("strings", Strings);
Measure("collections", Collections);
Measure("exceptions", Exceptions);
Measure("jit", Jit);
Measure("tasks", Tasks);
Measure("output", Output);

// PIPEPERF's baseline (step195/208): BinaryWriter + BinaryReader over a reused
// MemoryStream, the same three graphs. The same DLL on the desktop and on
// SharpOS's CoreCLR says what this costs on one runtime under the two; the
// NativeAOT std's own numbers come from PIPEPERF. Warmed first so the timed
// round runs tiered-up code on both.
BinaryGraphs.WarmUp();
Measure("binary.small", BinaryGraphs.Small);
Measure("binary.holder", BinaryGraphs.Holder);
Measure("binary.bag", BinaryGraphs.Bag);
BinaryGraphs.BigWarmUp();
Measure("binary.big", BinaryGraphs.Big);
SharpOS.Micro.MicroBench.Run();

// One benchmark again, long enough for the kernel's sampling profiler to see
// it: 100 samples a second get three or four points out of the 36 ms the
// regular exceptions benchmark takes. Chosen by the kernel through the
// environment (Probes.BenchProfile); unset on a desktop, so the host
// reference never runs it.
if (Environment.GetEnvironmentVariable("SHARPOS_BENCH_PROFILE") == "exceptions")
    Measure("exceptions.profile", () => ThrowAndCatch(10_000));

Console.WriteLine("=== bench end ===");
return 0;

// The clock goes first because every other benchmark times itself with it.
//
// Two questions: does it only move forward, and what does a read cost. The
// cost is measured by the kernel around this run (run.Bench.clock.calls /
// avg_ns) — timing a clock with itself answers nothing. What this side can see
// is going backwards, and how far its own idea of the elapsed time is from the
// kernel's (clock.self_timed_ms against run.Bench.wall_ms).
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

// Runs one benchmark: total time, time per operation, and how many
// collections of each generation it caused. The collections are half the
// story for the allocation suspects — a gen0 budget of 256 KB (suspect 7)
// shows up as a collection count, not as a slow allocation.
static void Measure(string name, Func<long> body)
{
    int gen0 = GC.CollectionCount(0);
    int gen1 = GC.CollectionCount(1);
    int gen2 = GC.CollectionCount(2);

    long started = Stopwatch.GetTimestamp();
    long operations = body();
    long ticks = Stopwatch.GetTimestamp() - started;

    long microseconds = ticks * 1_000_000 / Stopwatch.Frequency;
    Report(name + ".us", microseconds);
    Report(name + ".ops", operations);
    Report(name + ".ns_per_op", operations == 0 ? 0 : ticks * 1_000_000_000 / Stopwatch.Frequency / operations);
    Report(name + ".gc0", GC.CollectionCount(0) - gen0);
    Report(name + ".gc1", GC.CollectionCount(1) - gen1);
    Report(name + ".gc2", GC.CollectionCount(2) - gen2);
}

// Small objects that die at once: the allocator's fast path and the gen0
// budget (suspects 7, 10, 11).
static long AllocShortLived()
{
    const int Count = 300_000;
    object? last = null;
    for (int i = 0; i < Count; i++)
        last = new byte[32];
    GC.KeepAlive(last);
    return Count;
}

// Objects that survive: promotion, and the cost of a heap that keeps growing
// (suspect 8, heap limits; 12, page commit).
static long AllocRetained()
{
    const int Count = 100_000;
    var keep = new List<object>(Count);
    for (int i = 0; i < Count; i++)
        keep.Add(new byte[64]);
    GC.KeepAlive(keep);
    return Count;
}

// Formatting and concatenation — allocation plus CoreLib code paths.
static long Strings()
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
    GC.KeepAlive(length);
    return Count;
}

// Hashing and sorting: generic code over value types, interface dispatch.
static long Collections()
{
    const int Count = 50_000;
    var map = new Dictionary<int, int>();
    for (int i = 0; i < Count; i++)
        map[i * 31] = i;
    long sum = 0;
    for (int i = 0; i < Count; i++)
        if (map.TryGetValue(i * 31, out int v)) sum += v;

    var list = new List<int>(Count);
    for (int i = 0; i < Count; i++)
        list.Add((i * 7919) % Count);
    list.Sort();
    GC.KeepAlive(sum);
    return Count * 3;
}

// Throw and catch one frame down. Every step of an unwind looks up function
// tables, which on SharpOS is a linear walk over every loaded image (suspect
// 23).
static long Exceptions() => ThrowAndCatch(300);

static long ThrowAndCatch(int count)
{
    int caught = 0;
    for (int i = 0; i < count; i++)
    {
        try { Throw(i); }
        catch (InvalidOperationException) { caught++; }
    }
    return caught;

    static void Throw(int i) => throw new InvalidOperationException(i.ToString());
}

// Compile and run fresh methods: the JIT itself, and what it asks the runtime
// for on every call it imports (suspects 17-20).
static long Jit()
{
    const int Count = 100;
    long total = 0;
    for (int i = 0; i < Count; i++)
    {
        var method = new DynamicMethod("bench" + i, typeof(int), new[] { typeof(int) });
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, i);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ret);
        var invoke = (Func<int, int>)method.CreateDelegate(typeof(Func<int, int>));
        total += invoke(i);
    }
    GC.KeepAlive(total);
    return Count;
}

// Work handed to the thread pool and waited for: thread wake-up, waits, the
// scheduler (suspects 26-29).
static long Tasks()
{
    const int Count = 500;
    var tasks = new Task[Count];
    int done = 0;
    for (int i = 0; i < Count; i++)
        tasks[i] = Task.Run(() => Interlocked.Increment(ref done));
    Task.WaitAll(tasks);
    return done;
}

// Lines of program output: the console path, the serial port and the disk
// log together (suspects 33-35). The kernel's counters for this run say how
// much of it was the disk log (run.Bench.disklog.*).
static long Output()
{
    const int Count = 200;
    for (int i = 0; i < Count; i++)
        Console.WriteLine("bench output line " + i.ToString() + " ................................................");
    return Count;
}

static void Report(string name, long value)
    => Console.WriteLine($"[perf] bench.{name}={value}");

// The graphs and calls of PIPEPERF's Baseline, one operation a message
// written and read back.
static class BinaryGraphs
{
    private sealed class SmallMessage { public int A; public int B; public string S = ""; }
    private sealed class Item { public int X; public int Y; }
    private sealed class HolderMessage { public int Id; public Item[] Items = Array.Empty<Item>(); }

    private static readonly MemoryStream s_stream = new MemoryStream();
    private static readonly BinaryWriter s_writer = new BinaryWriter(s_stream);
    private static readonly BinaryReader s_reader = new BinaryReader(s_stream);

    private static readonly SmallMessage s_small = new SmallMessage { A = 1, B = 2, S = "small" };
    private static readonly HolderMessage s_holder = MakeHolder();
    private static readonly Dictionary<string, object> s_bag = MakeBag();

    private static HolderMessage MakeHolder()
    {
        var h = new HolderMessage { Id = 7, Items = new Item[100] };
        for (int i = 0; i < 100; i++) h.Items[i] = new Item { X = i, Y = i * 2 };
        return h;
    }

    private static Dictionary<string, object> MakeBag()
    {
        var bag = new Dictionary<string, object>();
        for (int i = 0; i < 8; i++)
        {
            if ((i & 1) == 0) bag["k" + i] = i;
            else bag["k" + i] = "v" + i;
        }
        return bag;
    }

    // The large message, BigGraph.cs as PIPEPERF has it (step208).
    private static readonly SharpOS.Micro.BigRecord[] s_big = SharpOS.Micro.BigGraph.Build();

    public static void BigWarmUp()
    {
        for (int round = 0; round < 3; round++) Big();
    }

    public static long Big()
    {
        const int Count = 20;
        for (int n = 0; n < Count; n++)
        {
            s_stream.Position = 0;
            SharpOS.Micro.BigGraph.Write(s_writer, s_big);
            s_stream.Position = 0;
            GC.KeepAlive(SharpOS.Micro.BigGraph.Read(s_reader));
        }
        return Count;
    }

    public static void WarmUp()
    {
        for (int round = 0; round < 3; round++) { Small(); Holder(); Bag(); }
    }

    public static long Small()
    {
        const int Count = 200_000;
        for (int i = 0; i < Count; i++)
        {
            s_stream.Position = 0;
            s_writer.Write(s_small.A); s_writer.Write(s_small.B); s_writer.Write(s_small.S);
            s_stream.Position = 0;
            var back = new SmallMessage { A = s_reader.ReadInt32(), B = s_reader.ReadInt32(), S = s_reader.ReadString() };
            GC.KeepAlive(back);
        }
        return Count;
    }

    public static long Holder()
    {
        const int Count = 2_000;
        for (int n = 0; n < Count; n++)
        {
            s_stream.Position = 0;
            s_writer.Write(s_holder.Id);
            s_writer.Write(s_holder.Items.Length);
            foreach (Item it in s_holder.Items) { s_writer.Write(it.X); s_writer.Write(it.Y); }
            s_stream.Position = 0;
            var h = new HolderMessage { Id = s_reader.ReadInt32() };
            h.Items = new Item[s_reader.ReadInt32()];
            for (int i = 0; i < h.Items.Length; i++) h.Items[i] = new Item { X = s_reader.ReadInt32(), Y = s_reader.ReadInt32() };
            GC.KeepAlive(h);
        }
        return Count;
    }

    public static long Bag()
    {
        const int Count = 20_000;
        for (int n = 0; n < Count; n++)
        {
            s_stream.Position = 0;
            s_writer.Write(s_bag.Count);
            foreach (var pair in s_bag)
            {
                s_writer.Write(pair.Key);
                if (pair.Value is int v) { s_writer.Write((byte)0); s_writer.Write(v); }
                else { s_writer.Write((byte)1); s_writer.Write((string)pair.Value); }
            }
            s_stream.Position = 0;
            var x = new Dictionary<string, object>();
            int count = s_reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string key = s_reader.ReadString();
                if (s_reader.ReadByte() == 0) x[key] = s_reader.ReadInt32();
                else x[key] = s_reader.ReadString();
            }
            GC.KeepAlive(x);
        }
        return Count;
    }
}
