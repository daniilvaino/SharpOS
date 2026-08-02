using System.Text;

namespace TermRace;

/// <summary>
/// Writes to the console and to the transcript file at once, so `--out` captures exactly
/// what a run printed — summaries, diffs and the scoreboard.
/// </summary>
public sealed class TeeWriter : TextWriter
{
	readonly TextWriter first;
	readonly TextWriter second;

	public TeeWriter (TextWriter first, TextWriter second)
	{
		this.first = first;
		this.second = second;
	}

	public override Encoding Encoding => first.Encoding;

	public override void Write (char value)
	{
		first.Write (value);
		second.Write (value);
	}

	public override void Write (string value)
	{
		first.Write (value);
		second.Write (value);
	}

	public override void WriteLine (string value)
	{
		first.WriteLine (value);
		second.WriteLine (value);
	}

	public override void Flush ()
	{
		first.Flush ();
		second.Flush ();
	}
}
