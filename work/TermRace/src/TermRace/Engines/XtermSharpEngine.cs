using XtermSharp;

namespace TermRace.Engines;

/// <summary>
/// Adapter over migueldeicaza/XtermSharp. Takes raw bytes directly: the engine
/// carries its own UTF-8 decoder.
/// </summary>
public sealed class XtermSharpEngine : ITerminalEngine
{
	Terminal terminal;

	public string Name => "xtermsharp";

	public void Reset (int cols, int rows)
	{
		// ConvertEol is an engine-side ONLCR emulation. The harness already models
		// the PTY line discipline, so leave the raw stream alone here.
		terminal = new Terminal (null, new TerminalOptions { Cols = cols, Rows = rows, ConvertEol = false });
	}

	public void Resize (int cols, int rows) => terminal.Resize (cols, rows);

	public void Feed (byte [] data, int offset, int count)
	{
		if (count == 0)
			return;
		if (offset == 0 && count == data.Length) {
			terminal.Feed (data, count);
			return;
		}
		var slice = new byte [count];
		Array.Copy (data, offset, slice, 0, count);
		terminal.Feed (slice, count);
	}

	public Screen Snapshot ()
	{
		var buffer = terminal.Buffer;
		int cols = terminal.Cols, rows = terminal.Rows;
		var cells = new Cell [rows] [];
		for (int y = 0; y < rows; y++) {
			var row = new Cell [cols];
			int index = buffer.YDisp + y;
			var line = index >= 0 && index < buffer.Lines.Length ? buffer.Lines [index] : null;
			for (int x = 0; x < cols; x++) {
				if (line == null || x >= line.Length)
					continue;
				var cd = line [x];
				row [x].CodePoint = cd.Code;
				row [x].Width = cd.Width;
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
