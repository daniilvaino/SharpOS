using System.Text;
using System.Text.Json;
using TermRace.Suites;

namespace TermRace;

public static class Report
{
	/// <summary>Side-by-side scoreboard: one row per suite, one column block per engine.</summary>
	public static void Table (TextWriter stdout, IReadOnlyList<CaseResult> results, IReadOnlyList<string> engines)
	{
		if (results.Count == 0)
			return;
		var suites = results.Select (r => r.Suite).Distinct ().ToList ();
		var sb = new StringBuilder ();
		sb.Append ("suite".PadRight (16));
		foreach (var engine in engines)
			sb.Append ((engine + " pass/total").PadLeft (26));
		sb.Append ('\n');

		foreach (var suite in suites) {
			sb.Append (suite.PadRight (16));
			foreach (var engine in engines) {
				var cases = results.Where (r => r.Suite == suite && r.Engine == engine).ToList ();
				if (cases.Count == 0) {
					sb.Append ("-".PadLeft (26));
					continue;
				}
				if (suite == "bench") {
					// Averaged over the workloads that completed, so the mix changes when a
					// previously crashing workload starts passing; the ok count makes that
					// visible instead of reading as a throughput or allocation regression.
					var ok = cases.Where (c => c.Status == Status.Pass && c.Milliseconds > 0).ToList ();
					var mib = ok.Select (c => c.InputBytes / (1024.0 * 1024.0) / (c.Milliseconds / 1000.0)).DefaultIfEmpty (0).Average ();
					var alloc = ok.Select (c => (double)c.AllocatedBytes).DefaultIfEmpty (0).Average ();
					sb.Append ($"{mib:0.0} MiB/s {alloc / (1024 * 1024):0.0}x {ok.Count}/{cases.Count} ok".PadLeft (26));
					continue;
				}
				var pass = cases.Count (c => c.Status is Status.Pass or Status.XFail);
				var bad = cases.Count (c => c.Bad);
				var skip = cases.Count (c => c.Status == Status.Skip);
				var cell = $"{pass}/{cases.Count}" + (bad > 0 ? $" bad={bad}" : "") + (skip > 0 ? $" skip={skip}" : "");
				sb.Append (cell.PadLeft (26));
			}
			sb.Append ('\n');
		}
		stdout.Write (sb.ToString ());
	}

	public static void WriteJson (string path, IReadOnlyList<CaseResult> results)
	{
		var payload = results.Select (r => new {
			suite = r.Suite,
			engine = r.Engine,
			@case = r.Case,
			status = r.Status.ToString ().ToLowerInvariant (),
			ms = Math.Round (r.Milliseconds, 3),
			allocatedBytesPerMiB = r.AllocatedBytes,
			detail = r.Detail
		});
		var directory = Path.GetDirectoryName (Path.GetFullPath (path));
		if (!string.IsNullOrEmpty (directory))
			Directory.CreateDirectory (directory);
		File.WriteAllText (path, JsonSerializer.Serialize (payload, new JsonSerializerOptions { WriteIndented = true }));
	}
}
