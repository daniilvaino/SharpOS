using System.Text;
using TermRace.Engines;

namespace TermRace.Suites;

/// <summary>
/// tests/xtermjs — pairs of `NAME.in` (raw bytes) and `NAME.text` (expected text grid).
/// Mirrors shitty's adapter.py: reset, feed the fixture through a default PTY line
/// discipline, dump the viewport with trailing blanks stripped, diff.
/// </summary>
public sealed class XtermJsSuite : ISuite
{
	public string Name => "xtermjs";
	public string CorpusDirectory => "xtermjs";

	const int Cols = 80;
	const int Rows = 25;

	public IEnumerable<CaseResult> Run (Func<ITerminalEngine> engineFactory, RunContext context)
	{
		var dir = Path.Combine (context.TestsRoot, CorpusDirectory);
		// tests/xtermjs/xfail.txt lists shitty's deliberate deviations from the fixtures.
		// Both engines here are xterm.js ports and the fixtures are xterm.js's own, so
		// every case is expected to pass and no waiver list is applied.
		var cases = Directory.EnumerateFiles (dir, "*.in")
			.Select (Path.GetFileNameWithoutExtension)
			.Where (c => File.Exists (Path.Combine (dir, c + ".text")))
			.OrderBy (c => c, StringComparer.Ordinal);

		var engine = engineFactory ();
		foreach (var name in context.Pick (cases)) {
			var input = ThroughDefaultPty (File.ReadAllBytes (Path.Combine (dir, name + ".in")));
			var expected = Normalize (File.ReadAllText (Path.Combine (dir, name + ".text")));

			string actual = null;
			var status = Watchdog.Run (() => {
				engine.Reset (Cols, Rows);
				// The upstream adapter starts every case from RIS + home.
				var preamble = new byte [] { 0x1b, (byte)'c', 0x1b, (byte)'[', (byte)'H' };
				engine.Feed (preamble, 0, preamble.Length);
				engine.Feed (input, 0, input.Length);
				actual = Dump (engine.Snapshot ());
			}, context.Timeout, out var detail, out var ms);

			var result = new CaseResult {
				Suite = Name, Case = name, Engine = engine.Name, Milliseconds = ms,
				Status = status, Detail = detail
			};
			if (status != Status.Pass) {
				engine = engineFactory ();
				yield return result;
				continue;
			}

			if (Normalize (actual) != expected) {
				result.Status = Status.Fail;
				result.Detail = Diff.Unified (expected, Normalize (actual), name + ".text", engine.Name);
			}
			yield return result;
		}
	}

	/// <summary>
	/// xterm.js writes each fixture to a fresh slave PTY; Linux OPOST|ONLCR expands every
	/// LF, including the LF of an existing CRLF pair.
	/// </summary>
	static byte [] ThroughDefaultPty (byte [] data)
	{
		var output = new List<byte> (data.Length + 64);
		foreach (var b in data) {
			if (b == (byte)'\n')
				output.Add ((byte)'\r');
			output.Add (b);
		}
		return output.ToArray ();
	}

	static string Dump (Screen screen)
	{
		var sb = new StringBuilder ();
		for (int y = 0; y < screen.Rows; y++)
			sb.Append (screen.RowTextTrimmed (y)).Append ('\n');
		return sb.ToString ();
	}

	static string Normalize (string text)
		=> string.Join ("\n", text.Replace ("\r\n", "\n").Split ('\n').Select (l => l.TrimEnd ()));
}

public static class XfailFile
{
	public static HashSet<string> Load (string path)
	{
		var set = new HashSet<string> (StringComparer.Ordinal);
		if (!File.Exists (path))
			return set;
		foreach (var raw in File.ReadAllLines (path)) {
			var line = raw.Trim ();
			if (line.Length != 0 && !line.StartsWith ("#", StringComparison.Ordinal))
				set.Add (line);
		}
		return set;
	}
}
