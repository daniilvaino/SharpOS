using System.Text;

namespace TermRace;

/// <summary>
/// Minimal unified diff over lines — enough to read a failing grid dump.
/// </summary>
public static class Diff
{
	public static string Unified (string expected, string actual, string fromFile, string toFile, int maxLines = 60)
	{
		var a = expected.Split ('\n');
		var b = actual.Split ('\n');
		var sb = new StringBuilder ();
		sb.Append ("--- ").Append (fromFile).Append ('\n');
		sb.Append ("+++ ").Append (toFile).Append ('\n');
		int emitted = 0;
		for (int i = 0; i < Math.Max (a.Length, b.Length); i++) {
			var left = i < a.Length ? a [i] : null;
			var right = i < b.Length ? b [i] : null;
			if (left == right)
				continue;
			if (emitted >= maxLines) {
				sb.Append ("... diff truncated\n");
				break;
			}
			sb.Append ("@@ line ").Append (i + 1).Append (" @@\n");
			if (left != null)
				sb.Append ('-').Append (Visible (left)).Append ('\n');
			if (right != null)
				sb.Append ('+').Append (Visible (right)).Append ('\n');
			emitted += 2;
		}
		return sb.ToString ();
	}

	public static string Visible (string text)
	{
		var sb = new StringBuilder (text.Length);
		foreach (var c in text) {
			if (c < 0x20 || c == 0x7f)
				sb.Append ("<").Append (((int)c).ToString ("x2")).Append (">");
			else
				sb.Append (c);
		}
		return sb.ToString ();
	}
}
