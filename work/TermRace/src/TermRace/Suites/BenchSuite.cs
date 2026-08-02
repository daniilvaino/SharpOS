using TermRace.Engines;

namespace TermRace.Suites;

/// <summary>
/// Throughput and allocation race. vtebench's own cases are POSIX shell generators, so the
/// load here is the tmux corpus concatenated into per-workload streams: bytes/second and
/// allocated bytes per MiB of input, measured with GC.GetAllocatedBytesForCurrentThread.
/// </summary>
public sealed class BenchSuite : ISuite
{
	public string Name => "bench";
	public string CorpusDirectory => Path.Combine ("tmux", "corpus");

	/// <summary>Input bytes fed per workload; corpus files are repeated to reach it.</summary>
	public int TargetBytes = 8 * 1024 * 1024;

	public IEnumerable<CaseResult> Run (Func<ITerminalEngine> engineFactory, RunContext context)
	{
		var dir = Path.Combine (context.TestsRoot, CorpusDirectory);
		// tmux seeds are named "<workload>.NNNN"; group them back into workloads.
		var workloads = Directory.EnumerateFiles (dir, "*.*")
			.Where (f => System.Text.RegularExpressions.Regex.IsMatch (Path.GetFileName (f), @"\.[0-9]{4}$"))
			.GroupBy (f => System.Text.RegularExpressions.Regex.Replace (Path.GetFileName (f), @"\.[0-9]{4}$", ""))
			.OrderBy (g => g.Key, StringComparer.Ordinal)
			.ToList ();

		foreach (var name in context.Pick (workloads.Select (w => w.Key))) {
			var group = workloads.First (w => w.Key == name);
			var payload = Concatenate (group.OrderBy (f => f, StringComparer.Ordinal), TargetBytes);
			var engine = engineFactory ();

			// Warm the engine and the JIT so the measured pass is steady state. An engine that
			// dies on this workload is reported here rather than taking the process down.
			var warmup = Watchdog.Run (() => {
				engine.Reset (80, 25);
				engine.Feed (payload, 0, Math.Min (payload.Length, 256 * 1024));
				engine.Reset (80, 25);
			}, TimeSpan.FromSeconds (Math.Max (60, context.Timeout.TotalSeconds)), out var warmupDetail, out var warmupMs);
			if (warmup != Status.Pass) {
				yield return new CaseResult {
					Suite = Name, Case = name, Engine = engine.Name, Status = warmup,
					Detail = warmupDetail, Milliseconds = warmupMs
				};
				continue;
			}

			long allocated = 0;
			double ms = 0;
			var status = Watchdog.Run (() => {
				var before = GC.GetAllocatedBytesForCurrentThread ();
				var watch = System.Diagnostics.Stopwatch.StartNew ();
				const int chunk = 4096;
				for (int offset = 0; offset < payload.Length; offset += chunk)
					engine.Feed (payload, offset, Math.Min (chunk, payload.Length - offset));
				_ = engine.Snapshot ();
				watch.Stop ();
				allocated = GC.GetAllocatedBytesForCurrentThread () - before;
				ms = watch.Elapsed.TotalMilliseconds;
			}, TimeSpan.FromSeconds (Math.Max (60, context.Timeout.TotalSeconds)), out var detail, out var wallMs);

			var megabytes = payload.Length / (1024.0 * 1024.0);
			yield return new CaseResult {
				Suite = Name, Case = name, Engine = engine.Name, Status = status,
				Milliseconds = status == Status.Pass ? ms : wallMs,
				InputBytes = payload.Length,
				AllocatedBytes = status == Status.Pass ? (long)(allocated / megabytes) : 0,
				Detail = status == Status.Pass
					? $"{megabytes:0.0} MiB in {ms:0} ms = {megabytes / (ms / 1000.0):0.0} MiB/s, {allocated / megabytes / (1024 * 1024):0.00} MiB alloc per MiB in"
					: detail
			};
		}
	}

	static byte [] Concatenate (IEnumerable<string> files, int targetBytes)
	{
		var output = new List<byte> (targetBytes + 4096);
		var sources = files.Select (File.ReadAllBytes).Where (b => b.Length > 0).ToList ();
		if (sources.Count == 0)
			return Array.Empty<byte> ();
		int index = 0;
		while (output.Count < targetBytes) {
			output.AddRange (sources [index % sources.Count]);
			index++;
		}
		return output.ToArray ();
	}
}
