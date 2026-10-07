using System.Collections.Generic;
using System.Text;
using ShellSyntaxTree;
using SharpOS.AppSdk;

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
        private const int MaxFileBytes = 8192;
        private const string AppDirectory = "\\apps";

        private readonly BashParser _parser = new BashParser();
        private string _cwd = AppDirectory;

        public bool ExitRequested { get; private set; }
        public int LastExitCode { get; private set; }
        public string WorkingDirectory => _cwd;

        public int RunLine(string line)
        {
            ParsedCommand parsed = _parser.Parse(line);
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
                    last = end - i > 1 ? RunPipeline(clauses, i, end) : RunClause(clause);
                i = end - 1;
                if (ExitRequested) break;
            }

            return LastExitCode = last;
        }

        /// <summary>
        /// <c>a | b | c</c>: a pipe between each two stages, every stage started
        /// with its ends, all of them waited for; the code is the last stage's.
        /// The first stage gets no input and the last no output.
        /// </summary>
        /// <remarks>
        /// A stage that cannot start stops the rest: the ends not handed to
        /// anyone are closed here — a started stage then sees its input end, or
        /// its output broken — and the stages already started are waited for.
        /// </remarks>
        private int RunPipeline(IReadOnlyList<Clause> clauses, int first, int end)
        {
            int count = end - first;
            var verbs = new string[count];
            var arguments = new List<string>[count];
            for (int k = 0; k < count; k++)
            {
                Clause stage = clauses[first + k];
                if (stage.Redirects.Count > 0)
                {
                    Write("sh: redirection is not supported yet\n");
                    return 2;
                }
                arguments[k] = new List<string>();
                verbs[k] = Split(stage, arguments[k]);
                if (verbs[k].Length == 0)
                {
                    Write("sh: an empty stage in a pipeline\n");
                    return 2;
                }
            }

            var pairs = new SharpOS.Std.Pipes.PipePair[count - 1];
            for (int k = 0; k < pairs.Length; k++) pairs[k] = SharpOS.Std.Pipes.Pipe.Create();

            var started = new List<Process>();
            int code = 0;
            bool failed = false;
            for (int k = 0; k < count && !failed; k++)
            {
                string path = ProgramPath(verbs[k], out int missing);
                if (path == null)
                {
                    code = missing;
                    failed = true;
                    break;
                }
                try
                {
                    started.Add(Process.Start(path, arguments[k].ToArray(),
                        k > 0 ? pairs[k - 1].ReadEnd : null,
                        k < count - 1 ? pairs[k].WriteEnd : null));
                }
                catch (System.InvalidOperationException e)
                {
                    Write("sh: ");
                    Write(e.Message);
                    Write("\n");
                    code = 126;
                    failed = true;
                }
            }

            // Whatever was not handed over: the stages that did start see the end.
            for (int k = 0; k < pairs.Length; k++)
            {
                pairs[k].WriteEnd.Dispose();
                pairs[k].ReadEnd.Dispose();
            }

            for (int k = 0; k < started.Count; k++)
            {
                started[k].WaitForExit();
                if (!failed && k == count - 1) code = started[k].ExitCode;
                started[k].Dispose();
            }
            return code;
        }

        // The verb and the arguments of a clause.
        private static string Split(Clause clause, List<string> args)
        {
            string verb = "";
            foreach (ClauseElement element in clause.Elements)
            {
                if (element.Role == ClauseElementRole.Verb)
                {
                    if (verb.Length == 0) verb = element.Value;
                }
                else if (element.Role == ClauseElementRole.Argument)
                {
                    args.Add(element.Value);
                }
            }
            return verb;
        }

        private int RunClause(Clause clause)
        {
            if (clause.Redirects.Count > 0)
            {
                Write("sh: redirection is not supported yet\n");
                return 2;
            }

            // Elements carry the decoded value; Raw still has its quotes.
            var args = new List<string>();
            string verb = Split(clause, args);

            if (verb.Length == 0) return 0;

            if (verb == "expect") return Expect(args);
            if (verb == "exit") return Exit(args);
            if (verb == "echo") return Echo(args);
            if (verb == "pwd") { Write(_cwd); Write("\n"); return 0; }
            if (verb == "cd") return ChangeDirectory(args);
            if (verb == "ls" || verb == "dir") return List(args);
            if (verb == "cat" || verb == "type") return Cat(args);
            if (verb == "help") return Help();

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
            if (args.Count == 0) { _cwd = AppDirectory; return 0; }

            string target = args[0];
            if (target == "..")
            {
                int cut = _cwd.LastIndexOf('\\');
                _cwd = cut <= 0 ? "\\" : _cwd.Substring(0, cut);
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

                for (uint i = 0; i < entry.NameLength && i < 256; i++)
                    AppHost.WriteChar((char)name[i]);

                if (entry.IsDirectory != 0) Write("\\");
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

        private int Cat(List<string> args)
        {
            if (args.Count == 0) { Write("sh: cat needs a file\n"); return 2; }

            string path = Resolve(args[0]);
            byte* buffer = stackalloc byte[MaxFileBytes];
            if (AppHost.TryReadFile(path, buffer, MaxFileBytes, out uint length) != AppServiceStatus.Ok)
            {
                Write("sh: cannot read ");
                Write(path);
                Write("\n");
                return 1;
            }

            for (uint i = 0; i < length; i++) AppHost.WriteChar((char)buffer[i]);
            if (length == MaxFileBytes) Write("\n[sh] ...truncated\n");
            return 0;
        }

        private int Help()
        {
            Write("builtins: cd pwd ls cat echo expect exit help\n");
            Write("expect CODE COMMAND — run it, succeed only on that exit code\n");
            Write("anything else is a path to run: .EXE as a program, .DLL on the hosted runtime\n");
            Write("operators: && || ; and a | b | c   (redirection is not implemented)\n");
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
                AppHost.WriteUInt((uint)status);
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

        private string Resolve(string path)
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

        private static void Write(string text) => AppHost.WriteString(text);

        private static void WriteInt(int value)
        {
            if (value < 0)
            {
                AppHost.WriteString("-");
                AppHost.WriteUInt((uint)(-(long)value));
                return;
            }
            AppHost.WriteUInt((uint)value);
        }
    }
}
