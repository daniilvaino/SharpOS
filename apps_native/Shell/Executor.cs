using System.Collections.Generic;
using System.Text;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace Shell
{
    /// <summary>
    /// Turns a parsed command line into calls into this kernel.
    /// </summary>
    /// <remarks>
    /// The parser hands back clauses joined by an operator, where the operator
    /// belongs to the clause that follows it: <c>a &amp;&amp; b</c> arrives as
    /// [{None,a}, {AndIf,b}]. Skipping a clause must leave the previous exit
    /// code alone, which is what makes a chain of them behave — after a failed
    /// <c>a</c>, both <c>b</c> and <c>c</c> in <c>a &amp;&amp; b &amp;&amp; c</c>
    /// see the same failure and stand down.
    /// </remarks>
    internal sealed unsafe partial class Executor
    {
        private string _cwd;

        // `sh -c` (step197): the shell's own standard ends go to the first
        // stage's input and the last stage's output; on a failure the failed
        // stage's name goes to the pipe called _report (Pipe.From and Pipe.To
        // name it in their exception).
        private PipeReadEnd _input;
        private PipeWriteEnd _output;
        private readonly string _report;

        public Executor() : this(null, null, null) { }

        public Executor(PipeReadEnd input, PipeWriteEnd output, string report)
        {
            _input = input;
            _output = output;
            _report = report;
            // The working directory is the process's: inherited, and what the
            // stages started from here inherit. A shell the kernel started
            // begins at HOME — the kernel starts it there (step207).
            _cwd = Process.NormalizePath(Process.WorkingDirectory);
        }

        /// <summary>Whether the shell runs a line for a caller, its ends being the caller's pipe.</summary>
        private bool HasEnds => _input != null || _output != null;

        public bool ExitRequested { get; private set; }
        public int LastExitCode { get; private set; }
        public string WorkingDirectory => _cwd;

        /// <summary>A stage's `n> file`, `n>&amp;m` or `n>&amp;-`, in the order written (step212).</summary>
        private sealed class Redirection
        {
            public int Number;
            public string Target;
            public bool Append;
            // `n>&m`: n gets what m has at this point; Discard for `n>&-`; -1 for a file.
            public int CopyOf = -1;
            public string Written;
        }

        // Redirection.CopyOf of `n>&-`: n goes nowhere.
        private const int Discard = 0;

        /// <summary>A name $NAME can take: a letter or _, then letters, digits and _.</summary>
        internal static bool IsName(string name)
        {
            if (name.Length == 0 || !IsNameStart(name[0])) return false;
            for (int i = 1; i < name.Length; i++)
                if (!IsNameChar(name[i])) return false;
            return true;
        }

        private static bool IsNameStart(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
        private static bool IsNameChar(char c) => IsNameStart(c) || (c >= '0' && c <= '9');

        /// <summary>
        /// <c>a | b | c</c>: a pipe between each two stages, every stage started
        /// with its ends, all of them waited for. <c>n&gt; file</c> on any
        /// stage is a WRITE file [--append] beside it, fed by the end the
        /// stage gets under n (step212; before, <c>&gt; file</c> was one more
        /// stage at the end); the redirects go in order, as in sh (HandOut). The
        /// first stage gets the shell's own input (none outside <c>sh -c</c>),
        /// the last its output (the screen).
        /// </summary>
        /// <remarks>
        /// The code (step197) is the first stage's from the left that ended with
        /// neither 0 nor 141 — 141 is a stage whose neighbour's end broke, the
        /// echo of a failure, not one — and the last stage's when there is no
        /// such; only that stage is reported. A stage that cannot start stops
        /// the rest: the ends not handed to anyone are closed here, and the
        /// stages already started are waited for.
        /// </remarks>
        private int RunPipeline(List<Stage> stages)
        {
            var verbs = new List<string>();
            var arguments = new List<List<string>>();
            // A stage's own environment (`env A=1 prog`, step208); null: the shell's.
            var environments = new List<string[]>();
            // A stage's redirects in order (step212); null: none.
            var redirects = new List<List<Redirection>>();
            // A builtin with output may start the pipeline (`echo x > f`,
            // `ls | PIPECNT`): the shell runs it itself, its text going on as
            // a string per line. `cat FILE` there is READ FILE — the file
            // streams instead of being gathered.
            bool builtinFirst = false;
            for (int k = 0; k < stages.Count; k++)
            {
                Stage stage = stages[k];
                List<string> args = stage.Arguments;
                string verb = stage.Verb;
                if (verb.Length == 0)
                {
                    Write("sh: an empty stage in a pipeline\n");
                    return 2;
                }
                verb = ExpandAlias(verb, args);
                string[] environment = stage.Environment;
                if (verb == "env")
                {
                    int envCode = ParseEnv(args, out string program, out List<string> rest, out string[] programEnvironment);
                    if (envCode != 0) return envCode;
                    if (program != null)
                    {
                        if (IsBuiltinName(program))
                        {
                            Write("sh: env starts programs; ");
                            Write(program);
                            Write(" is a builtin\n");
                            return 2;
                        }
                        verb = ExpandAlias(program, rest);
                        args = rest;
                        environment = programEnvironment;
                    }
                }
                if (IsBuiltinName(verb))
                {
                    if (!WritesText(verb))
                    {
                        Write("sh: ");
                        Write(verb);
                        Write(" changes the shell itself and cannot be a stage of a pipeline\n");
                        return 2;
                    }
                    if (k != 0)
                    {
                        Write("sh: ");
                        Write(verb);
                        Write(" does not read its input: a builtin can only start a pipeline\n");
                        return 2;
                    }
                    if (verb == "cat" || verb == "type")
                    {
                        if (args.Count != 1)
                        {
                            Write("sh: cat in a pipeline takes one file\n");
                            return 2;
                        }
                        verb = "READ";
                    }
                    else
                    {
                        builtinFirst = true;
                    }
                }
                verbs.Add(verb);
                arguments.Add(args);
                environments.Add(environment);

                // The redirects, in order, as in sh (step212): `n> f` / `n>> f`
                // a WRITE beside the stage, `n>&m` what m has at that point,
                // `n>&-` nowhere. 1 is the output, 2 the error stream, 3 stdtext;
                // `3>&1 1>&-` sends stdtext on down the pipeline instead.
                var named = new bool[10];
                named[1] = true;
                if (stage.Redirects != null)
                    foreach (Redirection r in stage.Redirects)
                    {
                        if (builtinFirst && k == 0 && (r.Number != 1 || r.CopyOf > 0))
                        {
                            Write("sh: `");
                            Write(r.Written);
                            Write("` is for programs; ");
                            Write(verb);
                            Write(" has only its output, for `> file` or `>&-`\n");
                            return 2;
                        }
                        // 2 and 3 not redirected are the screen, as 1 is at the
                        // end of a pipeline: `1>&2` sends the output there
                        // (step218). From 4 there is nothing to copy.
                        if (r.CopyOf > 3 && !named[r.CopyOf])
                        {
                            Write("sh: `" + r.Written + "`: " + r.CopyOf.ToString()
                                  + " is not redirected before it on this stage\n");
                            return 2;
                        }
                        named[r.Number] = true;
                    }
                redirects.Add(stage.Redirects);

                // `< file`: there, or nothing starts, as in sh.
                if (stage.InputFile != null && AppHost.FileExistsEx(stage.InputFile) != AppServiceStatus.Ok)
                {
                    Write("sh: cannot read ");
                    Write(stage.InputFile);
                    Write("\n");
                    return 1;
                }
            }

            int count = verbs.Count;
            var pairs = new SharpOS.Std.Pipes.PipePair[count - 1];
            for (int k = 0; k < pairs.Length; k++) pairs[k] = SharpOS.Std.Pipes.Pipe.Create();

            var started = new List<Process>();
            var names = new List<string>();
            // The WRITEs beside the stages: waited for after them, so the code
            // reported is a stage's, not the echo of its break in a side file.
            var sideStarted = new List<Process>();
            var sidePairs = new List<SharpOS.Std.Pipes.PipePair>();
            int code = 0;
            string failedStage = null;
            bool failed = false;
            // What the last stage writes for the screen, shown by the shell (step219).
            var shown = new List<PipeReadEnd>();
            // The stages and their WRITEs are a job: the terminal is theirs
            // until the last has ended.
            Process.BeginForegroundJob();
            for (int k = builtinFirst ? 1 : 0; k < count && !failed; k++)
            {
                string path = ProgramPath(verbs[k], out int missing);
                if (path == null)
                {
                    code = missing;
                    failedStage = verbs[k];
                    failed = true;
                    break;
                }
                // What the shell made for this stage and the stage did not
                // take: closed after the start, or the pipes would stay open.
                // (A pair's own ends close below with the pair.)
                var owned = new List<PipeWriteEnd>();
                PipeReadEnd input = null;
                try
                {
                    var ends = new List<(int, PipeWriteEnd)>();
                    PipeWriteEnd output = HandOut(redirects[k], Natural(k, count, pairs, owned, shown),
                                                  sideStarted, sidePairs, owned, ends);
                    if (k == count - 1) ShowText(ends, owned, shown);
                    input = stages[k].InputFile != null ? OpenInput(stages[k].InputFile, sideStarted, sidePairs)
                          : k > 0 ? pairs[k - 1].ReadEnd : TakeInput();
                    started.Add(Process.Start(path, arguments[k].ToArray(),
                        input, output, environments[k], ends.Count > 0 ? ends.ToArray() : null));
                    names.Add(verbs[k]);
                }
                catch (System.Exception e) when (e is System.InvalidOperationException || e is SharpOS.Std.Pipes.PipeException)
                {
                    Write("sh: ");
                    Write(e.Message);
                    Write("\n");
                    code = 126;
                    failedStage = verbs[k];
                    failed = true;
                }
                finally
                {
                    foreach (PipeWriteEnd made in owned) made.Dispose();
                    if (k == 0) input?.Dispose();
                }
            }

            Process.ForegroundJobStarted();

            // The screen's pipes are read while the stages run; from before a
            // builtin first writes: the last stage waits for no one. One whose
            // stage did not start ends at once (its end was closed above).
            var showing = new List<System.Threading.Tasks.Task>();
            Inspector inspector = _capturing ? null : Inspector;
            inspector?.Begin(Describe(verbs, arguments));
            PipeReadEnd textShown = _textShown;
            _textShown = null;
            foreach (PipeReadEnd screenEnd in shown)
            {
                PipeReadEnd shownEnd = screenEnd;
                StringBuilder sink = _capturing ? _capture : null;
                // stdtext is for a person as it is — a report, a panel — not
                // data: the feed shows it, the inspector keeps the output only.
                IScreenInspector keeper = screenEnd == textShown ? null : inspector;
                showing.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    if (sink != null) ReadInto(shownEnd, sink);
                    else SharpOS.Std.Pipes.Pipe.Show(shownEnd, keeper);
                }));
            }

            // The builtin runs once the stages after it are up: its text has
            // somewhere to go, however much of it there is.
            int builtinCode = 0;
            if (builtinFirst && !failed)
            {
                var owned = new List<PipeWriteEnd>();
                try
                {
                    PipeWriteEnd output = HandOut(redirects[0], Natural(0, count, pairs, owned, null),
                                                  sideStarted, sidePairs, owned, new List<(int, PipeWriteEnd)>());
                    builtinCode = RunBuiltinStage(verbs[0], arguments[0], output);
                }
                catch (System.Exception e) when (e is System.InvalidOperationException || e is SharpOS.Std.Pipes.PipeException)
                {
                    Write("sh: ");
                    Write(e.Message);
                    Write("\n");
                    builtinCode = 126;
                }
                finally
                {
                    foreach (PipeWriteEnd made in owned) made.Dispose();
                }
            }

            // Whatever was not handed over: the stages that did start see the end.
            for (int k = 0; k < pairs.Length; k++)
            {
                pairs[k].WriteEnd.Dispose();
                pairs[k].ReadEnd.Dispose();
            }
            foreach (SharpOS.Std.Pipes.PipePair pair in sidePairs)
            {
                pair.WriteEnd.Dispose();
                pair.ReadEnd.Dispose();
            }
            // The ends stay the shell's: each command got a copy (TakeInput,
            // TakeOutput) — the input read in turn, as one stdin in sh, the
            // output one stream — and they end when the shell does.

            int offset = builtinFirst ? 1 : 0;
            var codes = new int[started.Count + offset];
            if (builtinFirst)
            {
                codes[0] = builtinCode;
                names.Insert(0, verbs[0]);
            }
            for (int k = 0; k < started.Count; k++)
            {
                started[k].WaitForExit();
                codes[k + offset] = started[k].ExitCode;
                started[k].Dispose();
            }
            foreach (System.Threading.Tasks.Task task in showing) task.Wait();
            // A WRITE of `> file` that failed (it says why itself) is the
            // pipeline's code only when no stage failed: its stage's 141 is
            // the echo of it, not the cause.
            int sideCode = 0;
            foreach (Process side in sideStarted)
            {
                side.WaitForExit();
                if (sideCode == 0 && side.ExitCode != 0 && side.ExitCode != QuietExitCode) sideCode = side.ExitCode;
                side.Dispose();
            }
            Process.EndForegroundJob();

            if (!failed)
            {
                int chosen = codes.Length - 1;
                for (int k = 0; k < codes.Length; k++)
                {
                    if (codes[k] != 0 && codes[k] != QuietExitCode)
                    {
                        chosen = k;
                        break;
                    }
                }
                if ((codes[chosen] == 0 || codes[chosen] == QuietExitCode) && sideCode != 0)
                {
                    codes = new[] { sideCode };
                    names = new List<string> { "WRITE" };
                    chosen = 0;
                }
                code = codes[chosen];
                if (code != 0)
                {
                    failedStage = names[chosen];
                    Write("sh: ");
                    Write(names[chosen]);
                    Write(" exited with ");
                    WriteInt(code);
                    Write("\n");
                }
            }
            if (code != 0) Report(failedStage);
            return code;
        }

        // A pipeline as the inspector names it: its stages as they will run.
        private static string Describe(List<string> verbs, List<List<string>> arguments)
        {
            var text = new StringBuilder();
            for (int k = 0; k < verbs.Count; k++)
            {
                if (k > 0) text.Append(" | ");
                text.Append(verbs[k]);
                foreach (string a in arguments[k])
                {
                    text.Append(' ');
                    bool plain = a.Length > 0 && a.IndexOf(' ') < 0 && a.IndexOf('\'') < 0;
                    text.Append(plain ? a : "'" + a + "'");
                }
            }
            return text.ToString();
        }

        /// <summary>The exit code of a stage whose neighbour's end broke: quiet, never the pipeline's failure.</summary>
        private const int QuietExitCode = 141;

        // The builtins that print and change nothing: they may start a pipeline.
        private static bool WritesText(string verb)
            => verb == "echo" || verb == "pwd" || verb == "ls" || verb == "dir" || verb == "cat" || verb == "type"
               || verb == "history" || verb == "help" || verb == "env" || verb == "which" || verb == "df";

        // A builtin as the first stage: its text, gathered, to the pipe as a
        // string per line. The next stage leaving early is 141, as for a program.
        // Into the pipe as it is written (step213): gathered first, `cat` of
        // a 16 MiB file was one string of 32 MiB — past what an object may take.
        private int RunBuiltinStage(string verb, List<string> args, PipeWriteEnd output)
        {
            if (output == null)
            {
                // The screen, plain as into a pipe.
                var gathered = new StringBuilder();
                StringBuilder outer = _capture;
                _capture = gathered;
                try { return RunBuiltin(verb, args); }
                finally
                {
                    _capture = outer;
                    string text = gathered.ToString();
                    Write(text);
                    // What it printed waits in the inspector, as a program's text does.
                    if (!_capturing && Inspector != null && text.Length > 0) Inspector.TakeText(text);
                }
            }
            int code;
            try
            {
                using (System.IO.TextWriter writer = output.WriteText())
                {
                    _captureWriter = writer;
                    try { code = RunBuiltin(verb, args); }
                    finally { _captureWriter = null; }
                }
            }
            catch (SharpOS.Std.Pipes.PipeException) { code = QuietExitCode; }
            return code;
        }

        private System.IO.TextWriter _captureWriter;

        // Where stage k writes when nothing is redirected: the pipe to the next
        // stage, or the shell's own output — a copy, the shell's to close if
        // the stage does not take it. With no output of its own, the screen:
        // a pipe the shell shows (step219), whose reader goes to shown — the
        // stage sees the terminal there — or, for a builtin (shown is null)
        // and on a kernel without pipe attributes, null.
        private PipeWriteEnd Natural(int k, int count, SharpOS.Std.Pipes.PipePair[] pairs, List<PipeWriteEnd> owned,
                                     List<PipeReadEnd> shown)
        {
            if (k < count - 1) return pairs[k].WriteEnd;
            if (_capturing)
            {
                if (shown == null) return null;
                SharpOS.Std.Pipes.PipePair pair = SharpOS.Std.Pipes.Pipe.Create();
                shown.Add(pair.ReadEnd);
                owned.Add(pair.WriteEnd);
                return pair.WriteEnd;
            }
            PipeWriteEnd output = TakeOutput();
            if (output == null && shown != null) output = Screen(shown);
            if (output != null) owned.Add(output);
            return output;
        }

        // stdtext of the last stage, when not redirected and the shell shows
        // the screen: shown beside its output (step219).
        private void ShowText(List<(int, PipeWriteEnd)> ends, List<PipeWriteEnd> owned, List<PipeReadEnd> shown)
        {
            if (_output != null || _capturing) return;
            foreach ((int number, PipeWriteEnd _) in ends)
                if (number == SharpOS.Std.Pipes.Pipe.TextEnd) return;
            PipeWriteEnd text = Screen(shown);
            if (text == null) return;
            _textShown = shown[shown.Count - 1];
            owned.Add(text);
            ends.Add((SharpOS.Std.Pipes.Pipe.TextEnd, text));
        }

        // The reading end of the stdtext ShowText made for the pipeline being started.
        private PipeReadEnd _textShown;

        /// <summary>The side panel the objects shown go to as trees; null: none (not at the prompt, or no panels).</summary>
        internal Inspector Inspector { get; set; }

        /// <summary>Whether the shell can read the screen's pipes beside the stages: a thread backend is installed.</summary>
        internal static bool CanShow { get; set; }

        // A pipe to the screen: its writing end; the reader joins shown. Null
        // without threads and on a kernel without pipe attributes.
        private static PipeWriteEnd Screen(List<PipeReadEnd> shown)
        {
            if (!CanShow) return null;
            SharpOS.Std.Pipes.PipePair pair = SharpOS.Std.Pipes.Pipe.CreateScreen();
            if (pair == null) return null;
            shown.Add(pair.ReadEnd);
            return pair.WriteEnd;
        }

        /// <summary>
        /// The ends a stage's redirects leave it with (step212), as sh does it:
        /// 1 starts as <paramref name="output"/>; `n> file` starts a WRITE and
        /// n gets its pipe, `n>&amp;m` gets what m has then, `n>&amp;-` an end
        /// to nowhere. The new 1 is returned (null: the screen); the numbers
        /// from 2 go to <paramref name="ends"/> — not those left on the screen,
        /// which stay where they were. An end two numbers share goes once, the
        /// later number getting a copy.
        /// </summary>
        private PipeWriteEnd HandOut(List<Redirection> redirects, PipeWriteEnd output, List<Process> sideStarted,
                                     List<SharpOS.Std.Pipes.PipePair> sidePairs, List<PipeWriteEnd> owned,
                                     List<(int, PipeWriteEnd)> ends)
        {
            var at = new PipeWriteEnd[10];
            at[1] = output;
            if (redirects != null)
                foreach (Redirection r in redirects)
                {
                    if (r.CopyOf > 0)
                    {
                        at[r.Number] = at[r.CopyOf];
                    }
                    else if (r.CopyOf == Discard)
                    {
                        at[r.Number] = SharpOS.Std.Pipes.Pipe.Nowhere();
                        owned.Add(at[r.Number]);
                    }
                    else
                    {
                        string writePath = ProgramPath("WRITE", out _)
                                           ?? throw new System.InvalidOperationException("no WRITE on PATH for `> file`");
                        SharpOS.Std.Pipes.PipePair pair = SharpOS.Std.Pipes.Pipe.Create();
                        sidePairs.Add(pair);
                        string[] writeArgs = r.Append ? new[] { r.Target, "--append" } : new[] { r.Target };
                        sideStarted.Add(Process.Start(writePath, writeArgs, pair.ReadEnd, null, null));
                        at[r.Number] = pair.WriteEnd;
                    }
                }
            for (int n = 2; n < at.Length; n++)
            {
                if (at[n] == null) continue;
                PipeWriteEnd end = at[n];
                for (int m = 1; m < n; m++)
                    if (at[m] == at[n])
                    {
                        end = at[n].Duplicate();
                        owned.Add(end);
                        break;
                    }
                ends.Add((n, end));
            }
            return at[1];
        }

        // `< file`: a READ beside the stage, as `> file` is a WRITE: objects come
        // back as objects, a file of bytes as bytes.
        private PipeReadEnd OpenInput(string path, List<Process> sideStarted, List<SharpOS.Std.Pipes.PipePair> sidePairs)
        {
            string readPath = ProgramPath("READ", out _)
                              ?? throw new System.InvalidOperationException("no READ on PATH for `< file`");
            SharpOS.Std.Pipes.PipePair pair = SharpOS.Std.Pipes.Pipe.Create();
            sidePairs.Add(pair);
            sideStarted.Add(Process.Start(readPath, new[] { path }, null, pair.WriteEnd, null));
            return pair.ReadEnd;
        }

        // The shell's own input, a copy for each command (step216): they read
        // it in turn, what one leaves the next gets. A kernel without copies
        // of a reading end gets the end itself, once, as before.
        private PipeReadEnd TakeInput()
        {
            if (_input == null) return null;
            try
            {
                return _input.Duplicate();
            }
            catch (SharpOS.Std.Pipes.PipeException)
            {
                PipeReadEnd end = _input;
                _input = null;
                return end;
            }
        }

        // The shell's own output, a copy for each command (step212): one
        // stream for all of them, ending when the shell does. A kernel
        // without copies gets the end itself, once, as before.
        private PipeWriteEnd TakeOutput()
        {
            if (_output == null) return null;
            try
            {
                return _output.Duplicate();
            }
            catch (SharpOS.Std.Pipes.PipeException)
            {
                PipeWriteEnd end = _output;
                _output = null;
                return end;
            }
        }

        // The failed stage's name to whoever asked (sh -c --report NAME).
        private void Report(string stage)
        {
            if (_report == null || stage == null) return;
            try
            {
                using var writer = SharpOS.Std.Pipes.Pipe.Write<string>(_report);
                writer.Copy(stage);
            }
            catch (SharpOS.Std.Pipes.PipeException) { }
        }

        // Two fixed names for CONVERT's one special conversion, between bytes
        // and lines: LINES is `CONVERT --from lines` — a byte stream
        // into a string per line, as it comes; BYTES is `CONVERT --to bytes` —
        // back into byte[] messages, its options (--chunk N) after it. A name,
        // not a builtin: the stage is CONVERT, a process like any other. The
        // small commands are one program, UTILS, under their own names.
        public static string[][] Aliases => new[]
        {
            new[] { "LINES", "CONVERT", "--from", "lines" },
            new[] { "BYTES", "CONVERT", "--to", "bytes" },
            new[] { "head", "UTILS", "head" },
            new[] { "tail", "UTILS", "tail" },
            new[] { "grep", "UTILS", "grep" },
            new[] { "wc", "UTILS", "wc" },
            new[] { "sort", "UTILS", "sort" },
            new[] { "uniq", "UTILS", "uniq" },
            new[] { "tee", "UTILS", "tee" },
            new[] { "find", "UTILS", "find" },
            new[] { "xxd", "UTILS", "xxd" },
            new[] { "ps", "UTILS", "ps" },
            new[] { "kill", "UTILS", "kill" },
            new[] { "sleep", "UTILS", "sleep" },
            new[] { "date", "UTILS", "date" },
        };

        private static string[] AliasOf(string verb)
        {
            foreach (string[] alias in Aliases)
                if (string.Equals(alias[0], verb, System.StringComparison.OrdinalIgnoreCase)) return alias;
            return null;
        }

        // The verb a stage runs, with an alias's own arguments put first.
        private static string ExpandAlias(string verb, List<string> args)
        {
            string[] alias = AliasOf(verb);
            if (alias == null) return verb;
            for (int i = alias.Length - 1; i >= 2; i--) args.Insert(0, alias[i]);
            return alias[1];
        }

        /// <summary>The builtins, for help and Tab completion.</summary>
        public static string[] Builtins => new[]
        {
            "buildinfo", "cat", "cd", "clear", "dir", "echo", "env", "exit", "expect", "export", "help", "history", "ls",
            "mkdir", "mv", "pwd", "rm", "rmdir", "type", "unset", "which", "cp", "df",
            "true", "false", ":", "test", "[", "break", "continue", "inspect",
        };

        /// <summary>The prompt's history (Program.Interactive sets it; a script has none).</summary>
        public List<string> History { get; set; }

        private static bool IsBuiltinName(string verb)
        {
            foreach (string builtin in Builtins)
                if (verb == builtin) return true;
            return false;
        }

        // ---- step208: environment builtins ----

        // `env [NAME=value...] [program args...]`: the NAME=value words
        // first, the program after them. Null program: env prints.
        private int ParseEnv(List<string> args, out string program, out List<string> rest, out string[] environment)
        {
            program = null;
            rest = new List<string>();
            environment = SharpOS.Std.NoRuntime.EnvironmentStore.GetAll();
            int i = 0;
            for (; i < args.Count; i++)
            {
                string arg = args[i];
                if (arg.Length > 0 && arg[0] == '-')
                {
                    Write("sh: env: options are not supported: ");
                    Write(arg);
                    Write("\n");
                    return 2;
                }
                int eq = arg.IndexOf('=');
                if (eq < 0) break;
                string name = arg.Substring(0, eq);
                if (!IsName(name))
                {
                    Write("sh: env: not a variable name: ");
                    Write(name);
                    Write("\n");
                    return 2;
                }
                environment = SharpOS.Std.NoRuntime.EnvironmentBlock.With(environment, name, arg.Substring(eq + 1));
            }
            if (i < args.Count)
            {
                program = args[i];
                for (int k = i + 1; k < args.Count; k++) rest.Add(args[k]);
            }
            return 0;
        }

        // env, env NAME=value...: the environment, a NAME=value a line.
        private int Env(List<string> args)
        {
            int code = ParseEnv(args, out string program, out _, out string[] environment);
            if (code != 0) return code;
            if (program != null)
            {
                // RunLine sends `env ... program` to RunPipeline; here only
                // when it came another way (`expect 0 env ...`).
                Write("sh: env starts a program only as a command of its own\n");
                return 2;
            }
            foreach (string entry in environment)
            {
                Write(entry);
                Write("\n");
            }
            return 0;
        }

        // export NAME=value...: set for this shell and what it starts.
        // Without arguments, the environment. An empty value removes the
        // variable — the environment is Windows-shaped (step207), where
        // "NAME=" is no variable at all.
        private int Export(List<string> args)
        {
            if (args.Count == 0) return Env(args);
            int code = 0;
            foreach (string arg in args)
            {
                int eq = arg.IndexOf('=');
                string name = eq < 0 ? arg : arg.Substring(0, eq);
                if (!IsName(name))
                {
                    Write("sh: export: not a variable name: ");
                    Write(name);
                    Write("\n");
                    code = 1;
                    continue;
                }
                string value;
                if (eq >= 0) value = arg.Substring(eq + 1);
                else if (!_variables.TryGetValue(name, out value)) continue;
                _variables.Remove(name);
                // The kernel keeps a block of 64 Ki characters at most: past
                // that the variable is refused, and the shell stays.
                try { System.Environment.SetEnvironmentVariable(name, value); }
                catch (System.InvalidOperationException e)
                {
                    Write("sh: export: ");
                    Write(name);
                    Write(": ");
                    Write(e.Message);
                    Write("\n");
                    code = 1;
                }
            }
            return code;
        }

        private int Unset(List<string> args)
        {
            int code = 0;
            foreach (string name in args)
            {
                if (!IsName(name))
                {
                    Write("sh: unset: not a variable name: ");
                    Write(name);
                    Write("\n");
                    code = name.Length > 0 && name[0] == '-' ? 2 : 1;
                    continue;
                }
                _variables.Remove(name);
                System.Environment.SetEnvironmentVariable(name, null);
            }
            return code;
        }

        // ---- step208: directories and deleting ----

        // mkdir [-p] DIR...: as in sh — without -p the parent must exist and
        // the directory must not; with -p every missing one is made.
        private int MakeDirectory(List<string> args)
        {
            bool parents = false;
            int code = 0;
            foreach (string arg in args)
            {
                if (arg == "-p") { parents = true; continue; }
                string path = Resolve(arg);
                try
                {
                    if (!parents && System.IO.Directory.Exists(path))
                    {
                        Complain("mkdir", path, "exists");
                        code = 1;
                        continue;
                    }
                    if (!parents && !System.IO.Directory.Exists(System.IO.Directory.ParentOf(path)))
                    {
                        Complain("mkdir", path, "no such parent directory");
                        code = 1;
                        continue;
                    }
                    System.IO.Directory.CreateDirectory(path);
                }
                catch (System.Exception e) when (e is System.IO.IOException || e is System.PlatformNotSupportedException)
                {
                    Complain("mkdir", path, e.Message);
                    code = 1;
                }
            }
            return code;
        }

        // rm [-r] [-f] PATH...: a file; a directory only with -r, with all in
        // it. -f: a missing path is no error.
        private int Remove(List<string> args)
        {
            bool recursive = false, force = false;
            int code = 0;
            foreach (string arg in args)
            {
                if (arg.Length > 1 && arg[0] == '-')
                {
                    foreach (char c in arg.Substring(1))
                    {
                        if (c == 'r' || c == 'R') recursive = true;
                        else if (c == 'f') force = true;
                        else { Complain("rm", arg, "unknown option"); return 2; }
                    }
                    continue;
                }
                string path = Resolve(arg);
                try
                {
                    if (System.IO.Directory.Exists(path))
                    {
                        if (!recursive) { Complain("rm", path, "is a directory (rm -r removes it)"); code = 1; continue; }
                        System.IO.Directory.Delete(path, true);
                    }
                    else if (System.IO.File.Exists(path))
                    {
                        System.IO.File.Delete(path);
                    }
                    else if (!force)
                    {
                        Complain("rm", path, "no such file or directory");
                        code = 1;
                    }
                }
                catch (System.Exception e) when (e is System.IO.IOException || e is System.UnauthorizedAccessException || e is System.PlatformNotSupportedException)
                {
                    Complain("rm", path, e.Message);
                    code = 1;
                }
            }
            return code;
        }

        // ls -l: "   12345 2026-10-14 13:05  NAME", a directory "<DIR>" in
        // place of its size; directories first, then files, each by name.
        private int ListDetails(string path)
        {
            if (!System.IO.Directory.Exists(path))
            {
                if (System.IO.File.Exists(path)) { DetailLine(new System.IO.FileInfo(path)); return 0; }
                Write("sh: cannot list ");
                Write(path);
                Write("\n");
                return 1;
            }
            string[] dirs = System.IO.Directory.GetDirectories(path);
            string[] files = System.IO.Directory.GetFiles(path);
            System.Array.Sort(dirs, string.CompareOrdinal);
            System.Array.Sort(files, string.CompareOrdinal);
            foreach (string d in dirs)
            {
                var info = new System.IO.DirectoryInfo(d);
                Write("   <DIR>  ");
                Write(Stamp(info.LastWriteTime));
                Write("  ");
                WritePainted("\u001b[1;34m", info.Name + "/");
                Write("\n");
            }
            foreach (string f in files) DetailLine(new System.IO.FileInfo(f));
            return 0;
        }

        private void DetailLine(System.IO.FileInfo info)
        {
            string size = info.Length.ToString();
            for (int pad = size.Length; pad < 8; pad++) Write(" ");
            Write(size);
            Write("  ");
            Write(Stamp(info.LastWriteTime));
            Write("  ");
            if (EndsWith(info.Name, ".EXE")) WritePainted("\u001b[32m", info.Name);
            else Write(info.Name);
            Write("\n");
        }

        private static string Stamp(System.DateTime t)
            => t.Year.ToString() + "-" + Two(t.Month) + "-" + Two(t.Day) + " " + Two(t.Hour) + ":" + Two(t.Minute);

        private static string Two(int n) => n < 10 ? "0" + n.ToString() : n.ToString();

        // cp [-r] SRC DST, cp [-r] SRC... DIR: as in sh — into DIR when it is
        // a directory that exists; a directory only with -r, with all in it.
        private int Copy(List<string> args)
        {
            bool recursive = false;
            var names = new List<string>();
            foreach (string a in args)
            {
                if (a == "-r" || a == "-R") recursive = true;
                else names.Add(a);
            }
            if (names.Count < 2) { Write("sh: usage: cp [-r] SRC DST, or cp [-r] SRC... DIR\n"); return 2; }
            string last = Resolve(names[names.Count - 1]);
            bool intoDirectory = System.IO.Directory.Exists(last);
            if (!intoDirectory && names.Count > 2) { Complain("cp", last, "not a directory"); return 1; }
            int code = 0;
            for (int i = 0; i < names.Count - 1; i++)
            {
                string from = Resolve(names[i]);
                string to = intoDirectory ? last + "/" + LastName(from) : last;
                try
                {
                    if (System.IO.Directory.Exists(from))
                    {
                        if (!recursive) { Complain("cp", from, "is a directory (cp -r copies it)"); code = 1; continue; }
                        // A copy inside its own source would copy itself again as it grows.
                        if (IsWithin(to, from))
                        {
                            Complain("cp", from, "cannot copy a directory into itself");
                            code = 1;
                            continue;
                        }
                        CopyTree(from, to);
                    }
                    else System.IO.File.Copy(from, to, true);
                }
                catch (System.Exception e) when (e is System.IO.IOException || e is System.PlatformNotSupportedException)
                {
                    Complain("cp", from, e.Message);
                    code = 1;
                }
            }
            return code;
        }

        private static void CopyTree(string from, string to)
        {
            System.IO.Directory.CreateDirectory(to);
            foreach (string f in System.IO.Directory.GetFiles(from)) System.IO.File.Copy(f, to + "/" + LastName(f), true);
            foreach (string d in System.IO.Directory.GetDirectories(from)) CopyTree(d, to + "/" + LastName(d));
        }

        // df: the volume's size, what is used and what is free.
        private int DiskFree()
        {
            var drive = new System.IO.DriveInfo("/");
            try
            {
                long total = drive.TotalSize, free = drive.TotalFreeSpace;
                Write("volume     size      used      free  use%\n");
                Write("/    ");
                Write(Size(total));
                Write(Size(total - free));
                Write(Size(free));
                string percent = (total == 0 ? 0 : (total - free) * 100 / total).ToString() + "%";
                for (int pad = percent.Length; pad < 6; pad++) Write(" ");
                Write(percent);
                Write("\n");
                return 0;
            }
            catch (System.IO.IOException e)
            {
                Write("sh: df: ");
                Write(e.Message);
                Write("\n");
                return 1;
            }
        }

        // Bytes in K, M or G, right-aligned in ten columns.
        private static string Size(long bytes)
        {
            string text = bytes >= 10L << 30 ? (bytes >> 30).ToString() + "G"
                        : bytes >= 10L << 20 ? (bytes >> 20).ToString() + "M"
                        : bytes >= 10L << 10 ? (bytes >> 10).ToString() + "K"
                        : bytes.ToString();
            var built = new StringBuilder();
            for (int pad = text.Length; pad < 10; pad++) built.Append(' ');
            return built.Append(text).ToString();
        }

        // mv SRC DST, mv SRC... DIR: as in sh — into DIR when it is a
        // directory that exists, else a rename; the target must not exist.
        private int Move(List<string> args)
        {
            if (args.Count < 2) { Write("sh: usage: mv SRC DST, or mv SRC... DIR\n"); return 2; }
            string last = Resolve(args[args.Count - 1]);
            bool intoDirectory = System.IO.Directory.Exists(last);
            if (!intoDirectory && args.Count > 2) { Complain("mv", last, "not a directory"); return 1; }
            int code = 0;
            for (int i = 0; i < args.Count - 1; i++)
            {
                string from = Resolve(args[i]);
                string to = intoDirectory ? last + "/" + LastName(from) : last;
                try
                {
                    if (System.IO.Directory.Exists(from))
                    {
                        if (IsWithin(to, from))
                        {
                            Complain("mv", from, "cannot move a directory into itself");
                            code = 1;
                            continue;
                        }
                        System.IO.Directory.Move(from, to);
                    }
                    else System.IO.File.Move(from, to);
                }
                catch (System.Exception e) when (e is System.IO.IOException || e is System.PlatformNotSupportedException)
                {
                    Complain("mv", from, e.Message);
                    code = 1;
                }
            }
            return code;
        }

        // PATH is DIRECTORY or under it; names compared as FAT does, without case.
        private static bool IsWithin(string path, string directory)
        {
            string p = path.ToUpperInvariant(), d = directory.ToUpperInvariant();
            if (!d.EndsWith("/")) d += "/";
            return p + "/" == d || p.StartsWith(d);
        }

        private static string LastName(string path)
        {
            int end = path.Length;
            while (end > 1 && path[end - 1] == '/') end--;
            int start = end;
            while (start > 0 && path[start - 1] != '/') start--;
            return path.Substring(start, end - start);
        }

        // rmdir DIR...: an empty directory.
        private int RemoveDirectory(List<string> args)
        {
            int code = 0;
            foreach (string arg in args)
            {
                string path = Resolve(arg);
                try { System.IO.Directory.Delete(path); }
                catch (System.Exception e) when (e is System.IO.IOException || e is System.PlatformNotSupportedException)
                {
                    Complain("rmdir", path, e.Message);
                    code = 1;
                }
            }
            return code;
        }

        private void Complain(string command, string path, string what)
        {
            Write("sh: ");
            Write(command);
            Write(": ");
            Write(path);
            Write(": ");
            Write(what);
            Write("\n");
        }

        // which NAME...: what each runs — a builtin, or the program's file.
        private int Which(List<string> args)
        {
            int code = 0;
            foreach (string name in args)
            {
                if (IsBuiltinName(name))
                {
                    Write(name);
                    Write(": shell builtin\n");
                    continue;
                }
                string[] alias = AliasOf(name);
                if (alias != null)
                {
                    Write(name);
                    Write(": alias for");
                    for (int i = 1; i < alias.Length; i++) { Write(" "); Write(alias[i]); }
                    Write("\n");
                    continue;
                }
                string path = LooksLikePath(name) ? Resolve(name) : Process.FindProgram(name);
                if (path != null && LooksLikePath(name) && AppHost.FileExistsEx(path) != AppServiceStatus.Ok) path = null;
                if (path == null)
                {
                    Write("sh: which: no ");
                    Write(name);
                    Write(LooksLikePath(name) ? "\n" : " along PATH\n");
                    code = 1;
                    continue;
                }
                Write(path);
                Write("\n");
            }
            return code;
        }

        // A builtin by its verb; anything else is a program.
        private int RunBuiltin(string verb, List<string> args)
        {
            if (verb == "true" || verb == ":") return 0;
            if (verb == "false") return 1;
            if (verb == "test") return Test(args);
            if (verb == "[") return Bracket(args);
            if (verb == "break") return BreakOrContinue(args, true);
            if (verb == "continue") return BreakOrContinue(args, false);
            if (verb == "expect") return Expect(args);
            if (verb == "exit") return Exit(args);
            if (verb == "echo") return Echo(args);
            if (verb == "pwd") { Write(_cwd); Write("\n"); return 0; }
            if (verb == "cd") return ChangeDirectory(args);
            if (verb == "ls" || verb == "dir") return List(args);
            if (verb == "cat" || verb == "type") return Cat(args);
            if (verb == "help") return Help();
            if (verb == "clear") { System.Console.Clear(); return 0; }
            if (verb == "history") return ShowHistory();
            if (verb == "buildinfo") return BuildInfo(args);
            if (verb == "env") return Env(args);
            if (verb == "export") return Export(args);
            if (verb == "unset") return Unset(args);
            if (verb == "which") return Which(args);
            if (verb == "mkdir") return MakeDirectory(args);
            if (verb == "rm") return Remove(args);
            if (verb == "rmdir") return RemoveDirectory(args);
            if (verb == "mv") return Move(args);
            if (verb == "cp") return Copy(args);
            if (verb == "df") return DiskFree();
            if (verb == "inspect") return Inspect(args);

            return RunProgram(verb, args);
        }

        // `inspect` shows the inspector with the keys, `inspect N` with
        // command N in it, `inspect off` hides it.
        private int Inspect(List<string> args)
        {
            if (Inspector == null)
            {
                Write("sh: inspect: no inspector here (a prompt on a screen has one)\n");
                return 1;
            }
            if (args.Count == 0) return Inspector.Show(0) ? 0 : 1;
            if (args.Count == 1 && args[0] == "off") { Inspector.Close(); return 0; }
            string number = args[0].Length > 1 && args[0][0] == '#' ? args[0].Substring(1) : args[0];
            if (args.Count == 1 && TryParseInt(number, out int n) && n > 0)
            {
                if (Inspector.Show(n)) return 0;
                Write("sh: inspect: no command #");
                WriteInt(n);
                Write(" kept (the last ");
                WriteInt(Inspector.Kept);
                Write(" are)\n");
                return 1;
            }
            Write("sh: usage: inspect [N|off]\n");
            return 2;
        }

        /// <summary>
        /// <c>expect CODE COMMAND</c> — run the command and succeed only if it
        /// exits with exactly CODE.
        /// </summary>
        /// <remarks>
        /// Needed because "non-zero means failure" is not true here.
        /// AOTTESTS.EXE returns the number of tests it passed — 61 on a good
        /// run — and a battery that read that as a failure would report the
        /// opposite of the truth. The kernel's own boot batch has carried a
        /// per-app expected code for the same reason.
        ///
        /// A prefix rather than a trailing number, because a trailing number
        /// would be an argument to the program in every other shell, and this
        /// one is trying to be a shell rather than a private format.
        /// </remarks>
        private int Expect(List<string> args)
        {
            if (args.Count < 2)
            {
                Write("sh: usage: expect CODE COMMAND\n");
                return 2;
            }

            if (!TryParseInt(args[0], out int wanted))
            {
                Write("sh: expect: not a number: ");
                Write(args[0]);
                Write("\n");
                return 2;
            }

            var rest = new List<string>();
            for (int i = 2; i < args.Count; i++) rest.Add(args[i]);

            int actual = RunProgram(args[1], rest, reportNonZero: false);
            if (actual == wanted) return 0;

            Write("sh: expected ");
            WriteInt(wanted);
            Write(" from ");
            Write(args[1]);
            Write(", got ");
            WriteInt(actual);
            Write("\n");
            return 1;
        }

        private int Exit(List<string> args)
        {
            // As in sh: `exit` alone keeps the last command's code; a word
            // that is not a number is 2.
            ExitRequested = true;
            if (args.Count == 0) return LastExitCode;
            if (TryParseInt(args[0], out int code)) return code;
            Write("sh: exit: not a number: ");
            Write(args[0]);
            Write("\n");
            return 2;
        }

        private int Echo(List<string> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) Write(" ");
                Write(args[i]);
            }
            Write("\n");
            return 0;
        }

        private int ChangeDirectory(List<string> args)
        {
            if (args.Count == 0)
            {
                string home = System.Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(home)) { Write("cd: HOME is not set\n"); return 1; }
                _cwd = Process.NormalizePath(home);
                Process.WorkingDirectory = _cwd;
                return 0;
            }

            // Resolve takes "..", "." and trailing separators (step208): the
            // root is "\" and is always there; anything else must be a
            // directory — a file was let in before.
            string resolved = Resolve(args[0]);
            if (!System.IO.Directory.Exists(resolved))
            {
                Write("sh: no such directory: ");
                Write(resolved);
                Write("\n");
                return 1;
            }

            _cwd = resolved;
            Process.WorkingDirectory = _cwd;
            return 0;
        }

        private int List(List<string> args)
        {
            // ls -l (step208): size, last write and name, a line each.
            bool details = false;
            var paths = new List<string>();
            foreach (string a in args)
            {
                if (a == "-l") details = true;
                else paths.Add(a);
            }
            if (paths.Count == 0) paths.Add(_cwd);

            // Several paths: each under its name and a blank line between, as sh.
            int code = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                string path = Resolve(paths[i]);
                if (paths.Count > 1)
                {
                    if (i > 0) Write("\n");
                    Write(path);
                    Write(":\n");
                }
                int one = details ? ListDetails(path) : ListNames(path);
                if (one != 0) code = one;
            }
            return code;
        }

        private int ListNames(string path)
        {
            byte* name = stackalloc byte[256];
            uint index = 0;
            uint shown = 0;

            while (true)
            {
                AppServiceStatus status = AppHost.TryReadDirEntry(path, index, name, 256, out FileEntry entry);
                if (status != AppServiceStatus.Ok) break;

                string entryName = Encoding.UTF8.GetString(new System.ReadOnlySpan<byte>(name, (int)(entry.NameLength < 256 ? entry.NameLength : 256)));
                // Directories blue, programs green, the rest as they are.
                if (entry.IsDirectory != 0) WritePainted("\u001b[1;34m", entryName + "/");
                else if (EndsWith(entryName, ".EXE")) WritePainted("\u001b[32m", entryName);
                else Write(entryName);
                Write("\n");

                shown++;
                index++;
            }

            if (shown == 0)
            {
                Write("sh: cannot list ");
                Write(path);
                Write("\n");
                return 1;
            }
            return 0;
        }

        // The whole file, as UTF-8 text, in pieces: a character cut by the end
        // of a piece waits for the next one.
        private int Cat(List<string> args)
        {
            if (args.Count == 0) { Write("sh: cat needs a file\n"); return 2; }

            string path = Resolve(args[0]);
            AppFile file;
            try { file = AppFile.Open(path, AppFile.ModeRead); }
            catch (System.IO.IOException)
            {
                Write("sh: cannot read ");
                Write(path);
                Write("\n");
                return 1;
            }

            using (file)
            {
                var piece = new byte[16 * 1024];
                int carried = 0;
                while (true)
                {
                    int n = file.Read(new System.Span<byte>(piece, carried, piece.Length - carried));
                    int length = carried + n;
                    if (length == 0) break;
                    int whole = n == 0 ? length : WholeCharacters(piece, length);
                    Write(Encoding.UTF8.GetString(new System.ReadOnlySpan<byte>(piece, 0, whole)));
                    carried = length - whole;
                    for (int i = 0; i < carried; i++) piece[i] = piece[whole + i];
                    if (n == 0) break;
                }
            }
            return 0;
        }

        // How many bytes from the start end on a character boundary: a lead
        // byte in the last three whose sequence runs past the end stays out.
        private static int WholeCharacters(byte[] bytes, int length)
        {
            for (int back = 1; back <= 3 && back <= length; back++)
            {
                byte b = bytes[length - back];
                if ((b & 0xC0) == 0x80) continue;                 // a continuation byte
                int needed = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
                return needed > back ? length - back : length;
            }
            return length;
        }

        // buildinfo [on|off]: whether programs print their build line at start.
        private int BuildInfo(List<string> args)
        {
            if (args.Count > 0)
            {
                if (args[0] != "on" && args[0] != "off")
                {
                    Write("sh: usage: buildinfo [on|off]\n");
                    return 2;
                }
                Process.AnnounceBuild = args[0] == "on";
            }
            Write("buildinfo ");
            Write(Process.AnnounceBuild ? "on" : "off");
            Write("\n");
            return 0;
        }

        private int ShowHistory()
        {
            if (History == null) return 0;
            for (int i = 0; i < History.Count; i++)
            {
                string number = (i + 1).ToString();
                for (int pad = number.Length; pad < 5; pad++) Write(" ");
                Write(number);
                Write("  ");
                Write(History[i]);
                Write("\n");
            }
            return 0;
        }

        private int Help()
        {
            Write("builtins: cd pwd ls cat echo clear history buildinfo expect exit help export unset env which\n");
            Write("files: mkdir [-p] DIR, rm [-r] [-f] PATH, rmdir DIR, mv SRC DST (or into DIR),\n");
            Write("       cp [-r] SRC DST (or into DIR), ls -l (size, last write), df (the volume's room)\n");
            Write("environment: export NAME=value, unset NAME, env lists; env NAME=value PROGRAM args runs it with them;\n");
            Write("             $NAME and ${NAME} in words and in \"...\", not in '...'; ~ is HOME\n");
            Write("             ENV names a script the prompt runs first\n");
            Write("buildinfo on|off — programs print their build line at start (on in an autorun battery)\n");
            Write("programs: NAME.EXE by its name, looked for along PATH (" + (System.Environment.GetEnvironmentVariable("PATH") ?? "") + ")\n");
            Write("expect CODE COMMAND — run it, succeed only on that exit code\n");
            Write("LINES — bytes into lines (CONVERT --from lines); BYTES [--chunk N] — lines into bytes (CONVERT --to bytes)\n");
            Write("over messages of any type, or lines of a file: head tail grep wc sort uniq tee; sort -k FIELD, grep -k FIELD\n");
            Write("find, xxd; ps, kill ID, sleep N, date; each takes --help\n");
            Write("a path runs too: .EXE as a program, .DLL on the hosted runtime\n");
            Write("operators: && || ; a | b | c ; n> file, n>> file, n>&m, n>&- on any program, in order:\n");
            Write("           1 the output (left out: > file), 2 errors, 3 stdtext (reports, panels);\n");
            Write("           fetch 3>&1 1>&- | less — the panel down the pipe, the data nowhere;\n");
            Write("           hints leave out what is not for the screen; env PIPEHINTS=off shows all\n");
            Write("language: if/elif/else, while, until, for, case, break, continue, { }, ( ), ! ; x=1, X=1 prog, $?, $#, $1,\n");
            Write("          ${x:-word}, $(...), $((...)), globs (* ? [a-z]), test and [, < file\n");
            Write("not here: functions, here-documents, [[ ]], arrays, background jobs (&); read is READ.EXE, not a builtin\n");
            Write("keys: Up/Down history, Tab completes, Home/End, Ctrl+A/E/U/K/W, Ctrl+L clears,\n");
            Write("      Ctrl+C drops the line, Ctrl+D on an empty line leaves\n");
            Write("inspector (the panel at the right): what the last commands showed — objects open it, text and bytes\n");
            Write("      wait in it; F12 shows and hides it, Ctrl+Tab gives it the keys, Esc gives them back;\n");
            Write("      in it: Up/Down, PgUp/PgDn, Home/End move, Right/Enter open, Left closes, t table or tree,\n");
            Write("      p/n the command before or after, q hides it; inspect [N|off] — show it (command N, the mark\n");
            Write("      after a summary line such as '▸ 1000 × T  #3'), or hide it;\n");
            Write("      settings (system.env, or export at once): INSPECTOR=auto|on|off, INSPECTOR_WIDTH=40 (percent),\n");
            Write("      INSPECTOR_KEEP=16, INSPECTOR_DEPTH=auto|N, INSPECTOR_SUMMARY=on|off\n");
            Write("paths: /bin/NAME.EXE; a backslash escapes the next character, as in bash\n");
            return 0;
        }

        // The file a verb runs: a path as given, a bare word along PATH
        // (step207), with .EXE added when it has no extension of its own.
        // Null, with the code to return, when there is none.
        private string ProgramPath(string verb, out int missing)
        {
            missing = 0;
            if (!LooksLikePath(verb))
            {
                string found = Process.FindProgram(verb);
                if (found != null) return found;
                missing = 127;
                ReportMissing(verb, "PATH");
                return null;
            }
            string path = Resolve(verb);
            if (AppHost.FileExistsEx(path) == AppServiceStatus.Ok) return path;
            missing = 127;
            ReportMissing(verb, path);
            return null;
        }

        private void ReportMissing(string verb, string path)
        {
            // A bare word that resolves to nothing is almost never a
            // mistyped path — it is a builtin this shell does not have,
            // which on a rig means the image carries an older SHELL.EXE
            // than the script was written for. Say that, rather than
            // printing a path nobody meant to type.
            if (!LooksLikePath(verb))
            {
                Write("sh: unknown command: ");
                Write(verb);
                Write(" (no builtin, and not on ");
                Write(path);
                Write(")\n");
            }
            else
            {
                Write("sh: not found: ");
                Write(path);
                Write("\n");
            }
        }

        private int RunProgram(string verb, List<string> args, bool reportNonZero = true)
        {
            verb = ExpandAlias(verb, args);
            string path = ProgramPath(verb, out int missing);
            if (path == null) return missing;

            bool managed = EndsWith(path, ".dll");

            // Arguments reach a native program through its startup data. A
            // managed one is started by the hosted runtime, which takes none
            // yet: refusing beats pretending they were delivered.
            if (managed && args.Count > 0)
            {
                Write("sh: arguments are not passed to managed programs yet, refusing to run ");
                Write(path);
                Write("\n");
                return 2;
            }

            AppServiceStatus status = managed
                ? AppHost.TryRunManagedApp(path, out int exitCode)
                : AppHost.TryRunApp(path, args.ToArray(), out exitCode);

            if (status != AppServiceStatus.Ok)
            {
                // The status number, not its name: Enum.ToString has no
                // reflection metadata to work from on this tier.
                Write("sh: could not start ");
                Write(path);
                Write(" (status=");
                Write(((uint)status).ToString());
                Write(")\n");
                return 126;
            }

            // Silenced under `expect`, which says what it wanted and got.
            if (exitCode != 0 && reportNonZero)
            {
                Write("sh: ");
                Write(path);
                Write(" exited with ");
                WriteInt(exitCode);
                Write("\n");
            }
            return exitCode;
        }

        /// <summary>A path as the shell's commands take it: from the working directory unless it starts at the root.</summary>
        internal string Resolve(string path)
        {
            // ".", ".." and the root taken here (step208), not handed to FAT.
            bool absolute = path.Length > 0 && (path[0] == '/' || path[0] == '\\');
            return Process.NormalizePath(absolute ? path : _cwd + "/" + path);
        }

        // A path has a separator, as in sh: NAME.EXE is looked for along PATH
        // (step207), ./NAME and /bin/NAME are taken as written.
        private static bool LooksLikePath(string verb)
        {
            for (int i = 0; i < verb.Length; i++)
                if (verb[i] == '\\' || verb[i] == '/') return true;
            return false;
        }

        private static bool EndsWith(string value, string suffix)
        {
            if (value.Length < suffix.Length) return false;

            int offset = value.Length - suffix.Length;
            for (int i = 0; i < suffix.Length; i++)
            {
                char a = value[offset + i];
                char b = suffix[i];
                if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                if (b >= 'A' && b <= 'Z') b = (char)(b + 32);
                if (a != b) return false;
            }
            return true;
        }

        private static bool TryParseInt(string value, out int result)
        {
            result = 0;
            if (value.Length == 0) return false;

            int sign = 1;
            int start = 0;
            if (value[0] == '-') { sign = -1; start = 1; }
            if (start >= value.Length) return false;

            int accumulated = 0;
            for (int i = start; i < value.Length; i++)
            {
                char c = value[i];
                if (c < '0' || c > '9') return false;
                accumulated = accumulated * 10 + (c - '0');
            }

            result = accumulated * sign;
            return true;
        }

        // A builtin's output: the screen, or — while the builtin starts a
        // pipeline — the text gathered for the pipe (RunBuiltinStage).
        private StringBuilder _capture;
        private bool _complaining;

        private void Write(string text)
        {
            if (_complaining)
            {
                AppHost.Terminal.Write(text);
                if (text.EndsWith("\n")) _complaining = false;
            }
            else if (_capturing && text.StartsWith("sh: "))
            {
                // The shell's own complaint inside $(...) is not its output.
                AppHost.Terminal.Write("\u001b[31msh:\u001b[0m " + text.Substring(4));
                _complaining = !text.EndsWith("\n");
            }
            else if (_captureWriter != null) _captureWriter.Write(text);
            else if (_capture != null) _capture.Append(text);
            else if (text == "sh: ") AppHost.Terminal.Write("\u001b[31msh:\u001b[0m ");   // the shell's own complaints, marked
            else
            {
                AppHost.Terminal.Write(text);
                if (_teeInspector) Inspector?.TakeText(text);
            }
        }

        // Colour for the screen only: a builtin's text going into a pipe stays plain.
        private void WritePainted(string sgr, string text)
        {
            if (_captureWriter != null) _captureWriter.Write(text);
            else if (_capture != null) _capture.Append(text);
            else
            {
                AppHost.Terminal.Write(sgr + text + "\u001b[0m");
                if (_teeInspector) Inspector?.TakeText(text);
            }
        }

        // A builtin at the prompt writing to the screen: its text waits in the inspector too.
        private bool _teeInspector;

        private void WriteInt(int value) => Write(value.ToString());
    }
}
