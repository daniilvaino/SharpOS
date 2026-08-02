using TermRace.Engines;

namespace TermRace.Suites;

public enum Status { Pass, Fail, XFail, XPass, Skip, Crash, Timeout }

public sealed class CaseResult
{
	public string Suite;
	public string Case;
	public string Engine;
	public Status Status;
	public string Detail;
	public double Milliseconds;
	public long AllocatedBytes;

	/// <summary>Input bytes fed, for throughput reporting.</summary>
	public long InputBytes;

	public bool Bad => Status is Status.Fail or Status.XPass or Status.Crash or Status.Timeout;
}

public sealed class RunContext
{
	/// <summary>Root of the shitty test corpus (…/shitty/tests).</summary>
	public string TestsRoot;

	/// <summary>Case-name substring filter, null for all.</summary>
	public string Filter;

	/// <summary>Cap on cases per suite, 0 for no cap.</summary>
	public int Limit;

	/// <summary>Watchdog for a single case.</summary>
	public TimeSpan Timeout = TimeSpan.FromSeconds (10);

	public bool Verbose;

	public IEnumerable<string> Pick (IEnumerable<string> cases)
	{
		var seq = cases;
		if (!string.IsNullOrEmpty (Filter))
			seq = seq.Where (c => c.Contains (Filter, StringComparison.OrdinalIgnoreCase));
		if (Limit > 0)
			seq = seq.Take (Limit);
		return seq;
	}
}

public interface ISuite
{
	string Name { get; }

	/// <summary>Directory under tests/ this suite consumes; used for the "corpus missing" message.</summary>
	string CorpusDirectory { get; }

	/// <summary>
	/// The factory (not a single instance) is passed in because a case that trips the
	/// watchdog leaves a runaway thread mutating its engine; the suite drops that
	/// instance and asks for a fresh one.
	/// </summary>
	IEnumerable<CaseResult> Run (Func<ITerminalEngine> engineFactory, RunContext context);
}

/// <summary>
/// Runs one case under a wall-clock watchdog. A timed-out case cannot be aborted on
/// .NET Core, so its thread is abandoned and the caller must discard the engine.
/// </summary>
public static class Watchdog
{
	public static Status Run (Action body, TimeSpan timeout, out string detail, out double milliseconds)
	{
		Exception failure = null;
		var done = new ManualResetEventSlim (false);
		var started = System.Diagnostics.Stopwatch.StartNew ();
		var thread = new Thread (() => {
			try {
				body ();
			} catch (Exception e) {
				failure = e;
			} finally {
				done.Set ();
			}
		}, 16 * 1024 * 1024) { IsBackground = true };
		thread.Start ();

		if (!done.Wait (timeout)) {
			milliseconds = timeout.TotalMilliseconds;
			detail = $"no progress after {timeout.TotalSeconds:0.#}s";
			return Status.Timeout;
		}
		milliseconds = started.Elapsed.TotalMilliseconds;
		if (failure != null) {
			// The engine frames are the whole point of a crash report — without them every
			// IndexOutOfRange looks alike and the corpus cannot be triaged.
			var frames = (failure.StackTrace ?? "").Split ('\n')
				.Select (f => f.Trim ())
				.Where (f => f.Length > 0)
				.Take (4);
			detail = $"{failure.GetType ().Name}: {failure.Message}\n{string.Join ("\n", frames)}";
			return Status.Crash;
		}
		detail = null;
		return Status.Pass;
	}
}
