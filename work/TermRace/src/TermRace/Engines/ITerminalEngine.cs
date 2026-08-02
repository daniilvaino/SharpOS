namespace TermRace.Engines;

/// <summary>
/// One cell of the logical grid, engine-agnostic.
/// </summary>
public struct Cell
{
	/// <summary>Unicode code point of the cell, 0 for "never written".</summary>
	public int CodePoint;

	/// <summary>1 for narrow, 2 for the leading half of a wide glyph, 0 for its trailing half.</summary>
	public int Width;
}

/// <summary>
/// Viewport snapshot: what a renderer would draw right now.
/// </summary>
public sealed class Screen
{
	public int Cols;
	public int Rows;
	public int CursorX;
	public int CursorY;
	public Cell[][] Cells;

	public string RowText (int row, int startCol = 0, int endCol = -1)
	{
		if (row < 0 || row >= Rows)
			return string.Empty;
		var cells = Cells [row];
		if (endCol < 0 || endCol > cells.Length)
			endCol = cells.Length;
		var sb = new System.Text.StringBuilder ();
		for (int x = startCol; x < endCol; x++) {
			var cp = cells [x].CodePoint;
			if (cells [x].Width == 0)
				continue;
			sb.Append (cp == 0 ? ' ' : char.ConvertFromUtf32 (Sane (cp)));
		}
		return sb.ToString ();
	}

	/// <summary>Row text with trailing blanks removed, the xterm.js fixture convention.</summary>
	public string RowTextTrimmed (int row) => RowText (row).TrimEnd ();

	static int Sane (int cp)
		=> cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF) ? 0xFFFD : cp;
}

/// <summary>
/// Adapter over one terminal emulator implementation.
/// </summary>
public interface ITerminalEngine
{
	string Name { get; }

	/// <summary>Drops all state and starts a fresh terminal of the given size.</summary>
	void Reset (int cols, int rows);

	void Resize (int cols, int rows);

	/// <summary>Feeds raw bytes exactly as a PTY would deliver them.</summary>
	void Feed (byte [] data, int offset, int count);

	Screen Snapshot ();
}

public static class Engines
{
	public static readonly string [] Names = { "xtermsharp", "xtermnet" };

	public static ITerminalEngine Create (string name) => name switch {
		"xtermsharp" => new XtermSharpEngine (),
		"xtermnet" => new XtermNetEngine (),
		_ => throw new ArgumentException ($"unknown engine '{name}'; known: {string.Join (", ", Names)}")
	};
}
