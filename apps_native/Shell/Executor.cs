using System.Collections.Generic;
using System.Text;
using ShellSyntaxTree;
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
    internal sealed unsafe class Executor
    {
        private const string AppDirectory = "\\apps";

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
            // ("\\") begins in the programs' directory, as it always has.
            _cwd = Process.WorkingDirectory;
            if (_cwd == "\\" && input == null && output == null)
            {
                _cwd = AppDirectory;
                Process.WorkingDirectory = _cwd;
            }
        }

        /// <summary>Whether the shell runs a line for a caller, its ends being the caller's pipe.</summary>
        private bool HasEnds => _input != null || _output != null;

        public bool ExitRequested { get; private set; }
        public int LastExitCode { get; private set; }
        public string WorkingDirectory => _cwd;

        public int RunLine(string line)
        {
            // The parser resolves a redirect's target against its working
            // directory (step197): it gets the shell's, or `> out.txt` lands
            // in the root.
            var parser = new BashParser(new BashParserOptions { WorkingDirectory = _cwd.Replace('\\', '/') });
            ParsedCommand parsed = parser.Parse(line);
            if (parsed.IsUnparseable)
            {
                Write("sh: cannot parse: ");
                Write(parsed.UnparseableReason ?? line);
                Write("\n");
                return LastExitCode = 2;
            }

            int last = 0;
            var clauses = parsed.Clauses;
            for (int i = 0; i < clauses.Count; i++)
            {
                Clause clause = clauses[i];

                // The stages of a pipeline are the clauses after this one that
                // the parser joined to it with `|`.
                int end = i + 1;
                while (end < clauses.Count && clauses[end].Operator == CompoundOperator.Pipe) end++;

                bool skip = (clause.Operator == CompoundOperator.AndIf && last != 0)
                         || (clause.Operator == CompoundOperator.OrIf && last == 0);
                if (!skip)
                    last = end - i > 1 || clause.Redirects.Count > 0 || (HasEnds && (!IsBuiltin(clause) || WritesText(Verb(clause))))
                        ? RunPipeline(clauses, i, end)
                        : RunClause(clause);
                i = end - 1;
                if (ExitRequested) break;
            }

            return LastExitCode = last;
        }

        /// <summary>
        /// <c>a | b | c</c>: a pipe between each two stages, every stage started
        /// with its ends, all of them waited for. <c>&gt; file</c> and
        /// <c>&gt;&gt; file</c> on the last stage are one more stage, WRITE
        /// file [--append] (step197). The first stage gets the shell's own input
        /// (none outside <c>sh -c</c>), the last its output (the screen).
        /// </summary>
        /// <remarks>
        /// The code (step197) is the first stage's from the left that ended with
        /// neither 0 nor 141 — 141 is a stage whose neighbour's end broke, the
        /// echo of a failure, not one — and the last stage's when there is no
        /// such; only that stage is reported. A stage that cannot start stops
        /// the rest: the ends not handed to anyone are closed here, and the
        /// stages already started are waited for.
        /// </remarks>
        private int RunPipeline(IReadOnlyList<Clause> clauses, int first, int end)
        {
            var verbs = new List<string>();
            var arguments = new List<List<string>>();
            // A builtin with output may start the pipeline (`echo x > f`,
            // `ls | PIPECNT`): the shell runs it itself, its text going on as
            // a string per line. `cat FILE` there is READ FILE — the file
            // streams instead of being gathered.
            bool builtinFirst = false;
            for (int k = first; k < end; k++)
            {
                Clause stage = clauses[k];
                var args = new List<string>();
                string verb = Split(stage, args);
                if (verb.Length == 0)
                {
                    Write("sh: an empty stage in a pipeline\n");
                    return 2;
                }
                if (IsBuiltin(stage))
                {
                    if (!WritesText(verb))
                    {
                        Write("sh: ");
                        Write(verb);
                        Write(" changes the shell itself and cannot be a stage of a pipeline\n");
                        return 2;
                    }
                    if (k != first)
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

                foreach (Redirect redirect in stage.Redirects)
                {
                    bool append = redirect.Direction == RedirectDirection.Append;
                    if (k != end - 1 || (redirect.Direction != RedirectDirection.Out && !append) || stage.Redirects.Count > 1)
                    {
                        Write("sh: only `> file` and `>> file` at the end of a pipeline are supported\n");
                        return 2;
                    }
                    if (_output != null)
                    {
                        Write("sh: the output is already a pipe; `>` cannot redirect it\n");
                        return 2;
                    }
                    var write = new List<string> { redirect.Target };
                    if (append) write.Add("--append");
                    verbs.Add("WRITE");
                    arguments.Add(write);
                }
            }

            int count = verbs.Count;
            var pairs = new SharpOS.Std.Pipes.PipePair[count - 1];
            for (int k = 0; k < pairs.Length; k++) pairs[k] = SharpOS.Std.Pipes.Pipe.Create();

            var started = new List<Process>();
            var names = new List<string>();
            int code = 0;
            string failedStage = null;
            bool failed = false;
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
                try
                {
                    started.Add(Process.Start(path, arguments[k].ToArray(),
                        k > 0 ? pairs[k - 1].ReadEnd : TakeInput(),
                        k < count - 1 ? pairs[k].WriteEnd : TakeOutput()));
                    names.Add(verbs[k]);
                }
                catch (System.InvalidOperationException e)
                {
                    Write("sh: ");
                    Write(e.Message);
                    Write("\n");
                    code = 126;
                    failedStage = verbs[k];
                    failed = true;
                }
            }

            // The builtin runs once the stages after it are up: its text has
            // somewhere to go, however much of it there is.
            int builtinCode = 0;
            if (builtinFirst && !failed)
                builtinCode = RunBuiltinStage(verbs[0], arguments[0], count > 1 ? pairs[0].WriteEnd : TakeOutput());

            // Whatever was not handed over: the stages that did start see the end.
            for (int k = 0; k < pairs.Length; k++)
            {
                pairs[k].WriteEnd.Dispose();
                pairs[k].ReadEnd.Dispose();
            }
            _input?.Dispose();
            _output?.Dispose();
            _input = null;
            _output = null;

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

        /// <summary>The exit code of a stage whose neighbour's end broke: quiet, never the pipeline's failure.</summary>
        private const int QuietExitCode = 141;

        // The builtins that print and change nothing: they may start a pipeline.
        private static bool WritesText(string verb)
            => verb == "echo" || verb == "pwd" || verb == "ls" || verb == "dir" || verb == "cat" || verb == "type"
               || verb == "history" || verb == "help";

        // A builtin as the first stage: its text, gathered, to the pipe as a
        // string per line. The next stage leaving early is 141, as for a program.
        private int RunBuiltinStage(string verb, List<string> args, PipeWriteEnd output)
        {
            var gathered = new StringBuilder();
            _capture = gathered;
            int code;
            try { code = RunBuiltin(verb, args); }
            finally { _capture = null; }

            if (output == null) { Write(gathered.ToString()); return code; }
            try
            {
                using (System.IO.TextWriter writer = output.WriteText())
                    writer.Write(gathered.ToString());
            }
            catch (SharpOS.Std.Pipes.PipeException) { if (code == 0) code = QuietExitCode; }
            return code;
        }

        // The shell's own ends, each handed to one stage once.
        private PipeReadEnd TakeInput()
        {
            PipeReadEnd end = _input;
            _input = null;
            return end;
        }

        private PipeWriteEnd TakeOutput()
        {
            PipeWriteEnd end = _output;
            _output = null;
            return end;
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

        /// <summary>The builtins, for help and Tab completion.</summary>
        public static string[] Builtins => new[]
        {
            "buildinfo", "cat", "cd", "clear", "dir", "echo", "exit", "expect", "help", "history", "ls", "pwd", "type",
        };

        /// <summary>The prompt's history (Program.Interactive sets it; a script has none).</summary>
        public List<string> History { get; set; }

        private static string Verb(Clause clause) => Split(clause, new List<string>());

        private static bool IsBuiltin(Clause clause)
        {
            var args = new List<string>();
            string verb = Split(clause, args);
            foreach (string builtin in Builtins)
                if (verb == builtin) return true;
            return false;
        }

        // The verb and the arguments of a clause.
        private static string Split(Clause clause, List<string> args)
        {
            string verb = "";
            foreach (ClauseElement element in clause.Elements)
            {
                // The parser marks a run of bare words as a verb chain
                // ("git commit"): the program is the first, the rest are its
                // arguments, as in argv.
                if (element.Role == ClauseElementRole.Verb && verb.Length == 0)
                {
                    verb = element.Value;
                }
                else if (element.Role != ClauseElementRole.Redirect)
                {
                    args.Add(element.Value);
                }
            }
            return verb;
        }

        private int RunClause(Clause clause)
        {

            // Elements carry the decoded value; Raw still has its quotes.
            var args = new List<string>();
            string verb = Split(clause, args);

            if (verb.Length == 0) return 0;
            return RunBuiltin(verb, args);
        }

        // A builtin by its verb; anything else is a program.
        private int RunBuiltin(string verb, List<string> args)
        {
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

            return RunProgram(verb, args);
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
            ExitRequested = true;
            if (args.Count == 0) return 0;
            return TryParseInt(args[0], out int code) ? code : 0;
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
            if (args.Count == 0) { _cwd = AppDirectory; Process.WorkingDirectory = _cwd; return 0; }

            string target = args[0];
            if (target == "..")
            {
                int cut = _cwd.LastIndexOf('\\');
                _cwd = cut <= 0 ? "\\" : _cwd.Substring(0, cut);
                Process.WorkingDirectory = _cwd;
                return 0;
            }

            string resolved = Resolve(target);
            if (AppHost.FileExistsEx(resolved) != AppServiceStatus.Ok)
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
            string path = args.Count == 0 ? _cwd : Resolve(args[0]);

            byte* name = stackalloc byte[256];
            uint index = 0;
            uint shown = 0;

            while (true)
            {
                AppServiceStatus status = AppHost.TryReadDirEntry(path, index, name, 256, out FileEntry entry);
                if (status != AppServiceStatus.Ok) break;

                string entryName = Encoding.UTF8.GetString(new System.ReadOnlySpan<byte>(name, (int)(entry.NameLength < 256 ? entry.NameLength : 256)));
                // Directories blue, programs green, the rest as they are.
                if (entry.IsDirectory != 0) WritePainted("\u001b[1;34m", entryName + "\\");
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
            Write("builtins: cd pwd ls cat echo clear history buildinfo expect exit help\n");
            Write("buildinfo on|off — programs print their build line at start (on in an autorun battery)\n");
            Write("programs: any NAME.EXE in /apps by its name — READ, WRITE, CONVERT, PIPEGEN, ...\n");
            Write("expect CODE COMMAND — run it, succeed only on that exit code\n");
            Write("a path runs too: .EXE as a program, .DLL on the hosted runtime\n");
            Write("operators: && || ; a | b | c ; > file and >> file at the end of a pipeline\n");
            Write("keys: Up/Down history, Tab completes, Home/End, Ctrl+A/E/U/K/W, Ctrl+L clears,\n");
            Write("      Ctrl+C drops the line, Ctrl+D on an empty line leaves\n");
            // Worth saying out loud: this is bash syntax, so a backslash
            // escapes the next character and eats itself. /apps/X.EXE works,
            // \apps\X.EXE arrives as appsX.EXE.
            Write("paths: use /apps/NAME.EXE — a backslash is an escape here, not a separator\n");
            return 0;
        }

        // The file a verb runs: a path as given, a bare word from pps, with
        // .EXE added when it has no extension of its own. Null, with the code
        // to return, when there is none.
        private string ProgramPath(string verb, out int missing)
        {
            missing = 0;
            string path = LooksLikePath(verb) ? Resolve(verb) : AppDirectory + "\\" + verb;
            if (AppHost.FileExistsEx(path) == AppServiceStatus.Ok) return path;
            if (!LooksLikePath(verb) && AppHost.FileExistsEx(path + ".EXE") == AppServiceStatus.Ok) return path + ".EXE";
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
                Write(" (no builtin, and no ");
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
            var built = new StringBuilder();

            bool absolute = path.Length > 0 && (path[0] == '\\' || path[0] == '/');
            if (!absolute)
            {
                built.Append(_cwd);
                if (_cwd.Length > 0 && _cwd[_cwd.Length - 1] != '\\') built.Append('\\');
            }

            for (int i = 0; i < path.Length; i++)
                built.Append(path[i] == '/' ? '\\' : path[i]);

            return built.ToString();
        }

        private static bool LooksLikePath(string verb)
        {
            for (int i = 0; i < verb.Length; i++)
                if (verb[i] == '\\' || verb[i] == '/' || verb[i] == '.') return true;
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

        private void Write(string text)
        {
            if (_capture != null) _capture.Append(text);
            else if (text == "sh: ") AppHost.WriteString("\u001b[31msh:\u001b[0m ");   // the shell's own complaints, marked
            else AppHost.WriteString(text);
        }

        // Colour for the screen only: a builtin's text going into a pipe stays plain.
        private void WritePainted(string sgr, string text)
        {
            if (_capture != null) _capture.Append(text);
            else AppHost.WriteString(sgr + text + "\u001b[0m");
        }

        private void WriteInt(int value) => Write(value.ToString());
    }
}
