using System;
using System.IO;
using System.Text;
using XtermSharp;

namespace TermRace.Aot;

/// <summary>
/// Smallest thing that exercises the engine end to end: feed bytes, read the grid back.
/// If this runs as a native image, the parser, the buffers and the input handlers are all
/// AOT-safe.
/// </summary>
public static class Program
{
	public static int Main (string [] args)
	{
		var cols = 80;
		var rows = 25;
		byte [] input;

		if (args.Length > 0 && File.Exists (args [0]))
			input = File.ReadAllBytes (args [0]);
		else
			input = Encoding.UTF8.GetBytes (
				"[2J[H" +
				"AOT smoke\r\n" +
				"[1;31mred[0m normal\r\n" +
				"[3;10r[3Hscroll region\r\n" +
				"tab:\tstop\r\n" +
				"wide: 日本語\r\n");

		var terminal = new Terminal (null, new TerminalOptions { Cols = cols, Rows = rows, ConvertEol = false });
		terminal.Feed (input, input.Length);

		var buffer = terminal.Buffer;
		var sb = new StringBuilder ();
		for (int y = 0; y < rows; y++) {
			var index = buffer.YDisp + y;
			if (index >= buffer.Lines.Length)
				break;
			var line = buffer.Lines [index];
			var row = new StringBuilder ();
			for (int x = 0; x < cols && x < line.Length; x++) {
				var cd = line [x];
				if (cd.Width == 0)
					continue;
				row.Append (cd.Code == 0 ? ' ' : char.ConvertFromUtf32 (cd.Code));
			}
			var text = row.ToString ().TrimEnd ();
			if (text.Length != 0)
				sb.Append (y.ToString ("00")).Append ('|').Append (text).Append ('\n');
		}

		Console.Write (sb.ToString ());
		Console.WriteLine ($"cursor {buffer.X},{buffer.Y}  region {buffer.ScrollTop}-{buffer.ScrollBottom}");
		return 0;
	}
}
