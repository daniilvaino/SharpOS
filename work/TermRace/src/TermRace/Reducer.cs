using System.Text;
using TermRace.Engines;
using TermRace.Suites;

namespace TermRace;

/// <summary>
/// Feeds one corpus file to one engine and, if it crashes or hangs, shrinks the input to a
/// minimal byte sequence that still reproduces the same failure site. Greedy delta
/// debugging: repeatedly try to drop a contiguous chunk, keep the drop when the failure
/// survives, halve the chunk size when nothing can be dropped.
/// </summary>
public static class Reducer
{
	public static int Run (TextWriter stdout, string engineName, string path, TimeSpan timeout, string outputPath)
	{
		var data = File.ReadAllBytes (path);
		var original = Failure (engineName, data, timeout);
		if (original == null) {
			stdout.WriteLine ($"{Path.GetFileName (path)} does not fail on {engineName} ({data.Length} bytes)");
			return 0;
		}
		stdout.WriteLine ($"{Path.GetFileName (path)}: {original.Summary} at {original.Site}");
		stdout.WriteLine ($"reducing {data.Length} bytes…");

		var current = data;
		int chunk = Math.Max (1, current.Length / 2);
		while (chunk >= 1) {
			bool shrank = false;
			for (int start = 0; start + chunk <= current.Length;) {
				var candidate = Without (current, start, chunk);
				var failure = Failure (engineName, candidate, timeout);
				// Same failure site only: dropping bytes must not turn one bug into another.
				if (failure != null && failure.Site == original.Site) {
					current = candidate;
					shrank = true;
				} else {
					start += chunk;
				}
			}
			if (!shrank || chunk == 1)
				chunk /= 2;
		}

		stdout.WriteLine ($"minimal repro: {current.Length} bytes");
		stdout.WriteLine (Escape (current));
		stdout.WriteLine (original.Detail);

		if (outputPath != null) {
			var directory = Path.GetDirectoryName (Path.GetFullPath (outputPath));
			if (!string.IsNullOrEmpty (directory))
				Directory.CreateDirectory (directory);
			File.WriteAllBytes (outputPath, current);
			stdout.WriteLine ($"written to {outputPath}");
		}
		return 1;
	}

	/// <summary>
	/// Feeds one file and prints the resulting grid — the quickest way to see what a sequence
	/// actually does to an engine, next to what a fixture expects.
	/// </summary>
	public static int Dump (TextWriter stdout, string engineName, string path, int cols, int rows)
	{
		var data = File.ReadAllBytes (path);
		var engine = Engines.Engines.Create (engineName);
		engine.Reset (cols, rows);
		engine.Feed (data, 0, data.Length);
		var screen = engine.Snapshot ();
		stdout.WriteLine ($"{engineName}: {cols}x{rows}, cursor {screen.CursorX},{screen.CursorY}");
		for (int y = 0; y < screen.Rows; y++)
			stdout.WriteLine ($"{y,3}|{Diff.Visible (screen.RowTextTrimmed (y))}");
		return 0;
	}

	sealed class Crash
	{
		public string Summary;
		public string Site;
		public string Detail;
	}

	static Crash Failure (string engineName, byte [] data, TimeSpan timeout)
	{
		var engine = Engines.Engines.Create (engineName);
		var status = Watchdog.Run (() => {
			engine.Reset (80, 25);
			const int chunk = 1024;
			for (int offset = 0; offset < data.Length; offset += chunk)
				engine.Feed (data, offset, Math.Min (chunk, data.Length - offset));
			var screen = engine.Snapshot ();
			for (int y = 0; y < screen.Rows; y++)
				_ = screen.RowText (y);
		}, timeout, out var detail, out _);

		if (status == Status.Pass)
			return null;
		var lines = (detail ?? "").Split ('\n');
		return new Crash {
			Summary = lines [0],
			// The topmost engine frame identifies the bug; message text alone does not.
			Site = lines.Length > 1 ? lines [1].Split (" in ") [0] : lines [0],
			Detail = detail
		};
	}

	static byte [] Without (byte [] data, int start, int count)
	{
		var output = new byte [data.Length - count];
		Array.Copy (data, 0, output, 0, start);
		Array.Copy (data, start + count, output, start, data.Length - start - count);
		return output;
	}

	static string Escape (byte [] data)
	{
		var sb = new StringBuilder ();
		foreach (var b in data) {
			if (b == 0x1b)
				sb.Append ("\\e");
			else if (b >= 0x20 && b < 0x7f && b != (byte)'\\')
				sb.Append ((char)b);
			else
				sb.Append ("\\x").Append (b.ToString ("x2"));
		}
		return sb.ToString ();
	}
}
