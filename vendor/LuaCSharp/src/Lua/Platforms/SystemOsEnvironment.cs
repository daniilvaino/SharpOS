using System.Diagnostics;

namespace Lua.Platforms;

/// <summary>
/// Default implementation of ILuaEnvironment
/// </summary>
public sealed class SystemOsEnvironment : ILuaOsEnvironment
{
    public string? GetEnvironmentVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name);
    }

    public ValueTask Exit(int exitCode, CancellationToken cancellationToken)
    {
        Environment.Exit(exitCode);
        return default;
    }

    // SharpOS cut: was Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds — the kernel
    // keeps no per-process CPU time. Wall time since the app started instead, which is what
    // C's clock() — the reference Lua's os.clock — returns on Windows.
    // Without a counter frequency (no HPET handed over) 0/0 would make it NaN:
    // the wall clock's ticks then.
    static readonly long startTimestamp = Stopwatch.Frequency != 0 ? Stopwatch.GetTimestamp() : DateTime.UtcNow.Ticks;

    public double GetTotalProcessorTime()
    {
        // The start first: its initializer runs at the first read of the field,
        // and a "now" taken before that would be earlier than the start — the
        // first os.clock() came out negative.
        long start = startTimestamp;
        return Stopwatch.Frequency != 0
            ? (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency
            : (DateTime.UtcNow.Ticks - start) / (double)TimeSpan.TicksPerSecond;
    }
}
