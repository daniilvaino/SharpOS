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

	/// <summary>
	/// True when the engine distinguishes a cell never written to from one holding a printed
	/// space. XtermSharp does (blank cells carry code point 0); XTerm.NET fills blanks with
	/// Space, so the distinction is unobservable there and oracles must fall back to trimming.
	/// </summary>
	public bool TracksUnwritten;

	/// <summary>Scroll region, inclusive, in viewport rows.</summary>
	public int ScrollTop;
	public int ScrollBottom;

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

	/// <summary>
	/// Row text up to the end of what was actually written. A space that a program printed is
	/// content and stays; a cell never written to is not. libvterm's `?screen_row` draws that
	/// distinction, and blanket trimming loses it.
	/// </summary>
	public string RowToEol (int row, int startCol = 0, int endCol = -1)
	{
		if (row < 0 || row >= Rows)
			return string.Empty;
		var cells = Cells [row];
		if (endCol < 0 || endCol > cells.Length)
			endCol = cells.Length;
		int last = startCol - 1;
		for (int x = startCol; x < endCol; x++)
			if (cells [x].CodePoint != 0)
				last = x;
		return RowText (row, startCol, last + 1);
	}

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
