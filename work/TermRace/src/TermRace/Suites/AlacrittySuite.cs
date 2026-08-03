using System.Text;
using System.Text.Json;
using TermRace.Engines;

namespace TermRace.Suites;

/// <summary>
/// tests/alacritty — one directory per case: `alacritty.recording` (raw session bytes),
/// `size.json` (grid size) and `grid.json` (alacritty's serialized grid, one record per
/// cell). Feeds the recording and compares our grid against theirs.
///
/// The grid is a bottom-up ring buffer: `raw.inner` is in storage order with index zero the
/// newest line, and `raw.zero` says where that index currently sits, so the rows have to be
/// rotated and reversed before comparing.
/// </summary>
public sealed class AlacrittySuite : ISuite
{
	public string Name => "alacritty";
	public string CorpusDirectory => "alacritty";

	public IEnumerable<CaseResult> Run (Func<ITerminalEngine> engineFactory, RunContext context)
	{
		var root = Path.Combine (context.TestsRoot, CorpusDirectory);
		var cases = Directory.EnumerateDirectories (root)
			.Where (d => File.Exists (Path.Combine (d, "alacritty.recording")))
			.Select (Path.GetFileName)
			.OrderBy (c => c, StringComparer.Ordinal);

		var engine = engineFactory ();
		foreach (var name in context.Pick (cases)) {
			var dir = Path.Combine (root, name);
			var recording = File.ReadAllBytes (Path.Combine (dir, "alacritty.recording"));
			var size = JsonDocument.Parse (File.ReadAllText (Path.Combine (dir, "size.json"))).RootElement;
			var cols = size.GetProperty ("columns").GetInt32 ();
			var rows = size.GetProperty ("screen_lines").GetInt32 ();

			Screen actual = null;
			var status = Watchdog.Run (() => {
				engine.Reset (cols, rows);
				const int chunk = 4096;
				for (int offset = 0; offset < recording.Length; offset += chunk)
					engine.Feed (recording, offset, Math.Min (chunk, recording.Length - offset));
				actual = engine.Snapshot ();
			}, context.Timeout, out var detail, out var ms);

			var result = new CaseResult {
				Suite = Name, Case = name, Engine = engine.Name,
				Status = status, Detail = detail, Milliseconds = ms
			};
			if (status != Status.Pass) {
				engine = engineFactory ();
				yield return result;
				continue;
			}

			var expected = ExpectedRows (Path.Combine (dir, "grid.json"), rows);
			var mine = Enumerable.Range (0, actual.Rows).Select (actual.RowTextTrimmed).ToArray ();
			if (!expected.SequenceEqual (mine)) {
				result.Status = Status.Fail;
				result.Detail = Diff.Unified (string.Join ("\n", expected), string.Join ("\n", mine),
					"alacritty grid", engine.Name);
			}
			yield return result;
		}
	}

	static string [] ExpectedRows (string path, int rows)
	{
		var grid = JsonDocument.Parse (File.ReadAllText (path)).RootElement;
		var raw = grid.GetProperty ("raw");
		var storage = raw.GetProperty ("inner");
		var count = storage.GetArrayLength ();
		var zero = raw.GetProperty ("zero").GetInt32 ();

		var lines = new List<string> ();
		for (int i = 0; i < count; i++) {
			var row = storage [(zero + i) % count].GetProperty ("inner");
			var sb = new StringBuilder ();
			foreach (var cell in row.EnumerateArray ()) {
				// A wide glyph occupies two cells here as well: the second carries
				// WIDE_CHAR_SPACER and no character of its own, exactly like the zero-width
				// trailing half in our Screen.
				var flags = cell.GetProperty ("flags").GetString () ?? "";
				if (flags.Contains ("WIDE_CHAR_SPACER"))
					continue;
				sb.Append (cell.GetProperty ("c").GetString ());
			}
			lines.Add (sb.ToString ().TrimEnd ());
		}

		// Storage runs bottom-up: index zero is the newest line, so reverse into screen order.
		// Anything above that is scrollback, and the fixtures are captured with the viewport
		// at the bottom, so the last `rows` entries are what is on screen.
		lines.Reverse ();
		return lines.Skip (Math.Max (0, lines.Count - rows)).ToArray ();
	}
}
