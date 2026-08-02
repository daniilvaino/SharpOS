using TermRace.Engines;

namespace TermRace.Suites;

/// <summary>
/// Byte-corpus suites whose oracle is "did not throw, did not hang, screen still readable".
/// Covers tests/corpus (shitty's own crash regressions), tests/tmux, tests/mosh,
/// tests/fuzz_corpus and tests/ghostty.
/// </summary>
public sealed class CrashSuite : ISuite
{
	readonly string directory;
	readonly string [] globs;

	public CrashSuite (string name, string directory, params string [] globs)
	{
		Name = name;
		this.directory = directory;
		this.globs = globs.Length == 0 ? new [] { "*" } : globs;
	}

	public string Name { get; }
	public string CorpusDirectory => directory;

	public static IEnumerable<ISuite> All () => new ISuite [] {
		new CrashSuite ("corpus", "corpus"),
		new CrashSuite ("tmux", Path.Combine ("tmux", "corpus")),
		new CrashSuite ("mosh", Path.Combine ("mosh", "terminal_corpus")),
		new CrashSuite ("moshparser", Path.Combine ("mosh", "terminal_parser_corpus")),
		new CrashSuite ("ghostty-parser", Path.Combine ("ghostty", "parser-cmin")),
		new CrashSuite ("ghostty-stream", Path.Combine ("ghostty", "stream-cmin")),
		new CrashSuite ("ghostty-osc", Path.Combine ("ghostty", "osc-cmin")),
		new CrashSuite ("fuzz", "fuzz_corpus"),
	};

	public IEnumerable<CaseResult> Run (Func<ITerminalEngine> engineFactory, RunContext context)
	{
		var dir = Path.Combine (context.TestsRoot, directory);
		var files = globs.SelectMany (g => Directory.EnumerateFiles (dir, g))
			.Where (f => !IsMetadata (f))
			.OrderBy (f => f, StringComparer.Ordinal)
			.ToList ();

		var engine = engineFactory ();
		foreach (var path in context.Pick (files.Select (Path.GetFileName))) {
			var data = File.ReadAllBytes (Path.Combine (dir, path));
			var local = engine;
			var status = Watchdog.Run (() => {
				local.Reset (80, 25);
				// Chunked, because split escape sequences are where parsers fall over.
				const int chunk = 1024;
				for (int offset = 0; offset < data.Length; offset += chunk)
					local.Feed (data, offset, Math.Min (chunk, data.Length - offset));
				// Reading the grid back catches corruption that feeding alone hides.
				var screen = local.Snapshot ();
				for (int y = 0; y < screen.Rows; y++)
					_ = screen.RowText (y);
			}, context.Timeout, out var detail, out var ms);

			if (status != Status.Pass)
				engine = engineFactory ();

			yield return new CaseResult {
				Suite = Name, Case = path, Engine = local.Name,
				Status = status, Detail = detail, Milliseconds = ms
			};
		}
	}

	static bool IsMetadata (string path)
	{
		var name = Path.GetFileName (path);
		return name.StartsWith (".", StringComparison.Ordinal)
			|| name.EndsWith (".py", StringComparison.Ordinal)
			|| name.EndsWith (".md", StringComparison.Ordinal)
			|| name.EndsWith (".txt", StringComparison.Ordinal)
			|| name.EndsWith (".json", StringComparison.Ordinal)
			|| name.StartsWith ("LICENSE", StringComparison.Ordinal)
			|| name.StartsWith ("COPYING", StringComparison.Ordinal);
	}
}
