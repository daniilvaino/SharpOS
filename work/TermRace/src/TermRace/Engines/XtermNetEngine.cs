using System.Text;

namespace TermRace.Engines;

/// <summary>
/// Adapter over tomlm/XTerm.NET. Its Write() takes a string, so the harness owns
/// the UTF-8 decoder and keeps it across Feed() calls to survive split sequences.
/// </summary>
public sealed class XtermNetEngine : ITerminalEngine
{
	XTerm.Terminal terminal;
	Decoder decoder;
	char [] chars = new char [4096];

	public string Name => "xtermnet";

	public void Reset (int cols, int rows)
	{
		terminal = new XTerm.Terminal (new XTerm.Options.TerminalOptions { Cols = cols, Rows = rows });
		decoder = new UTF8Encoding (false, false).GetDecoder ();
	}

	public void Resize (int cols, int rows) => terminal.Resize (cols, rows);

	public void Feed (byte [] data, int offset, int count)
	{
		if (count == 0)
			return;
		int needed = count + 4;
		if (chars.Length < needed)
			chars = new char [needed];
		int produced = decoder.GetChars (data, offset, count, chars, 0, flush: false);
		if (produced > 0)
			terminal.Write (new string (chars, 0, produced));
	}

	public Screen Snapshot ()
	{
		var buffer = terminal.Buffer;
		int cols = terminal.Cols, rows = terminal.Rows;
		var cells = new Cell [rows] [];
		for (int y = 0; y < rows; y++) {
			var row = new Cell [cols];
			var line = buffer.GetLine (buffer.YDisp + y);
			for (int x = 0; x < cols; x++) {
				if (line == null || x >= line.Length)
					continue;
				var cell = line [x];
				row [x].CodePoint = cell.CodePoint;
				row [x].Width = cell.Width;
			}
			cells [y] = row;
		}
		return new Screen {
			Cols = cols,
			Rows = rows,
			CursorX = buffer.X,
			CursorY = buffer.YBase + buffer.Y - buffer.YDisp,
			Cells = cells
		};
	}
}
