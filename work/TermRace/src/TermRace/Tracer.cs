using System.Text;
using TermRace.Engines;

namespace TermRace;

/// <summary>
/// Feeds one file byte by byte to two engines at once and reports the first input position
/// where their cursor, scroll region or visible grid stops agreeing.
///
/// Neither engine is an oracle, so this does not say which one is right — it says which
/// control function they disagree on, which is the part that costs hours to find by eye.
/// </summary>
public static class Tracer
{
	public static int Run (TextWriter stdout, string leftName, string rightName, string path,
		int cols, int rows, int limit)
	{
		var data = File.ReadAllBytes (path);
		var left = Engines.Engines.Create (leftName);
		var right = Engines.Engines.Create (rightName);
		left.Reset (cols, rows);
		right.Reset (cols, rows);

		var reported = 0;
		var pending = new StringBuilder ();
		for (int i = 0; i < data.Length; i++) {
			left.Feed (data, i, 1);
			right.Feed (data, i, 1);
			pending.Append (Escape (data [i]));

			var a = left.Snapshot ();
			var b = right.Snapshot ();
			var difference = Compare (a, b);
			if (difference == null)
				continue;

			stdout.WriteLine ($"@{i} after \"{pending}\"");
			stdout.WriteLine ($"   {leftName,-11} {State (a)}");
			stdout.WriteLine ($"   {rightName,-11} {State (b)}");
			stdout.WriteLine ($"   {difference}");
			pending.Clear ();

			if (++reported >= limit) {
				stdout.WriteLine ($"... stopping after {limit} divergences");
				break;
			}
			// Resynchronize on the left engine's view so the next report is a fresh
			// disagreement rather than an echo of this one.
			right.Reset (cols, rows);
			left.Reset (cols, rows);
			left.Feed (data, 0, i + 1);
			right.Feed (data, 0, i + 1);
		}

		if (reported == 0)
			stdout.WriteLine ($"{leftName} and {rightName} agree over all {data.Length} bytes");
		return reported == 0 ? 0 : 1;
	}

	static string Compare (Screen a, Screen b)
	{
		if (a.CursorX != b.CursorX || a.CursorY != b.CursorY)
			return $"cursor {a.CursorX},{a.CursorY} vs {b.CursorX},{b.CursorY}";
		if (a.ScrollTop != b.ScrollTop || a.ScrollBottom != b.ScrollBottom)
			return $"region {a.ScrollTop}-{a.ScrollBottom} vs {b.ScrollTop}-{b.ScrollBottom}";
		for (int y = 0; y < Math.Min (a.Rows, b.Rows); y++) {
			var left = a.RowTextTrimmed (y);
			var right = b.RowTextTrimmed (y);
			if (left != right)
				return $"row {y}: \"{Diff.Visible (left)}\" vs \"{Diff.Visible (right)}\"";
		}
		return null;
	}

	static string State (Screen s)
		=> $"cursor {s.CursorX},{s.CursorY}  region {s.ScrollTop}-{s.ScrollBottom}";

	static string Escape (byte b)
		=> b == 0x1b ? "\\e"
		: b == (byte)'\r' ? "\\r"
		: b == (byte)'\n' ? "\\n"
		: b >= 0x20 && b < 0x7f ? ((char)b).ToString ()
		: $"\\x{b:x2}";
}
