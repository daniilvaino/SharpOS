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
            foreach (Clause clause in parsed.Clauses)
            {
                if (clause.Operator == CompoundOperator.AndIf && last != 0) continue;
                if (clause.Operator == CompoundOperator.OrIf && last == 0) continue;

                if (clause.Operator == CompoundOperator.Pipe)
                {
                    // Saying so rather than running the left half and dropping
                    // the right: a pipeline that silently loses its second
                    // stage is worse than one that refuses.
                    Write("sh: pipelines are not supported yet\n");
                    return LastExitCode = 2;
                }

                last = RunClause(clause);
                if (ExitRequested) break;
            }

            return LastExitCode = last;
        }

        private int RunClause(Clause clause)
        {
            if (clause.Redirects.Count > 0)
            {
                Write("sh: redirection is not supported yet\n");
                return 2;
            }

            string verb = "";
            var args = new List<string>();

            // Elements carry the decoded value; Raw still has its quotes.
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
            Write("operators: && || ;   (pipes and redirection are not implemented)\n");
            // Worth saying out loud: this is bash syntax, so a backslash
            // escapes the next character and eats itself. /apps/X.EXE works,
            // \apps\X.EXE arrives as appsX.EXE.
            Write("paths: use /apps/NAME.EXE — a backslash is an escape here, not a separator\n");
            return 0;
        }

        private int RunProgram(string verb, List<string> args, bool reportNonZero = true)
        {
            string path = LooksLikePath(verb) ? Resolve(verb) : AppDirectory + "\\" + verb;

            if (AppHost.FileExistsEx(path) != AppServiceStatus.Ok)
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
                return 127;
            }

            // The service ABI starts a program with no argv, so arguments
            // would vanish. Refusing beats pretending they were delivered.
            if (args.Count > 0)
            {
                Write("sh: arguments are not passed to programs yet, refusing to run ");
                Write(path);
                Write("\n");
                return 2;
            }

            bool managed = EndsWith(path, ".dll");
            AppServiceStatus status = managed
                ? AppHost.TryRunManagedApp(path, out int exitCode)
                : AppHost.TryRunApp(path, out exitCode);

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
