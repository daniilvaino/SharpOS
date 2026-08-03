using System.Text;
using TermRace.Engines;
using TermRace.Suites;

namespace TermRace;

public static class Program
{
	static readonly string [] DefaultSuites = { "xtermjs", "libvterm", "corpus" };

	public static int Main (string [] args)
	{
		Console.OutputEncoding = Encoding.UTF8;
		// Both engines log parser complaints straight to stdout. Park the real writer and
		// send everything they print to the void so the report stays parseable.
		var stdout = Console.Out;
		Console.SetOut (TextWriter.Null);

		var context = new RunContext ();
		var suiteNames = new List<string> ();
		var engineNames = new List<string> ();
		string jsonPath = null;
		string outPath = null;
		string runsRoot = null;
		string label = null;
		string reducePath = null;
		string dumpPath = null;
		string tracePath = null;
		int dumpCols = 80, dumpRows = 25;
		bool showDetail = true;

		for (int i = 0; i < args.Length; i++) {
			var arg = args [i];
			string Next (string name) => ++i < args.Length ? args [i] : throw new ArgumentException ($"{name} needs a value");
			switch (arg) {
			case "--tests": context.TestsRoot = Next (arg); break;
			case "--suite": suiteNames.AddRange (Next (arg).Split (',', StringSplitOptions.RemoveEmptyEntries)); break;
			case "--engine": engineNames.AddRange (Next (arg).Split (',', StringSplitOptions.RemoveEmptyEntries)); break;
			case "--filter": context.Filter = Next (arg); break;
			case "--limit": context.Limit = int.Parse (Next (arg)); break;
			case "--timeout": context.Timeout = TimeSpan.FromSeconds (double.Parse (Next (arg))); break;
			case "--json": jsonPath = Next (arg); break;
			case "--out": outPath = Next (arg); break;
			case "--runs": runsRoot = Next (arg); break;
			case "--label": label = Next (arg); break;
			case "--no-runs": runsRoot = ""; break;
			case "--reduce": reducePath = Next (arg); break;
			case "--dump": dumpPath = Next (arg); break;
			case "--trace": tracePath = Next (arg); break;
			case "--size": {
				var parts = Next (arg).Split ('x');
				dumpCols = int.Parse (parts [0]);
				dumpRows = int.Parse (parts [1]);
				break;
			}
			case "--quiet": showDetail = false; break;
			case "--verbose": context.Verbose = true; break;
			case "-h":
			case "--help": Usage (stdout); return 0;
			default:
				Console.Error.WriteLine ($"unknown argument '{arg}'");
				Usage (stdout);
				return 2;
			}
		}

		context.TestsRoot ??= LocateTests ();
		if (context.TestsRoot == null || !Directory.Exists (context.TestsRoot)) {
			Console.Error.WriteLine ("cannot find the shitty test corpus; pass --tests <path to shitty/tests>");
			return 2;
		}
		// Every run lands in its own timestamped directory so results accumulate instead of
		// overwriting each other — the point of a test-driven fork is comparing run N with
		// run N-1. --out/--json still work, and then write in addition to the run directory.
		string runDirectory = null;
		if (runsRoot != "") {
			var root = runsRoot ?? Path.Combine (LocateRunnerRoot (), "runs");
			var stamp = DateTime.Now.ToString ("yyyyMMdd-HHmmss");
			runDirectory = Path.Combine (root, string.IsNullOrEmpty (label) ? stamp : $"{stamp}-{Sanitize (label)}");
			Directory.CreateDirectory (runDirectory);
			outPath ??= Path.Combine (runDirectory, "report.txt");
			jsonPath ??= Path.Combine (runDirectory, "results.json");
		}

		StreamWriter transcript = null;
		if (outPath != null) {
			var directory = Path.GetDirectoryName (Path.GetFullPath (outPath));
			if (!string.IsNullOrEmpty (directory))
				Directory.CreateDirectory (directory);
			transcript = new StreamWriter (outPath, append: false, Encoding.UTF8) { AutoFlush = true };
			transcript.WriteLine ($"# termrace {string.Join (" ", args)}");
			stdout = new TeeWriter (stdout, transcript);
		}

		if (suiteNames.Count == 0)
			suiteNames.AddRange (DefaultSuites);
		if (engineNames.Count == 0)
			engineNames.AddRange (Engines.Engines.Names);

		var suites = new List<ISuite> ();
		foreach (var name in suiteNames) {
			var suite = MakeSuite (name);
			if (suite == null) {
				Console.Error.WriteLine ($"unknown suite '{name}'; known: xtermjs, libvterm, bench, crash, all, or one crash corpus: {string.Join (", ", CrashSuite.All ().Select (s => s.Name))}");
				return 2;
			}
			suites.AddRange (suite);
		}

		if (tracePath != null) {
			if (engineNames.Count < 2)
				engineNames = Engines.Engines.Names.ToList ();
			var code = Tracer.Run (stdout, engineNames [0], engineNames [1], tracePath, dumpCols, dumpRows, context.Limit > 0 ? context.Limit : 5);
			transcript?.Dispose ();
			return code;
		}

		if (dumpPath != null) {
			var code = Reducer.Dump (stdout, engineNames [0], dumpPath, dumpCols, dumpRows);
			transcript?.Dispose ();
			return code;
		}

		if (reducePath != null) {
			var reduced = runDirectory == null ? null : Path.Combine (runDirectory, "repro.bin");
			var code = Reducer.Run (stdout, engineNames [0], reducePath, context.Timeout, reduced);
			transcript?.Dispose ();
			return code;
		}

		var results = new List<CaseResult> ();
		foreach (var suite in suites) {
			var corpus = Path.Combine (context.TestsRoot, suite.CorpusDirectory);
			if (!Directory.Exists (corpus)) {
				Console.Error.WriteLine ($"skipping suite {suite.Name}: {corpus} not found");
				continue;
			}
			foreach (var engineName in engineNames) {
				var started = results.Count;
				foreach (var result in suite.Run (() => Engines.Engines.Create (engineName), context)) {
					results.Add (result);
					if (context.Verbose || result.Bad)
						stdout.WriteLine (Format (result, showDetail));
				}
				stdout.WriteLine (Summary (suite.Name, engineName, results.Skip (started)));
			}
		}

		stdout.WriteLine ();
		Report.Table (stdout, results, engineNames);
		if (jsonPath != null) {
			Report.WriteJson (jsonPath, results);
			stdout.WriteLine ($"json written to {jsonPath}");
		}
		if (transcript != null) {
			transcript.Dispose ();
			Console.Error.WriteLine ($"report written to {outPath}");
		}
		if (runDirectory != null)
			Console.Error.WriteLine ($"run directory: {runDirectory}");

		return results.Any (r => r.Bad) ? 1 : 0;
	}

	static IEnumerable<ISuite> MakeSuite (string name) => name switch {
		"xtermjs" => new ISuite [] { new XtermJsSuite () },
		"libvterm" => new ISuite [] { new LibVtermSuite () },
		"alacritty" => new ISuite [] { new AlacrittySuite () },
		"bench" => new ISuite [] { new BenchSuite () },
		_ when CrashSuite.All ().Any (s => s.Name == name) => CrashSuite.All ().Where (s => s.Name == name),
		"crash" => CrashSuite.All (),
		"all" => new ISuite [] { new XtermJsSuite (), new LibVtermSuite (), new AlacrittySuite () }.Concat (CrashSuite.All ()).Append (new BenchSuite ()),
		_ => null
	};

	/// <summary>Walks up from the binary looking for the runner's own root (the one with run.ps1).</summary>
	static string LocateRunnerRoot ()
	{
		var dir = AppContext.BaseDirectory;
		for (int i = 0; i < 10 && dir != null; i++) {
			if (File.Exists (Path.Combine (dir, "run.ps1")))
				return dir;
			dir = Path.GetDirectoryName (dir.TrimEnd (Path.DirectorySeparatorChar));
		}
		return Directory.GetCurrentDirectory ();
	}

	static string Sanitize (string text)
		=> string.Concat (text.Select (c => char.IsLetterOrDigit (c) || c is '-' or '_' ? c : '-'));

	/// <summary>Walks up from the binary looking for work/shitty/tests.</summary>
	static string LocateTests ()
	{
		var dir = AppContext.BaseDirectory;
		for (int i = 0; i < 10 && dir != null; i++) {
			var candidate = Path.Combine (dir, "shitty", "tests");
			if (Directory.Exists (candidate))
				return candidate;
			dir = Path.GetDirectoryName (dir.TrimEnd (Path.DirectorySeparatorChar));
		}
		return null;
	}

	static string Format (CaseResult r, bool showDetail)
	{
		var head = $"{r.Status.ToString ().ToUpperInvariant (),-7} {r.Engine,-11} {r.Suite}/{r.Case}";
		if (!showDetail || string.IsNullOrEmpty (r.Detail))
			return head;
		return head + "\n" + Indent (r.Detail);
	}

	static string Indent (string text)
		=> string.Join ("\n", text.TrimEnd ('\n').Split ('\n').Select (l => "    " + l));

	static string Summary (string suite, string engine, IEnumerable<CaseResult> results)
	{
		var list = results.ToList ();
		var counts = Enum.GetValues<Status> ()
			.Select (s => (Status: s, Count: list.Count (r => r.Status == s)))
			.Where (p => p.Count > 0)
			.Select (p => $"{p.Status.ToString ().ToLowerInvariant ()}={p.Count}");
		return $"[{suite}/{engine}] {list.Count} cases: {string.Join (" ", counts)}";
	}

	static void Usage (TextWriter stdout)
	{
		stdout.WriteLine (@"termrace — runs the shitty terminal test corpus against C# terminal engines.

usage: termrace [options]

  --tests PATH     root of the shitty corpus (default: auto-detected work/shitty/tests)
  --suite LIST     xtermjs, libvterm, alacritty, bench, all, crash (every byte corpus), or
                   corpus, tmux, mosh, moshparser, ghostty-parser, ghostty-stream,
                   ghostty-osc, fuzz
                   (default: xtermjs,libvterm,corpus)
  --engine LIST    xtermsharp, xtermnet (default: both)
  --filter TEXT    only cases whose name contains TEXT
  --limit N        at most N cases per suite
  --timeout SECS   per-case watchdog, default 10
  --json PATH      write machine-readable results
  --out PATH       also write everything printed here to a text report
  --dump FILE      feed one file and print the resulting grid, then exit (--size COLSxROWS)
  --trace FILE     feed one file to two engines byte by byte and report where they first
                   disagree on cursor, scroll region or grid (--limit N divergences)
  --reduce FILE    shrink one corpus file to a minimal input that still fails, then exit
                   (uses the first --engine; writes repro.bin into the run directory)
  --label TEXT     tag for this run's directory name, e.g. the patch it tests
  --runs PATH      root for timestamped run directories (default: <runner>/runs)
  --no-runs        do not create a run directory; only --out/--json are written
  --quiet          summary lines only, no diffs
  --verbose        print passing cases too");
	}
}
