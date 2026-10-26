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
            _cwd = Process.WorkingDirectory;
        }

        /// <summary>Whether the shell runs a line for a caller, its ends being the caller's pipe.</summary>
        private bool HasEnds => _input != null || _output != null;

        public bool ExitRequested { get; private set; }
        public int LastExitCode { get; private set; }
        public string WorkingDirectory => _cwd;

        /// <summary>
        /// One line: parsed once, refused whole when it holds what this shell
        /// does not run, then run a command at a time — each command's words
        /// expanded just before it runs, so <c>export A=1; echo $A</c> sees
        /// the new value, as in bash (step208).
        /// </summary>
        /// <remarks>
        /// ShellSyntaxTree is a safety analyser: it refuses <c>export</c> and
        /// <c>unset</c> outright, as commands that change bindings. They are
        /// renamed before the parse (<see cref="RenameBindingCommands"/>), and
        /// back in <see cref="Split"/>. The parser runs in
        /// IsolatedNonInteractive mode, the one that takes <c>$NAME</c>; what
        /// that mode claims about values is not used — only the syntax is,
        /// and the values come from this process's environment at run time.
        /// </remarks>
        public int RunLine(string line)
        {
            // The parser resolves a redirect's target against its working
            // directory (step197): it gets the shell's, or `> out.txt` lands
            // in the root.
            string source = RenameBindingCommands(line, out _renamedAt);
            var parser = new BashParser(new BashParserOptions
            {
                WorkingDirectory = _cwd.Replace('\\', '/'),
                InitialStateMode = BashInitialStateMode.IsolatedNonInteractive,
            });
            ParsedCommand parsed = parser.Parse(source);
            if (parsed.IsUnparseable)
            {
                Write("sh: cannot parse: ");
                Write(parsed.UnparseableReason ?? line);
                Write("\n");
                return LastExitCode = 2;
            }

            // Everything this shell does not run is refused before anything
            // runs: half a line run is worse than none.
            if (FindUnsupported(parsed, out string what, out int at))
            {
                Write("sh: not supported: ");
                Write(what);
                Write(" (column ");
                WriteInt(OriginalColumn(at) + 1);
                Write(")\n");
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
                // `env A=1 prog` starts the program with its own environment,
                // as a pipeline's stage does.
                if (!skip)
                    last = end - i > 1 || clause.Redirects.Count > 0 || EnvStartsProgram(clause)
                           || (HasEnds && (!IsBuiltin(clause) || WritesText(Verb(clause))))
                        ? RunPipeline(clauses, i, end)
                        : RunClause(clause);
                i = end - 1;
                if (ExitRequested) break;
            }

            return LastExitCode = last;
        }

        // ---- step208: the parse's way in, and what is refused ----

        // Where RenameBindingCommands put "__" in, in the renamed line.
        private List<int> _renamedAt;

        /// <summary>
        /// <c>export</c> and <c>unset</c> as commands — a bare word where a
        /// command starts — become <c>__export</c> and <c>__unset</c>, which
        /// the parser takes for programs. Found with the parser's own lexer, so
        /// a quoted "export" or one inside an argument is left alone.
        /// </summary>
        private static string RenameBindingCommands(string line, out List<int> renamedAt)
        {
            renamedAt = new List<int>();
            IReadOnlyList<ShellSyntaxTree.Internal.Bash.Lexing.BashToken> tokens =
                ShellSyntaxTree.Internal.Bash.Lexing.BashLexer.Tokenize(line);

            var built = new StringBuilder();
            int copied = 0;
            bool commandPlace = true;
            foreach (var token in tokens)
            {
                switch (token.Kind)
                {
                    case ShellSyntaxTree.Internal.Bash.Lexing.BashTokenKind.Whitespace:
                    case ShellSyntaxTree.Internal.Bash.Lexing.BashTokenKind.Continuation:
                    case ShellSyntaxTree.Internal.Bash.Lexing.BashTokenKind.Comment:
                        if (token.IsStatementSeparator) commandPlace = true;
                        continue;
                    case ShellSyntaxTree.Internal.Bash.Lexing.BashTokenKind.Operator:
                        string op = token.OperatorText;
                        commandPlace = op == ";" || op == "|" || op == "&&" || op == "||" || op == "(" || token.IsStatementSeparator;
                        continue;
                    case ShellSyntaxTree.Internal.Bash.Lexing.BashTokenKind.Word:
                        if (commandPlace && (token.Value == "export" || token.Value == "unset")
                            && token.SourceLength == token.Value.Length)
                        {
                            built.Append(line, copied, token.SourceStart - copied);
                            renamedAt.Add(built.Length);
                            built.Append("__");
                            copied = token.SourceStart;
                        }
                        commandPlace = false;
                        continue;
                    default:
                        commandPlace = false;
                        continue;
                }
            }
            if (renamedAt.Count == 0) return line;
            built.Append(line, copied, line.Length - copied);
            return built.ToString();
        }

        // A place in the renamed line as a place in the line typed.
        private int OriginalColumn(int at)
        {
            int column = at;
            if (_renamedAt != null)
                foreach (int inserted in _renamedAt)
                    if (inserted < at) column -= at - inserted < 2 ? at - inserted : 2;
            return column;
        }

        /// <summary>
        /// The first thing in the line this shell does not run: what it is and
        /// where. The syntax tree says which constructs there are; the words
        /// say which expansions — every word is expanded once here without
        /// looking anything up, which finds those that cannot be.
        /// </summary>
        private static bool FindUnsupported(ParsedCommand parsed, out string what, out int at)
        {
            if (FindUnsupportedNode(parsed.Syntax, out what, out at)) return true;

            foreach (Clause clause in parsed.Clauses)
            {
                bool verbSeen = false;
                foreach (ClauseElement element in clause.Elements)
                {
                    int start = element.SourceStart ?? 0;
                    string raw = element.Raw;
                    if (element.Role == ClauseElementRole.Redirect)
                    {
                        string target = RedirectTarget(raw);
                        start += raw.Length - target.Length;
                        raw = target;
                    }
                    else if (element.Role == ClauseElementRole.Verb && !verbSeen)
                    {
                        verbSeen = true;
                        if (raw.IndexOf('$') >= 0)
                        {
                            what = "a command named by a variable";
                            at = start;
                            return true;
                        }
                        if (element.Value.IndexOf('=') > 0 && raw[0] != '"' && raw[0] != '\'')
                        {
                            what = "shell variables — export NAME=value sets an environment variable";
                            at = start;
                            return true;
                        }
                    }
                    try { Expand(raw, false); }
                    catch (UnsupportedExpansion e)
                    {
                        what = e.Message;
                        at = start + e.Offset;
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool FindUnsupportedNode(ShellSyntaxNode node, out string what, out int at)
        {
            what = null;
            at = node.SourceStart ?? 0;
            switch (node)
            {
                case ShellBlockSyntax block:
                    foreach (ShellSyntaxNode statement in block.Statements)
                        if (FindUnsupportedNode(statement, out what, out at)) return true;
                    return false;
                case CommandListSyntax list:
                    foreach (CommandListItemSyntax item in list.Items)
                        if (FindUnsupportedNode(item.Command, out what, out at)) return true;
                    return false;
                case PipelineSyntax pipeline:
                    foreach (ShellSyntaxNode stage in pipeline.Stages)
                        if (FindUnsupportedNode(stage, out what, out at)) return true;
                    return false;
                case SimpleCommandSyntax command:
                    if (command.Substitutions.Count > 0)
                    {
                        what = "command substitution $(...)";
                        at = command.Substitutions[0].SourceStart ?? at;
                        return true;
                    }
                    if (command.ExecutionRegions.Count > 0)
                    {
                        what = "this construct";
                        at = command.ExecutionRegions[0].SourceStart ?? at;
                        return true;
                    }
                    return false;
                case ForEachSyntax:
                    what = "for loops";
                    return true;
                case GroupSyntax:
                    what = "( ... ) and { ...; } groups";
                    return true;
                case CommandSubstitutionSyntax:
                    what = "command substitution $(...)";
                    return true;
                default:
                    what = "this construct";
                    return true;
            }
        }

        private sealed class UnsupportedExpansion : System.Exception
        {
            public readonly int Offset;
            public UnsupportedExpansion(string what, int offset) : base(what) => Offset = offset;
        }

        /// <summary>
        /// A word as the command gets it: quotes removed, <c>\</c> escapes
        /// taken, <c>$NAME</c> and <c>${NAME}</c> replaced by the environment
        /// variable's value — empty when it is not set — outside single quotes,
        /// and <c>~</c> at the start by HOME. No word splitting: a value with
        /// spaces stays one argument. With <paramref name="lookup"/> false
        /// nothing is looked up; the word is only checked.
        /// </summary>
        /// <exception cref="UnsupportedExpansion">An expansion this shell does not do.</exception>
        private static string Expand(string raw, bool lookup)
        {
            var built = new StringBuilder();
            bool doubleQuoted = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\'' && !doubleQuoted)
                {
                    int close = raw.IndexOf('\'', i + 1);
                    if (close < 0) close = raw.Length;
                    built.Append(raw, i + 1, close - i - 1);
                    i = close;
                    continue;
                }
                if (c == '"')
                {
                    doubleQuoted = !doubleQuoted;
                    continue;
                }
                if (c == '\\' && i + 1 < raw.Length)
                {
                    // Inside double quotes a backslash escapes only these.
                    char next = raw[i + 1];
                    if (!doubleQuoted || next == '$' || next == '"' || next == '\\' || next == '`')
                    {
                        built.Append(next);
                        i++;
                    }
                    else if (next == '\n')
                    {
                        i++;
                    }
                    else
                    {
                        built.Append(c);
                    }
                    continue;
                }
                if (c == '~' && i == 0 && (raw.Length == 1 || raw[1] == '/'))
                {
                    if (lookup) built.Append(System.Environment.GetEnvironmentVariable("HOME") ?? "");
                    continue;
                }
                if (c == '$' && i + 1 < raw.Length)
                {
                    char next = raw[i + 1];
                    string name = null;
                    int last = i;
                    if (next == '{')
                    {
                        int close = raw.IndexOf('}', i + 2);
                        if (close < 0 || !IsName(raw.Substring(i + 2, close - i - 2)))
                            throw new UnsupportedExpansion("${...} other than ${NAME}", i);
                        name = raw.Substring(i + 2, close - i - 2);
                        last = close;
                    }
                    else if (IsNameStart(next))
                    {
                        int j = i + 1;
                        while (j < raw.Length && IsNameChar(raw[j])) j++;
                        name = raw.Substring(i + 1, j - i - 1);
                        last = j - 1;
                    }
                    else if ((next >= '0' && next <= '9') || "?#@*!$-".IndexOf(next) >= 0)
                    {
                        throw new UnsupportedExpansion("$" + next.ToString() + " (only $NAME and ${NAME})", i);
                    }
                    else if (next == '(' || next == '\'' || (next == '"' && !doubleQuoted))
                    {
                        throw new UnsupportedExpansion("$" + next.ToString() + "...", i);
                    }

                    if (name != null)
                    {
                        if (lookup) built.Append(System.Environment.GetEnvironmentVariable(name) ?? "");
                        i = last;
                        continue;
                    }
                }
                built.Append(c);
            }
            return built.ToString();
        }

        // The target of `> file` / `>> file` as written: the operator and
        // the blanks after it gone, the word left to expand.
        private static string RedirectTarget(string raw)
        {
            int i = 0;
            while (i < raw.Length && raw[i] >= '0' && raw[i] <= '9') i++;
            while (i < raw.Length && (raw[i] == '>' || raw[i] == '<' || raw[i] == '|' || raw[i] == '&')) i++;
            while (i < raw.Length && (raw[i] == ' ' || raw[i] == '\t')) i++;
            return raw.Substring(i);
        }

        /// <summary>A stage's `n> file`, `n>&amp;m` or `n>&amp;-`, in the order written (step212).</summary>
        private sealed class Redirection
        {
            public int Number;
            public string Target;
            public bool Append;
            // `n>&m`: n gets what m has at this point; Discard for `n>&-`; -1 for a file.
            public int CopyOf = -1;
        }

        // Redirection.CopyOf of `n>&-`: n goes nowhere.
        private const int Discard = 0;

        /// <summary>
        /// The operator of a redirect as written: `>` / `>>` with an optional
        /// end number before it (1 when none; 9 at most), `n>&amp;m`
        /// (<paramref name="copyOf"/> m) or `n>&amp;-` (<see cref="Discard"/>);
        /// -1 for a file. False for anything else — `<`, `&>`, here-documents —
        /// which this shell does not run.
        /// </summary>
        private static bool ParseRedirect(string raw, out int number, out bool append, out int copyOf)
        {
            number = 1;
            append = false;
            copyOf = -1;
            int i = 0;
            while (i < raw.Length && raw[i] >= '0' && raw[i] <= '9') i++;
            if (i > 1) return false;
            if (i == 1) number = raw[0] - '0';
            if (i >= raw.Length || raw[i] != '>') return false;
            i++;
            if (i < raw.Length && raw[i] == '&')
            {
                i++;
                if (i < raw.Length && raw[i] == '-') copyOf = Discard;
                else if (i < raw.Length && raw[i] >= '1' && raw[i] <= '9') copyOf = raw[i] - '0';
                else return false;
                i++;
                return i == raw.Length && number >= 1;
            }
            if (i < raw.Length && raw[i] == '>') { append = true; i++; }
            if (i < raw.Length && (raw[i] == '>' || raw[i] == '&' || raw[i] == '|')) return false;
            return number >= 1;
        }

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
        private int RunPipeline(IReadOnlyList<Clause> clauses, int first, int end)
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
                string[] environment = null;
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
                        verb = program;
                        args = rest;
                        environment = programEnvironment;
                    }
                }
                if (environment == null && IsBuiltinName(verb))
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
                environments.Add(environment);

                // The redirects, in order, as in sh (step212): `n> f` / `n>> f`
                // a WRITE beside the stage, `n>&m` what m has at that point,
                // `n>&-` nowhere. 1 is the output, 2 the error stream, 3 stdtext;
                // `3>&1 1>&-` sends stdtext on down the pipeline instead.
                List<Redirection> stageRedirects = null;
                var named = new bool[10];
                named[1] = true;
                foreach (ClauseElement element in stage.Elements)
                {
                    if (element.Role != ClauseElementRole.Redirect) continue;
                    if (!ParseRedirect(element.Raw, out int number, out bool append, out int copyOf))
                    {
                        Write("sh: only `n> file`, `n>> file`, `n>&m` and `n>&-` are supported (n 1 to 9; 1 when left out)\n");
                        return 2;
                    }
                    if (builtinFirst && k == first && (number != 1 || copyOf > 0))
                    {
                        Write("sh: `");
                        Write(element.Raw);
                        Write("` is for programs; ");
                        Write(verb);
                        Write(" has only its output, for `> file` or `>&-`\n");
                        return 2;
                    }
                    if (copyOf > 0 && !named[copyOf])
                    {
                        Write("sh: `" + element.Raw + "`: " + copyOf.ToString()
                              + " is not redirected before it on this stage\n");
                        return 2;
                    }
                    named[number] = true;
                    string target = copyOf >= 0 ? null : Resolve(Expand(RedirectTarget(element.Raw), true));
                    stageRedirects ??= new List<Redirection>();
                    stageRedirects.Add(new Redirection { Number = number, Target = target, Append = append, CopyOf = copyOf });
                }
                redirects.Add(stageRedirects);
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
                try
                {
                    var ends = new List<(int, PipeWriteEnd)>();
                    PipeWriteEnd output = HandOut(redirects[k], Natural(k, count, pairs, owned),
                                                  sideStarted, sidePairs, owned, ends);
                    started.Add(Process.Start(path, arguments[k].ToArray(),
                        k > 0 ? pairs[k - 1].ReadEnd : TakeInput(),
                        output, environments[k], ends.Count > 0 ? ends.ToArray() : null));
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
                }
            }

            // The builtin runs once the stages after it are up: its text has
            // somewhere to go, however much of it there is.
            int builtinCode = 0;
            if (builtinFirst && !failed)
            {
                var owned = new List<PipeWriteEnd>();
                try
                {
                    PipeWriteEnd output = HandOut(redirects[0], Natural(0, count, pairs, owned),
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
            // The input goes to one command. The output stays the shell's: each
            // command got a copy (TakeOutput), and the reader's stream ends when
            // the shell does.
            _input?.Dispose();
            _input = null;

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

        /// <summary>The exit code of a stage whose neighbour's end broke: quiet, never the pipeline's failure.</summary>
        private const int QuietExitCode = 141;

        // The builtins that print and change nothing: they may start a pipeline.
        private static bool WritesText(string verb)
            => verb == "echo" || verb == "pwd" || verb == "ls" || verb == "dir" || verb == "cat" || verb == "type"
               || verb == "history" || verb == "help" || verb == "env" || verb == "which" || verb == "df";

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

        // Where stage k writes when nothing is redirected: the pipe to the next
        // stage, or the shell's own output — a copy, the shell's to close if
        // the stage does not take it; null: the screen.
        private PipeWriteEnd Natural(int k, int count, SharpOS.Std.Pipes.PipePair[] pairs, List<PipeWriteEnd> owned)
        {
            if (k < count - 1) return pairs[k].WriteEnd;
            PipeWriteEnd output = TakeOutput();
            if (output != null) owned.Add(output);
            return output;
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

        // The shell's own input, handed to one stage once.
        private PipeReadEnd TakeInput()
        {
            PipeReadEnd end = _input;
            _input = null;
            return end;
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

        /// <summary>The builtins, for help and Tab completion.</summary>
        public static string[] Builtins => new[]
        {
            "buildinfo", "cat", "cd", "clear", "dir", "echo", "env", "exit", "expect", "export", "help", "history", "ls",
            "mkdir", "mv", "pwd", "rm", "rmdir", "type", "unset", "which", "cp", "df",
        };

        /// <summary>The prompt's history (Program.Interactive sets it; a script has none).</summary>
        public List<string> History { get; set; }

        private static string Verb(Clause clause) => Split(clause, new List<string>());

        private static bool IsBuiltin(Clause clause) => IsBuiltinName(Verb(clause));

        private static bool IsBuiltinName(string verb)
        {
            foreach (string builtin in Builtins)
                if (verb == builtin) return true;
            return false;
        }

        // The verb and the arguments of a clause, expanded now (step208): from
        // the words as written, not from the parser's values — those are its
        // guesses at what a variable holds.
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
                    verb = Expand(element.Raw, true);
                    if (verb == "__export" || verb == "__unset") verb = verb.Substring(2);
                }
                else if (element.Role != ClauseElementRole.Redirect)
                {
                    args.Add(Expand(element.Raw, true));
                }
            }
            return verb;
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

        private static bool EnvStartsProgram(Clause clause)
        {
            var args = new List<string>();
            if (Split(clause, args) != "env") return false;
            foreach (string arg in args)
                if (arg.IndexOf('=') < 0) return true;
            return false;
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
                // `export NAME` exports a shell variable in bash; this shell
                // has none, so there is nothing to do.
                if (eq < 0) continue;
                System.Environment.SetEnvironmentVariable(name, arg.Substring(eq + 1));
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
                WritePainted("\u001b[1;34m", info.Name + "\\");
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
                string to = intoDirectory ? last + "\\" + LastName(from) : last;
                try
                {
                    if (System.IO.Directory.Exists(from))
                    {
                        if (!recursive) { Complain("cp", from, "is a directory (cp -r copies it)"); code = 1; continue; }
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
            foreach (string f in System.IO.Directory.GetFiles(from)) System.IO.File.Copy(f, to + "\\" + LastName(f), true);
            foreach (string d in System.IO.Directory.GetDirectories(from)) CopyTree(d, to + "\\" + LastName(d));
        }

        // df: the volume's size, what is used and what is free.
        private int DiskFree()
        {
            var drive = new System.IO.DriveInfo("\\");
            try
            {
                long total = drive.TotalSize, free = drive.TotalFreeSpace;
                Write("volume     size      used      free  use%\n");
                Write("\\    ");
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
                string to = intoDirectory ? last + "\\" + LastName(from) : last;
                try
                {
                    if (System.IO.Directory.Exists(from)) System.IO.Directory.Move(from, to);
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

        private static string LastName(string path)
        {
            int end = path.Length;
            while (end > 1 && path[end - 1] == '\\') end--;
            int start = end;
            while (start > 0 && path[start - 1] != '\\') start--;
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
            if (args.Count == 0)
            {
                string home = System.Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(home)) { Write("cd: HOME is not set\n"); return 1; }
                _cwd = home;
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
            if (details) return ListDetails(paths.Count == 0 ? _cwd : Resolve(paths[0]));
            args = paths;
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
            Write("builtins: cd pwd ls cat echo clear history buildinfo expect exit help export unset env which\n");
            Write("files: mkdir [-p] DIR, rm [-r] [-f] PATH, rmdir DIR, mv SRC DST (or into DIR),\n");
            Write("       cp [-r] SRC DST (or into DIR), ls -l (size, last write), df (the volume's room)\n");
            Write("environment: export NAME=value, unset NAME, env lists; env NAME=value PROGRAM args runs it with them;\n");
            Write("             $NAME and ${NAME} in words and in \"...\", not in '...'; ~ is HOME\n");
            Write("             ENV names a script the prompt runs first\n");
            Write("buildinfo on|off — programs print their build line at start (on in an autorun battery)\n");
            Write("programs: NAME.EXE by its name, looked for along PATH (" + (System.Environment.GetEnvironmentVariable("PATH") ?? "") + ")\n");
            Write("expect CODE COMMAND — run it, succeed only on that exit code\n");
            Write("a path runs too: .EXE as a program, .DLL on the hosted runtime\n");
            Write("operators: && || ; a | b | c ; n> file, n>> file, n>&m, n>&- on any program, in order:\n");
            Write("           1 the output (left out: > file), 2 errors, 3 stdtext (reports, panels);\n");
            Write("           sysinfo 3>&1 1>&- | less — the panel down the pipe, the data nowhere\n");
            Write("not here: shell variables (x=1), $(...), loops, if, functions, X=1 prog\n");
            Write("keys: Up/Down history, Tab completes, Home/End, Ctrl+A/E/U/K/W, Ctrl+L clears,\n");
            Write("      Ctrl+C drops the line, Ctrl+D on an empty line leaves\n");
            // Worth saying out loud: this is bash syntax, so a backslash
            // escapes the next character and eats itself: /bin/X.EXE works,
            // a backslash path loses its separators.
            Write("paths: use /bin/NAME.EXE — a backslash is an escape here, not a separator\n");
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

            // ".", ".." and the root taken here (step208), not handed to FAT.
            return Process.NormalizePath(built.ToString());
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
