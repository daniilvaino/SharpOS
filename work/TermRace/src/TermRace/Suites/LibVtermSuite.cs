using System.Text;
using TermRace.Engines;

namespace TermRace.Suites;

/// <summary>
/// tests/libvterm/upstream/*.test — Paul Evans' DSL: directives push bytes into the
/// terminal, indented `?query = value` lines assert screen state.
///
/// Only the screen-level subset is executed: PUSH / RESET / RESIZE / UTF8 / WANTSTATE /
/// WANTSCREEN and the ?cursor, ?screen_row, ?screen_chars, ?screen_text, ?screen_eol
/// queries. Callback-level directives (WANTPARSER, INKEY, ENCIN, MOUSE*, DAMAGE*, …) and
/// pen/lineinfo queries have no equivalent on either engine's public surface: a block
/// holding one is reported SKIP. Directives outside that subset do not move the screen and
/// are ignored.
/// </summary>
public sealed class LibVtermSuite : ISuite
{
	public string Name => "libvterm";
	public string CorpusDirectory => "libvterm";

	const int Cols = 80;
	const int Rows = 25;

	static readonly HashSet<string> KnownDirectives = new () {
		"INIT", "RESET", "RESIZE", "PUSH", "UTF8", "WANTSTATE", "WANTSCREEN"
	};

	public IEnumerable<CaseResult> Run (Func<ITerminalEngine> engineFactory, RunContext context)
	{
		var dir = Path.Combine (context.TestsRoot, CorpusDirectory, "upstream");
		var files = Directory.EnumerateFiles (dir, "*.test")
			.OrderBy (f => f, StringComparer.Ordinal)
			.Select (f => (Path: f, Case: Path.GetFileNameWithoutExtension (f)));

		var engine = engineFactory ();
		foreach (var file in files.Where (f => context.Pick (new [] { f.Case }).Any ())) {
			var slot = new EngineSlot { Engine = engine, Factory = engineFactory };
			foreach (var result in RunFile (file.Path, file.Case, slot, context))
				yield return result;
			engine = slot.Engine;
		}
	}

	sealed class EngineSlot
	{
		public ITerminalEngine Engine;
		public Func<ITerminalEngine> Factory;
	}

	IEnumerable<CaseResult> RunFile (string path, string fileName, EngineSlot slot, RunContext context)
	{
		var engine = slot.Engine;
		var results = new List<CaseResult> ();
		var lines = File.ReadAllLines (path);
		engine.Reset (Cols, Rows);

		string block = "(prologue)";
		var pending = new List<string> ();   // assertion failures inside the current block
		bool blockSkipped = false, fileStopped = false;
		string stopReason = null;
		var timer = System.Diagnostics.Stopwatch.StartNew ();

		void Flush ()
		{
			if (block == null)
				return;
			var status = fileStopped ? Status.Skip
				: blockSkipped ? Status.Skip
				: pending.Count == 0 ? Status.Pass : Status.Fail;
			results.Add (new CaseResult {
				Suite = Name, Case = $"{fileName}:{block}", Engine = engine.Name,
				Status = status,
				Detail = status == Status.Fail ? string.Join ("\n", pending)
					: status == Status.Skip ? (stopReason ?? "unsupported query") : null,
				Milliseconds = timer.Elapsed.TotalMilliseconds
			});
			pending.Clear ();
			blockSkipped = false;
			timer.Restart ();
		}

		foreach (var raw in lines) {
			var line = raw.TrimEnd ();
			if (line.Length == 0 || line.StartsWith ("#", StringComparison.Ordinal))
				continue;

			if (line.StartsWith ("!", StringComparison.Ordinal)) {
				Flush ();
				block = line.Substring (1).Trim ();
				continue;
			}

			var trimmed = line.Trim ();
			if (trimmed.StartsWith ("?", StringComparison.Ordinal)) {
				if (fileStopped || blockSkipped)
					continue;
				var verdict = Assert (engine, trimmed);
				if (verdict.Supported) {
					if (!verdict.Ok)
						pending.Add ($"{trimmed}   -> {verdict.Actual}");
				} else {
					blockSkipped = true;
					stopReason = verdict.Actual;
				}
				continue;
			}

			if (fileStopped)
				continue;

			var directive = trimmed.Split (' ') [0];
			if (!KnownDirectives.Contains (directive)) {
				// Input-side (INKEY/ENCIN/MOUSE*/PASTE/FOCUS) and bookkeeping (DAMAGE*,
				// WANTPARSER, SETDEFAULTCOL) directives do not move the screen, so they are
				// ignored instead of ending the file; whatever they were meant to assert is
				// caught by the unsupported-query skip below.
				continue;
			}

			var argument = trimmed.Length > directive.Length ? trimmed.Substring (directive.Length).Trim () : "";
			switch (directive) {
			case "INIT":
			case "WANTSTATE":
			case "WANTSCREEN":
			case "UTF8":
				break;
			case "RESET":
				engine.Reset (Cols, Rows);
				break;
			case "RESIZE": {
				var parts = argument.Split (',');
				if (parts.Length == 2 && int.TryParse (parts [0].Trim (), out var r) && int.TryParse (parts [1].Trim (), out var c))
					engine.Resize (c, r);
				break;
			}
			case "PUSH": {
				var bytes = Dsl.Unquote (argument);
				var local = engine;
				var status = Watchdog.Run (() => local.Feed (bytes, 0, bytes.Length), context.Timeout,
					out var detail, out _);
				if (status != Status.Pass) {
					results.Add (new CaseResult {
						Suite = Name, Case = $"{fileName}:{block}", Engine = engine.Name,
						Status = status, Detail = detail
					});
					engine = slot.Factory ();
					slot.Engine = engine;
					engine.Reset (Cols, Rows);
					fileStopped = true;
					stopReason = "engine died earlier in this file";
					block = null;
				}
				break;
			}
			}
		}
		Flush ();
		slot.Engine = engine;
		return results;
	}

	struct Verdict
	{
		public bool Supported;
		public bool Ok;
		public string Actual;
	}

	static Verdict Assert (ITerminalEngine engine, string query)
	{
		var eq = query.IndexOf ('=');
		var head = (eq < 0 ? query : query.Substring (0, eq)).Trim ();
		var expected = eq < 0 ? "" : query.Substring (eq + 1).Trim ();
		var space = head.IndexOf (' ');
		var kind = space < 0 ? head : head.Substring (0, space);
		var args = space < 0 ? "" : head.Substring (space + 1).Trim ();
		var screen = engine.Snapshot ();

		switch (kind) {
		case "?cursor": {
			var actual = $"{screen.CursorY},{screen.CursorX}";
			return new Verdict { Supported = true, Ok = actual == expected.Replace (" ", ""), Actual = actual };
		}
		case "?screen_row": {
			if (!int.TryParse (args, out var row))
				return new Verdict { Supported = false, Actual = "unparsable ?screen_row" };
			return Compare (RowContent (screen, row), expected);
		}
		case "?screen_chars":
		case "?screen_text": {
			var rect = args.Split (',').Select (p => int.Parse (p.Trim ())).ToArray ();
			if (rect.Length != 4)
				return new Verdict { Supported = false, Actual = "unparsable rectangle" };
			// A rectangle spanning several rows comes back as rows joined by newlines, each
			// ending where its content ends.
			var parts = new List<string> ();
			for (int y = rect [0]; y < rect [2] && y < screen.Rows; y++)
				parts.Add (RowContent (screen, y, rect [1], Math.Min (rect [3], screen.Cols)));
			return Compare (string.Join ("\n", parts), expected);
		}
		case "?screen_eol": {
			var parts = args.Split (',').Select (p => int.Parse (p.Trim ())).ToArray ();
			if (parts.Length != 2)
				return new Verdict { Supported = false, Actual = "unparsable ?screen_eol" };
			var text = RowContent (screen, parts [0]);
			var eol = parts [1] >= text.Length ? 1 : 0;
			return new Verdict { Supported = true, Ok = eol.ToString () == expected, Actual = eol.ToString () };
		}
		default:
			return new Verdict { Supported = false, Actual = $"unsupported query {kind}" };
		}
	}

	/// <summary>
	/// Row content up to its end. Engines that track unwritten cells get the precise answer;
	/// the others can only be trimmed, which silently drops trailing printed spaces.
	/// </summary>
	static string RowContent (Screen screen, int row, int startCol = 0, int endCol = -1)
		=> screen.TracksUnwritten
			? screen.RowToEol (row, startCol, endCol)
			: screen.RowText (row, startCol, endCol).TrimEnd ();

	/// <summary>
	/// Expected values come either quoted ("ABC") or as a comma-separated list of numbers:
	/// code points for ?screen_row/?screen_chars, UTF-8 bytes for ?screen_text. Comparing
	/// both sides as text collapses that difference.
	/// </summary>
	static Verdict Compare (string actual, string expected)
	{
		string want;
		if (expected.StartsWith ("\"", StringComparison.Ordinal)) {
			want = Encoding.UTF8.GetString (Dsl.Unquote (expected));
		} else if (expected.Length == 0) {
			want = "";
		} else {
			var numbers = expected.Split (',').Select (p => p.Trim ()).Where (p => p.Length > 0).ToArray ();
			var sb = new StringBuilder ();
			var raw = new List<byte> ();
			bool utf8Bytes = numbers.All (n => Dsl.Number (n) <= 0xFF) && numbers.Any (n => Dsl.Number (n) > 0x7F);
			foreach (var n in numbers) {
				var value = Dsl.Number (n);
				if (utf8Bytes)
					raw.Add ((byte)value);
				else
					sb.Append (char.ConvertFromUtf32 (value));
			}
			want = utf8Bytes ? Encoding.UTF8.GetString (raw.ToArray ()) : sb.ToString ();
		}
		return new Verdict { Supported = true, Ok = actual == want, Actual = $"\"{Diff.Visible (actual)}\" vs \"{Diff.Visible (want)}\"" };
	}
}

public static class Dsl
{
	public static int Number (string text)
		=> text.StartsWith ("0x", StringComparison.OrdinalIgnoreCase)
			? Convert.ToInt32 (text.Substring (2), 16)
			: int.Parse (text);

	/// <summary>
	/// Decodes a libvterm DSL double-quoted byte string, including the `"A"x5` repeat suffix.
	/// </summary>
	public static byte [] Unquote (string text)
	{
		var body = Decode (text, out var end);
		// A trailing xN repeats the literal; without it a five-column write pushed one byte.
		var tail = text.Substring (Math.Min (end, text.Length)).Trim ();
		if (tail.StartsWith ("x", StringComparison.OrdinalIgnoreCase)
			&& int.TryParse (tail.Substring (1).Trim (), out var repeat) && repeat > 1) {
			var repeated = new List<byte> (body.Length * repeat);
			for (int i = 0; i < repeat; i++)
				repeated.AddRange (body);
			return repeated.ToArray ();
		}
		return body;
	}

	static byte [] Decode (string text, out int end)
	{
		var output = new List<byte> ();
		int i = 0;
		while (i < text.Length && text [i] != '"')
			i++;
		if (i < text.Length)
			i++;
		for (; i < text.Length; i++) {
			var c = text [i];
			if (c == '"') {
				i++;
				break;
			}
			if (c != '\\') {
				foreach (var b in Encoding.UTF8.GetBytes (c.ToString ()))
					output.Add (b);
				continue;
			}
			i++;
			if (i >= text.Length)
				break;
			switch (text [i]) {
			case 'e': output.Add (0x1b); break;
			case 'n': output.Add ((byte)'\n'); break;
			case 'r': output.Add ((byte)'\r'); break;
			case 't': output.Add ((byte)'\t'); break;
			case 'b': output.Add (0x08); break;
			case 'a': output.Add (0x07); break;
			case 'f': output.Add (0x0c); break;
			case 'v': output.Add (0x0b); break;
			case '0': output.Add (0); break;
			case '\\': output.Add ((byte)'\\'); break;
			case '"': output.Add ((byte)'"'); break;
			case 'x': {
				if (i + 1 < text.Length && text [i + 1] == '{') {
					var close = text.IndexOf ('}', i + 2);
					if (close < 0)
						break;
					var value = Convert.ToInt32 (text.Substring (i + 2, close - i - 2), 16);
					foreach (var b in Encoding.UTF8.GetBytes (char.ConvertFromUtf32 (value)))
						output.Add (b);
					i = close;
				} else {
					int digits = 0;
					int value = 0;
					while (digits < 2 && i + 1 < text.Length && Uri.IsHexDigit (text [i + 1])) {
						value = value * 16 + Convert.ToInt32 (text [i + 1].ToString (), 16);
						i++;
						digits++;
					}
					output.Add ((byte)value);
				}
				break;
			}
			default:
				output.Add ((byte)text [i]);
				break;
			}
		}
		end = i;
		return output.ToArray ();
	}
}
