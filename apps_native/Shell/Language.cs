using System.Collections.Generic;
using System.Text;
using Meziantou.Framework.Language;
using Meziantou.Framework.Language.Shell;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace Shell
{
    // The language: the tree the parser gives, walked. if, while, until, for,
    // case, { }, ( ), && || !, variables, $(...), $((...)), globs. Each command
    // is expanded just before it runs, so `x=1; echo $x` sees the new value.
    internal sealed unsafe partial class Executor
    {
        // Shell variables: not handed to what is started. A name in the
        // environment stays there (Assign); export moves one over.
        private readonly Dictionary<string, string> _variables = new Dictionary<string, string>();

        // $1 ...: the arguments the script was given.
        private string[] _positional = new string[0];

        // break N and continue N: how many loops are left to leave.
        private int _breaks, _continues;

        // Inside $(...): the commands' output goes to _capture.
        private bool _capturing;

        private sealed class ShellError : System.Exception
        {
            public readonly int Code;
            public ShellError(string message, int code = 1) : base(message) => Code = code;
        }

        /// <summary>
        /// One line or script: parsed once, refused whole when it holds what
        /// this shell does not run, since half of it run is worse than none.
        /// </summary>
        public int RunLine(string line)
        {
            ShellSyntaxTree tree = ShellSyntaxTree.ParseText(line, ShellDialect.Bash);
            foreach (Diagnostic d in tree.GetDiagnostics())
            {
                if (d.Severity != DiagnosticSeverity.Error) continue;
                Write("sh: ");
                Write("cannot parse: " + d.Message + " (" + Place(d.Location) + ")\n");
                return LastExitCode = 2;
            }

            ShellSyntaxNode root = tree.GetRoot();
            string what = FindUnsupported(root, out int where);
            if (what != null)
            {
                int lineNumber = 1, column = 1;
                for (int i = 0; i < where && i < line.Length; i++)
                {
                    if (line[i] == '\n') { lineNumber++; column = 1; }
                    else column++;
                }
                Write("sh: ");
                Write("not supported: " + what + " (line " + lineNumber.ToString() + ", column " + column.ToString() + ")\n");
                return LastExitCode = 2;
            }

            try
            {
                RunList(((ShellScriptSyntax)root).Statements);
            }
            catch (ShellError e)
            {
                Write("sh: ");
                Write(e.Message + "\n");
                LastExitCode = e.Code;
            }
            _breaks = _continues = 0;
            return LastExitCode;
        }

        /// <summary>$1 and on: what the script was given.</summary>
        public void SetArguments(string[] arguments) => _positional = arguments;

        /// <summary>Whether a line ends inside something: an `if` without `fi`, an open quote, a `|` with nothing after it.</summary>
        public static bool IsIncomplete(string text)
        {
            int end = text.TrimEnd().Length;
            foreach (Diagnostic d in ShellSyntaxTree.ParseText(text, ShellDialect.Bash).GetDiagnostics())
                if (d.Severity == DiagnosticSeverity.Error && d.Location.SourceSpan.Start + d.Location.SourceSpan.Length >= end)
                    return true;
            return false;
        }

        private static string Place(Location location)
        {
            LinePositionSpan span = location.GetLineSpan();
            return "line " + (span.Start.Line + 1).ToString() + ", column " + (span.Start.Character + 1).ToString();
        }

        // ---- what is refused ----

        private static string FindUnsupported(ShellSyntaxNode root, out int where)
        {
            foreach (SyntaxNode node in root.DescendantNodesAndSelf())
            {
                where = node.Span.Start;
                switch (node)
                {
                    case PosixFunctionDefinitionSyntax: return "functions";
                    case PosixProcessSubstitutionSyntax: return "process substitution";
                    case PosixDelimitedExpressionStatementSyntax: return "[[ ]] and (( )) — test and $(( )) are here";
                    case PosixArrayAssignmentSyntax: return "arrays";
                    case PosixHereDocumentSyntax: return "here-documents";
                    case PosixPrefixedStatementSyntax: return "this construct";
                    case ZshForeachStatementSyntax:
                    case ZshRepeatStatementSyntax:
                    case ZshAlwaysStatementSyntax: return "this construct";
                    case ShellRedirectionSyntax redirection:
                        string op = redirection.OperatorToken.Text;
                        if (op != ">" && op != ">>" && op != ">&" && op != "<")
                            return "the redirect " + op + " — n> file, n>> file, n>&m, n>&- and < file are here";
                        break;
                    case ShellStatementListSyntax list:
                        foreach (SyntaxToken separator in list.SeparatorTokens)
                            if (separator.Text == "&") return "background jobs (&)";
                        break;
                    case ShellPipelineSyntax pipeline:
                        foreach (SyntaxToken separator in pipeline.OperatorTokens)
                            if (separator.Text != "|") return "the pipe " + separator.Text;
                        break;
                    case PosixForStatementSyntax loop:
                        if (loop.AdditionalVariableTokens.Count > 0) return "a for over several names";
                        break;
                    case ShellVariableReferenceSyntax variable:
                        if (variable.IsBraced && !ParseBraced(variable.Name, out _, out _, out _))
                            return "${" + variable.Name + "} — ${NAME}, ${#NAME} and ${NAME:-word} with := :+ :? are here";
                        break;
                    case ShellRawExpressionSyntax:
                        return "this arithmetic";
                }
            }
            where = 0;
            return null;
        }

        // ---- statements ----

        private bool Stopping => ExitRequested || _breaks > 0 || _continues > 0;

        private int RunList(ShellStatementListSyntax list)
        {
            foreach (ShellStatementSyntax statement in list.Statements)
            {
                if (statement is ShellEmptyStatementSyntax) continue;
                LastExitCode = RunStatement(statement);
                if (Stopping) break;
            }
            return LastExitCode;
        }

        private int RunStatement(ShellStatementSyntax statement)
        {
            switch (statement)
            {
                case ShellCommandListSyntax list: return RunAndOr(list);
                case ShellPipelineSyntax pipeline: return RunPipelineStatement(pipeline);
                case ShellCommandSyntax command: return RunCommand(command);
                case PosixIfStatementSyntax conditional: return RunIf(conditional);
                case PosixWhileStatementSyntax loop: return RunWhile(loop);
                case PosixForStatementSyntax loop: return RunFor(loop);
                case PosixCaseStatementSyntax choice: return RunCase(choice);
                case PosixCompoundStatementSyntax block:
                    if (block.OpenToken.Text != "(") return RunList(block.Statements);
                    return InScope(() => RunList(block.Statements));
                case PosixRedirectedStatementSyntax:
                    return RunPipeline(new List<Stage> { MakeStage(statement) });
                default:
                    throw new ShellError("this construct cannot run: " + statement.ToString(), 2);
            }
        }

        private int RunAndOr(ShellCommandListSyntax list)
        {
            int code = RunStatement(list.Pipelines[0]);
            LastExitCode = code;
            IReadOnlyList<SyntaxToken> operators = list.OperatorTokens;
            for (int i = 1; i < list.Pipelines.Count && !Stopping; i++)
            {
                bool and = operators[i - 1].Text == "&&";
                if (and != (code == 0)) continue;
                code = RunStatement(list.Pipelines[i]);
                LastExitCode = code;
            }
            return code;
        }

        private int RunPipelineStatement(ShellPipelineSyntax pipeline)
        {
            int code;
            if (pipeline.Commands.Count == 1)
            {
                code = RunStatement(pipeline.Commands[0]);
            }
            else
            {
                var stages = new List<Stage>();
                foreach (ShellStatementSyntax command in pipeline.Commands) stages.Add(MakeStage(command));
                code = RunPipeline(stages);
            }
            if (pipeline.BangToken.IsPresent()) code = code == 0 ? 1 : 0;
            return code;
        }

        private int RunIf(PosixIfStatementSyntax conditional)
        {
            if (RunList(conditional.Condition) == 0 && !Stopping) return RunList(conditional.Body);
            if (Stopping) return LastExitCode;
            foreach (PosixElifClauseSyntax clause in conditional.ElifClauses)
            {
                if (RunList(clause.Condition) == 0 && !Stopping) return RunList(clause.Body);
                if (Stopping) return LastExitCode;
            }
            return conditional.ElseClause != null ? RunList(conditional.ElseClause.Body) : 0;
        }

        private int RunWhile(PosixWhileStatementSyntax loop)
        {
            bool until = loop.Keyword.Text == "until";
            int code = 0;
            while (true)
            {
                int test = RunList(loop.Condition);
                if (Stopping || (test == 0) == until) break;
                code = RunList(loop.Body);
                if (AfterBody()) break;
            }
            return code;
        }

        private int RunFor(PosixForStatementSyntax loop)
        {
            var items = new List<string>();
            if (loop.InKeyword.IsPresent())
            {
                foreach (ShellWordSyntax word in loop.Items) ExpandFields(word, items);
            }
            else
            {
                items.AddRange(_positional);
            }
            string name = loop.VariableToken.Text;
            int code = 0;
            foreach (string item in items)
            {
                Assign(name, item);
                code = RunList(loop.Body);
                if (AfterBody()) break;
            }
            return code;
        }

        // A body has run: whether the loop ends. break N takes one level here
        // and leaves N-1 to the loop around this one; continue N likewise.
        private bool AfterBody()
        {
            if (ExitRequested) return true;
            if (_breaks > 0)
            {
                _breaks--;
                return true;
            }
            if (_continues > 0)
            {
                _continues--;
                return _continues > 0;
            }
            return false;
        }

        private int RunCase(PosixCaseStatementSyntax choice)
        {
            string subject = ExpandOne(choice.Subject);
            foreach (PosixCaseClauseSyntax clause in choice.Clauses)
            {
                foreach (ShellWordSyntax pattern in clause.Patterns)
                {
                    Piece piece = ExpandPattern(pattern);
                    if (Matches(piece.Text, piece.Mask, 0, subject, 0, false))
                        return RunList(clause.Body);
                }
            }
            return 0;
        }

        // ( ... ) and $(...): what changes inside stays inside.
        private int InScope(System.Func<int> run)
        {
            var variables = new Dictionary<string, string>(_variables);
            string[] environment = SharpOS.Std.NoRuntime.EnvironmentStore.GetAll();
            string directory = _cwd;
            int code;
            try
            {
                code = run();
            }
            finally
            {
                _breaks = _continues = 0;
                ExitRequested = false;
                _variables.Clear();
                foreach (KeyValuePair<string, string> v in variables) _variables[v.Key] = v.Value;
                RestoreEnvironment(environment);
                if (_cwd != directory)
                {
                    _cwd = directory;
                    Process.WorkingDirectory = directory;
                }
            }
            return code;
        }

        private static void RestoreEnvironment(string[] saved)
        {
            foreach (string entry in SharpOS.Std.NoRuntime.EnvironmentStore.GetAll())
            {
                int eq = entry.IndexOf('=');
                if (eq > 0) System.Environment.SetEnvironmentVariable(entry.Substring(0, eq), null);
            }
            foreach (string entry in saved)
            {
                int eq = entry.IndexOf('=');
                if (eq > 0) System.Environment.SetEnvironmentVariable(entry.Substring(0, eq), entry.Substring(eq + 1));
            }
        }

        // ---- a command ----

        private sealed class Stage
        {
            public string Verb = "";
            public List<string> Arguments = new List<string>();
            public string[] Environment;
            public List<Redirection> Redirects;
            public string InputFile;
        }

        private int RunCommand(ShellCommandSyntax command)
        {
            Stage stage = MakeStage(command);
            if (stage.Verb.Length == 0)
            {
                // x=1, > file: the substitution's code stands if there was one.
                ApplyAssignments(command);
                if (stage.Redirects != null)
                    foreach (Redirection r in stage.Redirects)
                        if (r.Target != null)
                        {
                            using var file = AppFile.Open(r.Target, r.Append ? AppFile.ModeAppend : AppFile.ModeWrite);
                        }
                return _substituted ? LastExitCode : 0;
            }

            bool builtin = IsBuiltinName(stage.Verb);
            bool pipeline = stage.Redirects != null || stage.InputFile != null
                            || (stage.Verb == "env" && EnvStartsProgram(stage.Arguments))
                            || stage.Environment != null && !builtin
                            || (CanShow && !builtin && !EndsWith(stage.Verb, ".dll"))
                            || (HasEnds && !_capturing && (!builtin || WritesText(stage.Verb)));
            if (pipeline) return RunPipeline(new List<Stage> { stage });
            if (Inspector == null || _capturing || !WritesText(stage.Verb)) return RunBuiltin(stage.Verb, stage.Arguments);
            Inspector.Begin(Describe(new List<string> { stage.Verb }, new List<List<string>> { stage.Arguments }));
            _teeInspector = true;
            try { return RunBuiltin(stage.Verb, stage.Arguments); }
            finally { _teeInspector = false; }
        }

        // Set by a $(...) in the command being made: `x=$(false)` is 1.
        private bool _substituted;

        private Stage MakeStage(ShellStatementSyntax statement)
        {
            if (statement is ShellCommandSyntax command) return MakeCommandStage(command);

            // A compound stage (`for ...; done | head`, `while ...; done < f`):
            // a shell of its own, with this one's variables in its environment.
            var stage = new Stage { Verb = "SHELL" };
            ShellStatementSyntax body = statement;
            if (statement is PosixRedirectedStatementSyntax redirected)
            {
                body = redirected.Statement;
                foreach (ShellRedirectionSyntax r in redirected.Redirections) AddRedirect(stage, r);
            }
            stage.Arguments.Add("-c");
            stage.Arguments.Add(body.ToFullString().Trim());
            string[] environment = SharpOS.Std.NoRuntime.EnvironmentStore.GetAll();
            foreach (KeyValuePair<string, string> v in _variables)
                if (v.Value.Length > 0)
                    environment = SharpOS.Std.NoRuntime.EnvironmentBlock.With(environment, v.Key, v.Value);
            stage.Environment = environment;
            return stage;
        }

        private Stage MakeCommandStage(ShellCommandSyntax command)
        {
            _substituted = false;
            var stage = new Stage();
            var fields = new List<string>();
            bool named = false;
            foreach (ShellSyntaxNode element in command.Elements)
            {
                switch (element)
                {
                    case ShellWordSyntax word:
                        named = true;
                        ExpandFields(word, fields);
                        break;
                    case ShellAssignmentSyntax assignment when named:
                        // After the name it is an argument: export A=1.
                        fields.Add(assignment.Name + "=" + (assignment.Value != null ? ExpandOne(assignment.Value) : ""));
                        break;
                    case ShellRedirectionSyntax redirection:
                        AddRedirect(stage, redirection);
                        break;
                }
            }
            if (fields.Count == 0) return stage;

            stage.Verb = fields[0];
            // FAT names have no case, so neither do programs; the builtins
            // answer in any case too (step213).
            string lower = stage.Verb.ToLowerInvariant();
            if (lower != stage.Verb && IsBuiltinName(lower)) stage.Verb = lower;
            fields.RemoveAt(0);
            stage.Arguments = fields;

            // X=1 program: for it alone.
            foreach (ShellSyntaxNode element in command.Elements)
            {
                if (element is ShellWordSyntax) break;
                if (element is not ShellAssignmentSyntax assignment) continue;
                stage.Environment = SharpOS.Std.NoRuntime.EnvironmentBlock.With(
                    stage.Environment ?? SharpOS.Std.NoRuntime.EnvironmentStore.GetAll(),
                    assignment.Name, assignment.Value != null ? ExpandOne(assignment.Value) : "");
            }
            return stage;
        }

        private void ApplyAssignments(ShellCommandSyntax command)
        {
            foreach (ShellSyntaxNode element in command.Elements)
                if (element is ShellAssignmentSyntax assignment)
                    Assign(assignment.Name, assignment.Value != null ? ExpandOne(assignment.Value) : "");
        }

        private void AddRedirect(Stage stage, ShellRedirectionSyntax redirection)
        {
            string op = redirection.OperatorToken.Text;
            string target = redirection.Target != null ? ExpandOne(redirection.Target) : "";
            if (op == "<")
            {
                stage.InputFile = Resolve(target);
                return;
            }
            var made = new Redirection { Written = redirection.ToString() };
            made.Number = redirection.IoNumberToken.IsPresent() && int.TryParse(redirection.IoNumberToken.Text, out int n) ? n : 1;
            if (made.Number < 1 || made.Number > 9)
                throw new ShellError("`" + made.Written + "`: the end is 1 to 9", 2);
            if (op == ">&")
            {
                if (target == "-") made.CopyOf = Discard;
                else if (target.Length == 1 && target[0] >= '1' && target[0] <= '9') made.CopyOf = target[0] - '0';
                else throw new ShellError("`" + made.Written + "`: n>&m and n>&- are here, m 1 to 9", 2);
            }
            else
            {
                made.Append = op == ">>";
                made.Target = Resolve(target);
            }
            stage.Redirects ??= new List<Redirection>();
            stage.Redirects.Add(made);
        }

        private static bool EnvStartsProgram(List<string> args)
        {
            foreach (string arg in args)
                if (arg.IndexOf('=') < 0) return true;
            return false;
        }

        // ---- variables ----

        private void Assign(string name, string value)
        {
            if (System.Environment.GetEnvironmentVariable(name) != null)
                System.Environment.SetEnvironmentVariable(name, value);
            else
                _variables[name] = value;
        }

        private string Lookup(string name)
        {
            if (name.Length == 1)
            {
                char c = name[0];
                if (c == '?') return LastExitCode.ToString();
                if (c == '$') return Process.CurrentId.ToString();
                if (c == '#') return _positional.Length.ToString();
                if (c == '0') return "sh";
                if (c == '*' || c == '@') return string.Join(" ", _positional);
                if (c == '!' || c == '-') return "";
            }
            if (name[0] >= '1' && name[0] <= '9' && int.TryParse(name, out int index))
                return index <= _positional.Length ? _positional[index - 1] : "";
            if (_variables.TryGetValue(name, out string value)) return value;
            return System.Environment.GetEnvironmentVariable(name) ?? "";
        }

        private bool IsSet(string name)
            => _variables.ContainsKey(name) || System.Environment.GetEnvironmentVariable(name) != null
               || (name[0] >= '1' && name[0] <= '9' && Lookup(name).Length > 0);

        // ${NAME}, ${#NAME}, ${NAME:-word} and the rest of POSIX's four, with
        // the colon (empty counts as unset) or without.
        private static bool ParseBraced(string body, out string name, out string op, out string word)
        {
            name = body;
            op = null;
            word = null;
            if (body.Length == 0) return false;
            if (body[0] == '#' && body.Length > 1)
            {
                op = "#";
                name = body.Substring(1);
                return IsParameter(name);
            }
            int at = 0;
            if (IsNameStart(body[0])) while (at < body.Length && IsNameChar(body[at])) at++;
            else if ((body[0] >= '0' && body[0] <= '9') || "?#@*!$-".IndexOf(body[0]) >= 0) at = 1;
            if (at == 0) return false;
            name = body.Substring(0, at);
            if (at == body.Length) return true;
            int opEnd = body[at] == ':' ? at + 1 : at;
            if (opEnd >= body.Length || "-=+?".IndexOf(body[opEnd]) < 0) return false;
            op = body.Substring(at, opEnd - at + 1);
            word = body.Substring(opEnd + 1);
            return true;
        }

        private static bool IsParameter(string name)
            => IsName(name) || (name.Length == 1 && ((name[0] >= '0' && name[0] <= '9') || "?#@*!$-".IndexOf(name[0]) >= 0));

        private string Braced(string body)
        {
            ParseBraced(body, out string name, out string op, out string word);
            if (op == null) return Lookup(name);
            if (op == "#") return Lookup(name).Length.ToString();
            string value = Lookup(name);
            bool colon = op[0] == ':';
            bool present = colon ? value.Length > 0 : IsSet(name);
            switch (op[op.Length - 1])
            {
                case '-': return present ? value : ExpandText(word);
                case '+': return present ? ExpandText(word) : "";
                case '=':
                    if (present) return value;
                    value = ExpandText(word);
                    Assign(name, value);
                    return value;
                default:
                    if (present) return value;
                    throw new ShellError(name + ": " + (word.Length > 0 ? ExpandText(word) : "not set"));
            }
        }

        // The word in ${NAME:-word}: the parser keeps it as text.
        private string ExpandText(string text)
        {
            ShellSyntaxTree tree = ShellSyntaxTree.ParseText(": " + text, ShellDialect.Bash);
            var built = new StringBuilder();
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
            {
                if (node is not ShellCommandSyntax command) continue;
                IReadOnlyList<ShellWordSyntax> words = command.Arguments;
                for (int i = 0; i < words.Count; i++)
                {
                    if (i > 0) built.Append(' ');
                    built.Append(ExpandOne(words[i]));
                }
                break;
            }
            return built.ToString();
        }

        // ---- words ----

        // A word being built: its text, and for each character whether it is an
        // active glob character (not quoted, not from a quoted expansion).
        private sealed class Piece
        {
            public readonly StringBuilder Text = new StringBuilder();
            public readonly StringBuilder Mask = new StringBuilder();
            public bool Quoted;

            public void Add(string text, bool glob)
            {
                Text.Append(text);
                Mask.Append(glob ? 'g' : ' ', text.Length);
            }
        }

        // A word as fields: split at blanks where an expansion was not quoted,
        // then matched against files where a glob character is active.
        private void ExpandFields(ShellWordSyntax word, List<string> into)
        {
            var pieces = new List<Piece>();
            var current = new Piece();
            foreach (ShellWordPartSyntax part in word.Parts)
                current = AddPart(part, false, current, pieces, true);
            if (current.Quoted || current.Text.Length > 0) pieces.Add(current);
            foreach (Piece piece in pieces)
                Glob(piece, into);
        }

        // A word as one string: a redirect's target, a value. No splitting, no files.
        private string ExpandOne(ShellWordSyntax word)
        {
            var pieces = new List<Piece>();
            var current = new Piece();
            foreach (ShellWordPartSyntax part in word.Parts)
                current = AddPart(part, false, current, pieces, false);
            return current.Text.ToString();
        }

        // A case pattern: quoted parts literal, the rest a pattern.
        private Piece ExpandPattern(ShellWordSyntax word)
        {
            var pieces = new List<Piece>();
            var current = new Piece();
            foreach (ShellWordPartSyntax part in word.Parts)
                current = AddPart(part, false, current, pieces, false);
            return current;
        }

        private Piece AddPart(ShellWordPartSyntax part, bool quoted, Piece current, List<Piece> pieces, bool split)
        {
            switch (part)
            {
                case ShellLiteralWordPartSyntax literal:
                    string text = literal.Value;
                    if (!quoted && current.Text.Length == 0 && text.Length > 0 && text[0] == '~' && (text.Length == 1 || text[1] == '/'))
                        text = (System.Environment.GetEnvironmentVariable("HOME") ?? "") + text.Substring(1);
                    current.Add(text, false);
                    return current;
                case ShellEscapeSequenceSyntax escape:
                    current.Add(escape.Value, false);
                    current.Quoted = true;
                    return current;
                case ShellGlobSyntax glob:
                    // The parser keeps [...] as one token: `[$x]` still has
                    // its value to take.
                    string globText = glob.GlobToken.Text;
                    if (glob.IsBracketExpression && (globText.IndexOf('$') >= 0 || globText.IndexOf('`') >= 0))
                        globText = "[" + ExpandText(globText.Substring(1, globText.Length - 2)) + "]";
                    current.Add(globText, !quoted);
                    return current;
                case ShellQuotedStringSyntax inner:
                    current.Quoted = true;
                    foreach (ShellWordPartSyntax p in inner.Parts)
                        current = AddPart(p, true, current, pieces, split);
                    return current;
                case ShellVariableReferenceSyntax variable:
                    // "$@": each argument a field of its own.
                    if (quoted && split && variable.Name == "@")
                    {
                        for (int i = 0; i < _positional.Length; i++)
                        {
                            if (i > 0)
                            {
                                pieces.Add(current);
                                current = new Piece { Quoted = true };
                            }
                            current.Add(_positional[i], false);
                        }
                        return current;
                    }
                    return AddValue(variable.IsBraced ? Braced(variable.Name) : Lookup(variable.Name), quoted, current, pieces, split);
                case ShellCommandSubstitutionSyntax substitution:
                    return AddValue(Capture(substitution.Statements), quoted, current, pieces, split);
                case PosixArithmeticExpansionSyntax arithmetic:
                    return AddValue(Evaluate(arithmetic.Expression).ToString(), quoted, current, pieces, split);
                default:
                    throw new ShellError("this cannot be expanded: " + part.ToString(), 2);
            }
        }

        private static Piece AddValue(string value, bool quoted, Piece current, List<Piece> pieces, bool split)
        {
            if (quoted || !split)
            {
                current.Add(value, false);
                return current;
            }
            int at = 0;
            while (at < value.Length)
            {
                int start = at;
                while (at < value.Length && !IsBlank(value[at])) at++;
                if (at > start) AddActive(current, value.Substring(start, at - start));
                if (at == value.Length) break;
                while (at < value.Length && IsBlank(value[at])) at++;
                // The blanks end the field: at the end of the value too, what
                // follows in the word starts a new one.
                if (current.Quoted || current.Text.Length > 0)
                {
                    pieces.Add(current);
                    current = new Piece();
                }
            }
            return current;
        }

        private static bool IsBlank(char c) => c == ' ' || c == '\t' || c == '\n';

        // A value not quoted: its glob characters are active, as in sh.
        private static void AddActive(Piece piece, string value)
        {
            foreach (char c in value)
                piece.Add(c.ToString(), c == '*' || c == '?');
        }

        private string Capture(ShellStatementListSyntax statements)
        {
            if (!CanShow) throw new ShellError("$(...) needs threads, and this shell has none");
            var captured = new StringBuilder();
            StringBuilder outerCapture = _capture;
            bool outerCapturing = _capturing;
            System.IO.TextWriter outerWriter = _captureWriter;
            _capture = captured;
            _capturing = true;
            _captureWriter = null;
            try
            {
                LastExitCode = InScope(() => RunList(statements));
            }
            finally
            {
                _capture = outerCapture;
                _capturing = outerCapturing;
                _captureWriter = outerWriter;
            }
            _substituted = true;
            int end = captured.Length;
            while (end > 0 && captured[end - 1] == '\n') end--;
            return captured.ToString(0, end);
        }

        // A stage's output inside $(...): text, as the screen would have shown it.
        private static void ReadInto(PipeReadEnd end, StringBuilder into)
        {
            Decoder decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[4096];
            foreach (View v in end.Read())
            {
                if (v.TryGetBytes(out byte* bytes, out int count))
                {
                    int done = 0;
                    while (done < count)
                    {
                        int piece = count - done < 1024 ? count - done : 1024;
                        int n = decoder.GetChars(bytes + done, piece, RawChars(chars), chars.Length, false);
                        into.Append(chars, 0, n);
                        done += piece;
                    }
                    continue;
                }
                if (v.TryGetChars(out char* text, out int length))
                    into.Append(new string(new System.ReadOnlySpan<char>(text, length)));
                else
                    into.Append(v.IsNull ? "null" : v.ToString());
                into.Append('\n');
            }
        }

        private static char* RawChars(char[] chars)
        {
            fixed (char* p = chars) return p;   // the collector does not move objects
        }

        // ---- files ----

        private void Glob(Piece piece, List<string> into)
        {
            string text = piece.Text.ToString();
            string mask = piece.Mask.ToString();
            if (mask.IndexOf('g') < 0)
            {
                into.Add(text);
                return;
            }
            var found = new List<string>();
            bool absolute = text.Length > 0 && text[0] == '/';
            Walk(absolute ? "/" : "", text, mask, absolute ? 1 : 0, found);
            if (found.Count == 0)
            {
                // Nothing matches: the word as written, as in sh.
                into.Add(text);
                return;
            }
            found.Sort(string.CompareOrdinal);
            into.AddRange(found);
        }

        // From a path built so far, the part of the pattern from `at`.
        private void Walk(string built, string text, string mask, int at, List<string> found)
        {
            int slash = text.IndexOf('/', at);
            int end = slash < 0 ? text.Length : slash;
            string segment = text.Substring(at, end - at);
            string segmentMask = mask.Substring(at, end - at);
            bool last = slash < 0;

            if (segmentMask.IndexOf('g') < 0)
            {
                string next = built + segment;
                if (last)
                {
                    if (System.IO.File.Exists(Resolve(next)) || System.IO.Directory.Exists(Resolve(next))) found.Add(next);
                }
                else if (System.IO.Directory.Exists(Resolve(next.Length == 0 ? "/" : next)))
                {
                    Walk(next + "/", text, mask, slash + 1, found);
                }
                return;
            }

            string directory = Resolve(built.Length == 0 ? "." : built);
            if (!System.IO.Directory.Exists(directory)) return;
            var names = new List<string>();
            if (last)
                foreach (string f in System.IO.Directory.GetFiles(directory)) names.Add(LastName(f));
            foreach (string d in System.IO.Directory.GetDirectories(directory)) names.Add(LastName(d));
            bool hiddenToo = segment.Length > 0 && segment[0] == '.';
            foreach (string name in names)
            {
                if (name.Length > 0 && name[0] == '.' && !hiddenToo) continue;
                if (!Matches(new StringBuilder(segment), new StringBuilder(segmentMask), 0, name, 0, true)) continue;
                if (last) found.Add(built + name);
                else Walk(built + name + "/", text, mask, slash + 1, found);
            }
        }

        // A pattern against a name; a character is a pattern one only where the
        // mask says so. noCase: FAT names, which have no case.
        private static bool Matches(StringBuilder pattern, StringBuilder mask, int p, string name, int n, bool noCase)
        {
            while (p < pattern.Length)
            {
                char c = pattern[p];
                if (mask[p] == 'g')
                {
                    if (c == '*')
                    {
                        while (p < pattern.Length && pattern[p] == '*' && mask[p] == 'g') p++;
                        for (int rest = n; rest <= name.Length; rest++)
                            if (Matches(pattern, mask, p, name, rest, noCase)) return true;
                        return false;
                    }
                    if (n >= name.Length) return false;
                    if (c == '?')
                    {
                        p++;
                        n++;
                        continue;
                    }
                    if (c == '[')
                    {
                        // The token is one bracket expression, all of it masked.
                        int close = p + 1;
                        while (close < pattern.Length && mask[close] == 'g' && !(pattern[close] == ']' && close > p + 1)) close++;
                        if (close < pattern.Length && mask[close] == 'g')
                        {
                            if (!InSet(pattern, p + 1, close, name[n], noCase)) return false;
                            p = close + 1;
                            n++;
                            continue;
                        }
                    }
                }
                if (n >= name.Length || !SameChar(c, name[n], noCase)) return false;
                p++;
                n++;
            }
            return n == name.Length;
        }

        private static bool SameChar(char a, char b, bool noCase)
            => a == b || (noCase && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));

        private static bool InSet(StringBuilder pattern, int from, int to, char c, bool noCase)
        {
            bool negated = from < to && (pattern[from] == '!' || pattern[from] == '^');
            if (negated) from++;
            if (noCase) c = char.ToUpperInvariant(c);
            bool found = false;
            for (int i = from; i < to; i++)
            {
                char low = pattern[i], high = low;
                if (i + 2 < to && pattern[i + 1] == '-')
                {
                    high = pattern[i + 2];
                    i += 2;
                }
                if (noCase)
                {
                    low = char.ToUpperInvariant(low);
                    high = char.ToUpperInvariant(high);
                }
                if (c >= low && c <= high) found = true;
            }
            return found != negated;
        }

        // ---- arithmetic ----

        private long Evaluate(ShellExpressionSyntax expression)
        {
            switch (expression)
            {
                case ShellGroupedExpressionSyntax grouped: return Evaluate(grouped.Expression);
                case ShellConditionalExpressionSyntax conditional:
                    return Evaluate(Evaluate(conditional.Condition) != 0 ? conditional.WhenTrue : conditional.WhenFalse);
                case ShellOperandExpressionSyntax operand: return Number(ExpandOne(operand.Word));
                case ShellUnaryExpressionSyntax unary: return EvaluateUnary(unary);
                case ShellBinaryExpressionSyntax binary: return EvaluateBinary(binary);
                default: throw new ShellError("this arithmetic cannot run: " + expression.ToString(), 2);
            }
        }

        // A number, or the name of a variable that holds one; empty is 0.
        private long Number(string text)
        {
            text = text.Trim();
            if (text.Length == 0) return 0;
            if (IsName(text)) return Number(Lookup(text));
            bool negative = text[0] == '-';
            string digits = negative || text[0] == '+' ? text.Substring(1) : text;
            long value = 0;
            int radix = digits.StartsWith("0x") || digits.StartsWith("0X") ? 16 : digits.Length > 1 && digits[0] == '0' ? 8 : 10;
            for (int i = radix == 16 ? 2 : radix == 8 ? 1 : 0; i < digits.Length; i++)
            {
                char c = char.ToLowerInvariant(digits[i]);
                int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : 99;
                if (digit >= radix) throw new ShellError("not a number: " + text);
                value = value * radix + digit;
            }
            if (digits.Length == 0) throw new ShellError("not a number: " + text);
            return negative ? -value : value;
        }

        private static string NameOf(ShellExpressionSyntax target)
        {
            if (target is ShellOperandExpressionSyntax operand)
            {
                string name = operand.Word.Value;
                if (name != null && IsName(name)) return name;
            }
            throw new ShellError("this cannot be assigned to: " + target.ToString(), 2);
        }

        private long EvaluateUnary(ShellUnaryExpressionSyntax unary)
        {
            string prefix = unary.PrefixOperatorToken.IsPresent() ? unary.PrefixOperatorToken.Text : null;
            string postfix = unary.PostfixOperatorToken.IsPresent() ? unary.PostfixOperatorToken.Text : null;
            if (prefix == "++" || prefix == "--" || postfix == "++" || postfix == "--")
            {
                string name = NameOf(unary.Operand);
                long before = Number(Lookup(name));
                long after = (prefix ?? postfix) == "++" ? before + 1 : before - 1;
                Assign(name, after.ToString());
                return prefix != null ? after : before;
            }
            long value = Evaluate(unary.Operand);
            switch (prefix)
            {
                case "-": return -value;
                case "+": return value;
                case "!": return value == 0 ? 1 : 0;
                case "~": return ~value;
                default: throw new ShellError("this arithmetic cannot run: " + unary.ToString(), 2);
            }
        }

        private long EvaluateBinary(ShellBinaryExpressionSyntax binary)
        {
            string op = binary.OperatorToken.Text;
            if (op == "&&") return Evaluate(binary.Left) != 0 && Evaluate(binary.Right) != 0 ? 1 : 0;
            if (op == "||") return Evaluate(binary.Left) != 0 || Evaluate(binary.Right) != 0 ? 1 : 0;
            if (op == ",")
            {
                Evaluate(binary.Left);
                return Evaluate(binary.Right);
            }
            if (op == "=")
            {
                long assigned = Evaluate(binary.Right);
                Assign(NameOf(binary.Left), assigned.ToString());
                return assigned;
            }
            if (op.Length >= 2 && op[op.Length - 1] == '=' && op != "==" && op != "!=" && op != "<=" && op != ">=")
            {
                string name = NameOf(binary.Left);
                long result = Apply(op.Substring(0, op.Length - 1), Number(Lookup(name)), Evaluate(binary.Right));
                Assign(name, result.ToString());
                return result;
            }
            return Apply(op, Evaluate(binary.Left), Evaluate(binary.Right));
        }

        private static long Apply(string op, long a, long b)
        {
            switch (op)
            {
                case "+": return a + b;
                case "-": return a - b;
                case "*": return a * b;
                case "/":
                    if (b == 0) throw new ShellError("division by zero");
                    return a / b;
                case "%":
                    if (b == 0) throw new ShellError("division by zero");
                    return a % b;
                case "**":
                    if (b < 0) throw new ShellError("a negative power");
                    long power = 1;
                    for (long i = 0; i < b; i++) power *= a;
                    return power;
                case "<<": return a << (int)b;
                case ">>": return a >> (int)b;
                case "<": return a < b ? 1 : 0;
                case "<=": return a <= b ? 1 : 0;
                case ">": return a > b ? 1 : 0;
                case ">=": return a >= b ? 1 : 0;
                case "==": return a == b ? 1 : 0;
                case "!=": return a != b ? 1 : 0;
                case "&": return a & b;
                case "|": return a | b;
                case "^": return a ^ b;
                default: throw new ShellError("this arithmetic cannot run: " + op, 2);
            }
        }

        // ---- test, [, break, continue ----

        private int Test(List<string> args)
        {
            try
            {
                int at = 0;
                bool result = args.Count > 0 && TestOr(args, ref at);
                if (at < args.Count) throw new ShellError("test: too many arguments: " + args[at], 2);
                return result ? 0 : 1;
            }
            catch (ShellError e) when (e.Code == 2)
            {
                Write("sh: ");
                Write(e.Message + "\n");
                return 2;
            }
        }

        private int Bracket(List<string> args)
        {
            if (args.Count == 0 || args[args.Count - 1] != "]")
            {
                Write("sh: ");
                Write("[: the closing ] is missing\n");
                return 2;
            }
            return Test(args.GetRange(0, args.Count - 1));
        }

        private bool TestOr(List<string> a, ref int at)
        {
            bool value = TestAnd(a, ref at);
            while (at < a.Count && a[at] == "-o")
            {
                at++;
                bool right = TestAnd(a, ref at);
                value = value || right;
            }
            return value;
        }

        private bool TestAnd(List<string> a, ref int at)
        {
            bool value = TestPrimary(a, ref at);
            while (at < a.Count && a[at] == "-a")
            {
                at++;
                bool right = TestPrimary(a, ref at);
                value = value && right;
            }
            return value;
        }

        private bool TestPrimary(List<string> a, ref int at)
        {
            if (at >= a.Count) throw new ShellError("test: an argument is missing", 2);
            string first = a[at];
            if (first == "!" && at + 1 < a.Count)
            {
                at++;
                return !TestPrimary(a, ref at);
            }
            if (first == "(" )
            {
                at++;
                bool inner = TestOr(a, ref at);
                if (at >= a.Count || a[at] != ")") throw new ShellError("test: the closing ) is missing", 2);
                at++;
                return inner;
            }
            // A binary operator wins over reading the first word as an operator.
            if (at + 2 < a.Count && IsBinaryTest(a[at + 1]))
            {
                string left = first, op = a[at + 1], right = a[at + 2];
                at += 3;
                switch (op)
                {
                    case "=":
                    case "==": return left == right;
                    case "!=": return left != right;
                    case "-eq": return TestNumber(left) == TestNumber(right);
                    case "-ne": return TestNumber(left) != TestNumber(right);
                    case "-lt": return TestNumber(left) < TestNumber(right);
                    case "-le": return TestNumber(left) <= TestNumber(right);
                    case "-gt": return TestNumber(left) > TestNumber(right);
                    default: return TestNumber(left) >= TestNumber(right);
                }
            }
            if (first.Length == 2 && first[0] == '-' && at + 1 < a.Count)
            {
                string operand = a[at + 1];
                switch (first[1])
                {
                    case 'n': at += 2; return operand.Length > 0;
                    case 'z': at += 2; return operand.Length == 0;
                    case 'e': at += 2; return ExistsAtAll(operand);
                    case 'r': at += 2; return ExistsAtAll(operand);
                    case 'w': at += 2; return ExistsAtAll(operand);
                    case 'x': at += 2; return ExistsAtAll(operand);
                    case 'f': at += 2; return System.IO.File.Exists(Resolve(operand));
                    case 'd': at += 2; return System.IO.Directory.Exists(Resolve(operand));
                    case 's':
                        at += 2;
                        return System.IO.File.Exists(Resolve(operand)) && new System.IO.FileInfo(Resolve(operand)).Length > 0;
                }
            }
            at++;
            return first.Length > 0;
        }

        private bool ExistsAtAll(string path)
            => System.IO.File.Exists(Resolve(path)) || System.IO.Directory.Exists(Resolve(path));

        private static bool IsBinaryTest(string op)
            => op == "=" || op == "==" || op == "!=" || op == "-eq" || op == "-ne" || op == "-lt" || op == "-le" || op == "-gt" || op == "-ge";

        private static long TestNumber(string text)
        {
            if (long.TryParse(text.Trim(), out long value)) return value;
            throw new ShellError("test: not a number: " + text, 2);
        }

        private int BreakOrContinue(List<string> args, bool isBreak)
        {
            int levels = 1;
            if (args.Count > 0 && (!TryParseInt(args[0], out levels) || levels < 1))
            {
                Write("sh: ");
                Write((isBreak ? "break" : "continue") + ": not a positive number: " + args[0] + "\n");
                return 2;
            }
            if (isBreak) _breaks = levels;
            else _continues = levels;
            return 0;
        }
    }
}
