using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Text.Json.Nodes;
using System.Threading;
using BabyKusto.Core;
using BabyKusto.Core.Evaluation;
using Kusto.Language;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using SharpOS.AppSdk;
using SharpOS.Std.Exchange;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // KQL 'query' — a pipeline stage in KQL (step200; streaming since step201):
    // the messages on the standard input are the table Input, and the query
    // (BabyKusto) runs over them as they arrive.
    //
    //   PIPEGEN 100 | KQL 'where Level >= 3 | summarize n = count() by Level'
    //
    // A query that starts with an operator applies to Input; one that names
    // Input itself, or starts with print, let, datatable or range, is taken as
    // it is.
    //
    // The columns are the type the writer declared (step205: waited for, not
    // guessed from the first message), and the query is bound to it before
    // any data is read: integers and enums long, float and double real, bool,
    // string and char string, DateTime datetime, TimeSpan timespan, Guid and
    // decimal their text, anything else (objects, arrays, structs) dynamic. A
    // message that is not an object of fields — an Expando, an array, a
    // string, a number — is the column Value. Over Expandos the table is open:
    // every name the query reads is a dynamic column of its own (`where Level
    // == 4`), looked up in each message, of each message's own kind, null
    // where a message lacks it; `Value.Name` reads the same entry.
    //
    // The input is read in chunks of what has arrived (up to 256 messages,
    // never waiting for more than one); operators that need everything (sort,
    // summarize) wait for the end, `where` answers as messages come, `take`
    // stops reading. A query of where, take and sort alone sends the input's
    // own blocks on — no copy, no translation, the input's type — found by a
    // hidden row number. Over Expandos, extend does too: its columns, when
    // they are numbers, bools or strings, are written into each message in
    // place (ExpandoEdits), and the message keeps everything else it held.
    // Over a type of fields (step204–205), extend that assigns existing
    // fields writes them into the message: numbers, bools, DateTime and
    // TimeSpan where they lie, a string as a new record in the block's tail
    // with the field pointing at it; the same block goes on, of the same type;
    // a value that does not fit the field is an error. A tail of project,
    // project-away and project-rename by name, over Expandos, is done to each
    // block too — entries kept, removed, renamed where they are, their order
    // unchanged (step205). Any other result goes out as Expando messages —
    // over Expandos, the entries of Value with the other columns beside them.
    //
    // A query that fails while its output is open breaks the output: the next
    // stage sees the stream broken, not ended (step205).
    //
    // Exit code: 0; 1 for a query that fails on the data (the message says
    // where); 2 for usage and for a query that does not bind to the input's
    // type — before any data is read; 141 when the next stage left. One line
    // `[kql] start ms` in the log: the declared type, the bindings, the first
    // row out.
    internal static unsafe class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run(StressArgs.Apply(AppHost.Arguments));
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run(AppHost.Arguments);

        private const string TableName = "Input";
        private const string RowColumn = "__row";
        private const string WholeColumn = "Value";
        private const int ChunkRows = 256;

        // The start, by phase (step205): to the writer's declared type, the
        // first binding (over Expandos), the second, the first row out. One
        // line in the log per run — what a query costs before its data.
        private static long s_start, s_declared, s_firstBound, s_bound;
        private static bool s_reported;

        private static void ReportStart()
        {
            if (s_reported || s_start == 0) return;
            s_reported = true;
            long now = Stopwatch.GetTimestamp();
            AppHost.WriteString("[kql] start ms: declared=" + Ms(s_start, s_declared) + " bind1=" + Ms(s_declared, s_firstBound)
                                + " bind2=" + Ms(s_firstBound, s_bound) + " first=" + Ms(s_bound, now) + " total=" + Ms(s_start, now) + "\n");
        }

        private static string Ms(long from, long to)
            => to <= from ? "0" : ((to - from) * 1000 / Stopwatch.Frequency).ToString();

        private static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("KQL 'query': runs the query over the messages on the input (the table " + TableName + ")");
                Console.WriteLine("  PIPEGEN 100 | KQL 'where Level >= 3 | summarize n = count() by Level'");
                return 2;
            }
            string query = Query(string.Join(" ", args));
            try
            {
                Plan plan = Plan.Of(query);
                return plan.ReadsInput ? OverInput(query, plan) : Alone(query);
            }
            catch (PipeException e) when (e.IsStandardEndBroken)
            {
                // The next stage left, or the one before died: quiet, as on SIGPIPE.
                return 141;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A type the app has no name for prints as "EETypeRva:0x…":
                // the message says what happened without it.
                string type = e.GetType().Name;
                bool named = e is not PipeException && !type.StartsWith("EETypeRva");
                Console.WriteLine("KQL: " + (named ? type + ": " : "") + e.Message);
                return 1;
            }
        }

        private static int OverInput(string query, Plan plan)
        {
            s_start = Stopwatch.GetTimestamp();
            RawPipeReader input = Pipe.Read();
            RawRegion first = null;
            PipeTable source = null;
            try
            {
                // The type the writer declared, before any message (step205):
                // the query is bound to it, and a wrong one fails before a
                // byte of data is read — on an empty input too.
                PipeTransport.WaitSchema(input.Handle);
                Columns columns = Columns.Declared(input);
                if (columns.Sources.Length == 0)
                {
                    // A writer that never declared a type, or an older kernel:
                    // the first message is all there is to go by.
                    first = input.Next();
                    if (first != null) columns = Columns.Of(first.Root);
                }
                s_declared = Stopwatch.GetTimestamp();
                s_firstBound = s_declared;

                // Over Expandos the table is open: the first binding finds the
                // entries the query names, which become columns of their own
                // — dynamic, each message's value of its own kind.
                if (columns.Bag)
                {
                    if (!Analyze(query, columns.Symbol().WithIsOpen(true), out KustoCode open, null)) return 2;
                    columns.Name(InputNames(open));
                    s_firstBound = Stopwatch.GetTimestamp();
                }

                // A tail of project / project-away / project-rename by name,
                // over Expandos: done to each message's block here, the rest
                // of the query by BabyKusto.
                TailEdits tail = columns.Bag ? TailEdits.Of(query) : null;
                string run = query;
                Plan runPlan = plan;
                if (tail != null)
                {
                    Plan rest = Plan.Of(tail.Rest);
                    if (rest.Forward || rest.Extend) { run = tail.Rest; runPlan = rest; }
                    else tail = null;
                }

                columns.Row = runPlan.Forward || (runPlan.Extend && (columns.Bag || columns.Typed));
                TableSymbol schema = columns.Symbol();
                if (!Analyze(query, schema, out KustoCode code,
                             columns.Sources.Length == 0 ? "the input is empty and declares no type" : null))
                    return 2;
                KustoCode runCode = code;
                if (!ReferenceEquals(run, query) && !Analyze(run, schema, out runCode, null)) return 2;
                s_bound = Stopwatch.GetTimestamp();

                // The input's blocks go on as they are, or with the new
                // columns written into each message; else new messages.
                Addition[] added = null;
                FieldEdit[] edits = null;
                bool forward = false;
                if (columns.Row && runCode.ResultType is TableSymbol result)
                {
                    if (runPlan.Forward && SameColumns(result, schema)) forward = true;
                    else if (runPlan.Extend && columns.Bag && Additions(result, schema, out added)) forward = true;
                    else if (runPlan.Extend && columns.Typed && Edits(result, schema, columns, runPlan, out edits)) forward = true;
                }
                if (!forward && tail != null)
                {
                    // The tail cannot be done in place after all: the whole
                    // query, new messages.
                    run = query;
                    tail = null;
                }
                source = new PipeTable(schema, columns, input, first, keep: forward, release: runPlan.Streaming);
                first = null;

                var engine = new BabyKustoEngine();
                engine.AddGlobalTable(source);
                EvaluationResult evaluated = engine.Evaluate(run);
                return forward ? Forward(evaluated, source, input, added, edits, tail) : Materialize(evaluated, columns);
            }
            finally
            {
                first?.Dispose();
                source?.Close();
                input.Dispose();
            }
        }

        // A query with no table of its own (print, datatable, range): the input is not read.
        private static int Alone(string query)
        {
            if (!Analyze(query, null, out _, null)) return 2;
            return Materialize(new BabyKustoEngine().Evaluate(query), null);
        }

        private static bool Analyze(string query, TableSymbol schema, out KustoCode code, string hint)
        {
            GlobalState globals = schema == null ? GlobalState.Default
                                                 : GlobalState.Default.WithDatabase(new DatabaseSymbol("db", schema));
            code = KustoCode.ParseAndAnalyze(query, globals);
            IReadOnlyList<Diagnostic> found = code.GetDiagnostics();
            if (found.Count == 0) return true;
            // The first one: the rest usually follow from it.
            Console.WriteLine("KQL: " + Where(query, found[0].Start) + ": " + found[0].Message);
            if (hint != null) Console.WriteLine("KQL: " + hint);
            return false;
        }

        // "where …" is a query over Input; "Input | …", "print …", "let …" are queries already.
        private static string Query(string text)
        {
            string t = text.TrimStart();
            if (t.StartsWith("|")) t = t.Substring(1).TrimStart();
            int end = 0;
            while (end < t.Length && (char.IsLetterOrDigit(t[end]) || t[end] == '_' || t[end] == '-')) end++;
            string word = t.Substring(0, end);
            if (word == TableName || word == "print" || word == "let" || word == "datatable" || word == "range")
                return t;
            return TableName + "\n| " + t;
        }

        // A position in the query as the user wrote it (without the Input prefix).
        private static string Where(string query, int start)
        {
            int prefix = query.StartsWith(TableName + "\n| ") ? TableName.Length + 3 : 0;
            int at = start - prefix + 1;
            return "at " + (at > 0 ? at : 1).ToString();
        }

        private static bool SameColumns(TableSymbol a, TableSymbol b)
        {
            if (a.Columns.Count != b.Columns.Count) return false;
            for (int i = 0; i < a.Columns.Count; i++)
                if (a.Columns[i].Name != b.Columns[i].Name || a.Columns[i].Type != b.Columns[i].Type) return false;
            return true;
        }

        private static int IndexOf(TableSymbol t, string name)
        {
            for (int i = 0; i < t.Columns.Count; i++)
                if (t.Columns[i].Name == name) return i;
            return -1;
        }

        // A column extend added, written into each message as an entry.
        private struct Addition
        {
            public int Index;
            public ExpandoName Name;
        }

        // extend over Expandos: Value and the row number as they came, every
        // other column of a type an Expando entry takes in place — a number,
        // bool or string (ExpandoEdits). A datetime, a timespan or a dynamic
        // would be an object in the block: those results are new messages.
        private static bool Additions(TableSymbol result, TableSymbol schema, out Addition[] added)
        {
            added = null;
            var list = new List<Addition>();
            bool value = false, row = false;
            for (int i = 0; i < result.Columns.Count; i++)
            {
                ColumnSymbol c = result.Columns[i];
                if (c.Name == WholeColumn && c.Type == ScalarTypes.Dynamic) { value = true; continue; }
                if (c.Name == RowColumn && c.Type == ScalarTypes.Long) { row = true; continue; }
                // An entry the query named, as it came: already in the message.
                int input = IndexOf(schema, c.Name);
                if (input >= 0 && schema.Columns[input].Type == c.Type) continue;
                if (c.Type != ScalarTypes.Long && c.Type != ScalarTypes.Int && c.Type != ScalarTypes.Real
                    && c.Type != ScalarTypes.Bool && c.Type != ScalarTypes.String) return false;
                list.Add(new Addition { Index = i, Name = new ExpandoName(c.Name) });
            }
            if (!value || !row) return false;
            added = list.ToArray();
            return true;
        }

        // A field extend assigns, written into the message where it lies.
        private struct FieldEdit
        {
            public int Index;
            public string Name;
        }

        // extend over a type of fields: the result has exactly the input's
        // columns (extend only reassigned some), and every column it assigned
        // is a field that holds a number or a bool — the kinds a view writes
        // in place. A string, a char (a string in KQL), a struct or a
        // reference would need room or an object: those results are new
        // messages.
        private static bool Edits(TableSymbol result, TableSymbol schema, Columns columns, Plan plan, out FieldEdit[] edits)
        {
            edits = null;
            if (plan.Extended.Count == 0 || !SameColumns(result, schema)) return false;
            var list = new List<FieldEdit>();
            foreach (string name in plan.Extended)
            {
                Source s = columns.Find(name);
                if (s == null || s.Field == null) return false;
                FieldKind kind = s.Field.Kind;
                // A string goes into the block's tail and the field points at
                // it; a DateTime or TimeSpan is one 64-bit field (step205).
                bool text = kind == FieldKind.Reference && s.Field.TypeName == "System.String";
                bool time = kind == FieldKind.Struct && (s.Field.TypeName == "System.DateTime" || s.Field.TypeName == "System.TimeSpan");
                if ((kind == FieldKind.Reference && !text) || (kind == FieldKind.Struct && !time) || kind == FieldKind.Char) return false;
                bool seen = false;
                foreach (FieldEdit e in list) if (e.Name == name) seen = true;
                if (!seen) list.Add(new FieldEdit { Index = IndexOf(result, name), Name = name });
            }
            edits = list.ToArray();
            return true;
        }

        // The input's names a query reads, from a binding over an open table:
        // references to columns the binder inferred (type unknown), not to
        // ones the query declares itself.
        private static List<string> InputNames(KustoCode open)
        {
            var declared = new List<Symbol>();
            open.Syntax.WalkElements(e =>
            {
                if (e is NameDeclaration d && d.ReferencedSymbol != null) declared.Add(d.ReferencedSymbol);
            });
            var names = new List<string>();
            open.Syntax.WalkElements(e =>
            {
                if (e is not NameReference n || n.ReferencedSymbol is not ColumnSymbol c || c.Type != ScalarTypes.Unknown) return;
                foreach (Symbol d in declared) if (ReferenceEquals(d, c)) return;
                if (!names.Contains(n.SimpleName)) names.Add(n.SimpleName);
            });
            return names;
        }

        // project, project-away and project-rename of plain names at the end
        // of a query over Expandos (step205): done to the message's own block
        // — entries removed, kept, renamed where they are — so it goes on by
        // the same address. The query before them is BabyKusto's.
        private sealed class TailEdits
        {
            public string Rest;
            private readonly List<TailEdit> _edits = new List<TailEdit>();

            private enum Op : byte { Away, Keep, Rename }

            private sealed class TailEdit
            {
                public Op Op;
                public ExpandoName[] Names;
                public ExpandoName[] From;
            }

            public static TailEdits Of(string query)
            {
                PipeExpression root = null;
                KustoCode.Parse(query).Syntax.WalkElements(e =>
                {
                    if (root == null && e is PipeExpression p) root = p;
                });
                if (root == null) return null;
                var pipes = new List<PipeExpression>();
                for (Expression x = root; x is PipeExpression p; x = p.Expression) pipes.Insert(0, p);
                var edits = new List<TailEdit>();
                int first = pipes.Count;
                for (int i = pipes.Count - 1; i >= 0; i--)
                {
                    TailEdit edit = EditOf(pipes[i].Operator);
                    if (edit == null) break;
                    edits.Insert(0, edit);
                    first = i;
                }
                if (edits.Count == 0) return null;
                var tail = new TailEdits { Rest = query.Substring(0, pipes[first].Expression.End) };
                tail._edits.AddRange(edits);
                return tail;
            }

            private static TailEdit EditOf(QueryOperator op)
            {
                SyntaxList<SeparatedElement<Expression>> list =
                    op is ProjectAwayOperator away ? away.Expressions
                    : op is ProjectOperator project ? project.Expressions
                    : op is ProjectRenameOperator rename ? rename.Expressions
                    : null;
                if (list == null || list.Count == 0) return null;
                var names = new ExpandoName[list.Count];
                var from = op is ProjectRenameOperator ? new ExpandoName[list.Count] : null;
                for (int i = 0; i < list.Count; i++)
                {
                    Expression x = list[i].Element;
                    string name, source = null;
                    if (from == null && x is NameReference plain) name = plain.SimpleName;
                    else if (from != null && x is SimpleNamedExpression named && named.Expression is NameReference old)
                    {
                        name = named.Name.SimpleName;
                        source = old.SimpleName;
                    }
                    else return null;
                    if (name == WholeColumn || name == RowColumn || source == WholeColumn || source == RowColumn) return null;
                    names[i] = new ExpandoName(name);
                    if (from != null) from[i] = new ExpandoName(source);
                }
                return new TailEdit
                {
                    Op = op is ProjectAwayOperator ? Op.Away : op is ProjectOperator ? Op.Keep : Op.Rename,
                    Names = names,
                    From = from,
                };
            }

            // An edit may replace the block: the root is taken anew each time.
            public void Apply(RawRegion region)
            {
                foreach (TailEdit edit in _edits)
                {
                    switch (edit.Op)
                    {
                        case Op.Away:
                            foreach (ExpandoName n in edit.Names) region.Remove(region.Root, n);
                            break;
                        case Op.Keep:
                            region.KeepOnly(region.Root, edit.Names);
                            break;
                        default:
                            for (int i = 0; i < edit.Names.Length; i++) region.Rename(region.Root, edit.From[i], edit.Names[i]);
                            break;
                    }
                }
            }
        }

        // What the query does, read from its syntax before the input's type is known.
        private sealed class Plan
        {
            // It names Input: the input is read.
            public bool ReadsInput;
            // where, take and sort only, and no `*` (which would see the
            // hidden row number): the result rows are input messages.
            public bool Forward = true;
            // ... or extend too: over Expandos, the result rows are input
            // messages with entries added in place.
            public bool Extend = true;
            // where, take and extend only: a chunk's rows are done with once the next chunk is asked for.
            public bool Streaming = true;
            // The names extend assigns.
            public readonly List<string> Extended = new List<string>();

            public static Plan Of(string query)
            {
                var plan = new Plan();
                KustoCode.Parse(query).Syntax.WalkElements(e =>
                {
                    // Collected on the side: extend still goes through the
                    // operator rules below.
                    if (e is ExtendOperator extend)
                        foreach (SeparatedElement<Expression> x in extend.Expressions)
                            if (x.Element is SimpleNamedExpression named) plan.Extended.Add(named.Name.SimpleName);

                    if (e is NameReference name && name.SimpleName == TableName) plan.ReadsInput = true;
                    else if (e is StarExpression) plan.Forward = plan.Extend = false;
                    // A column of the input named again: Value or the row number would not be the message's.
                    else if (e is NameDeclaration declared && (declared.SimpleName == WholeColumn || declared.SimpleName == RowColumn))
                        plan.Forward = plan.Extend = false;
                    else if (e is QueryOperator && e is not FilterOperator && e is not TakeOperator)
                    {
                        if (e is not ExtendOperator) plan.Streaming = false;
                        if (e is not SortOperator) plan.Forward = false;
                        if (e is not SortOperator && e is not ExtendOperator) plan.Extend = false;
                    }
                });
                return plan;
            }
        }

        // ---- out: the input's own blocks ----

        // Each result row's message goes on as its block: as it came, or with
        // the columns extend added written into its Expando (ExpandoEdits — in
        // the block's room, or in a block that replaces it).
        private static int Forward(EvaluationResult evaluated, PipeTable source, RawPipeReader input, Addition[] added, FieldEdit[] edits,
                                   TailEdits tail)
        {
            if (evaluated is not TabularResult table) return 0;
            int row = IndexOf(table.Value.Type, RowColumn);
            int output = Pipe.TakeOutput();
            bool screen = output == 0;
            bool declared = false;
            try
            {
                foreach (ITableChunk chunk in table.Value.GetData())
                {
                    Array ids = chunk.Columns[row].RawData;
                    for (int r = 0; r < chunk.RowCount; r++)
                    {
                        RawRegion region = source.Take((long)ids.GetValue(r));
                        if (region == null) continue;
                        if (added != null || edits != null)
                        {
                            try
                            {
                                if (added != null)
                                    for (int a = 0; a < added.Length; a++)
                                        Assign(region, added[a].Name, chunk.Columns[added[a].Index], r);
                                if (edits != null)
                                    for (int a = 0; a < edits.Length; a++)
                                        Write(region, edits[a].Name, chunk.Columns[edits[a].Index], r);
                            }
                            catch
                            {
                                region.Dispose();
                                throw;
                            }
                        }
                        if (tail != null)
                        {
                            try { tail.Apply(region); }
                            catch
                            {
                                region.Dispose();
                                throw;
                            }
                        }
                        ReportStart();
                        if (screen)
                        {
                            try { ScreenText.Print(region.Root); }
                            finally { region.Dispose(); }
                            continue;
                        }
                        if (!declared)
                        {
                            PipeStatus opened = PipeTransport.OpenEnd(output, input.Schema, input.RootKey, out string error);
                            if (opened != PipeStatus.Ok)
                            {
                                region.Dispose();
                                Console.WriteLine("KQL: the output: " + (error ?? Pipe.Explain(opened)));
                                return 1;
                            }
                            declared = true;
                        }
                        PipeStatus sent = PipeTransport.Send(output, region.Block, region.Length);
                        if (sent != PipeStatus.Ok)
                        {
                            region.Dispose();
                            if (sent == PipeStatus.Broken) return 141;
                            Console.WriteLine("KQL: the output: " + Pipe.Explain(sent));
                            return 1;
                        }
                        region.End("the region went on to the standard output");
                    }
                }
                ReportStart();
                // Nothing passed: the output still carries the input's type, and its end.
                if (!declared && !screen)
                {
                    byte[] schema = input.Schema ?? PipeTransport.Schema(input.Handle, out _);
                    ulong rootKey = input.RootKey;
                    if (schema != null && rootKey == 0) PipeTransport.Schema(input.Handle, out rootKey);
                    if (schema != null) PipeTransport.OpenEnd(output, schema, rootKey, out _);
                }
                return 0;
            }
            finally
            {
                // A query that fails halfway breaks the output: the next stage
                // must not take what it got for the whole answer.
                if (output != 0) PipeTransport.End(output);
            }
        }

        // A result cell into the message's Expando, in place. An edit may
        // replace the block, and with it every view: the root is taken anew.
        private static void Assign(RawRegion region, ExpandoName name, Column column, int r)
        {
            TypeSymbol type = column.Type;
            if (type == ScalarTypes.Long)
            {
                long? v = ((Column<long?>)column)[r];
                if (v.HasValue) region.Set(region.Root, name, v.Value);
                else region.Set(region.Root, name, (object)null);
            }
            else if (type == ScalarTypes.Int)
            {
                int? v = ((Column<int?>)column)[r];
                if (v.HasValue) region.Set(region.Root, name, v.Value);
                else region.Set(region.Root, name, (object)null);
            }
            else if (type == ScalarTypes.Real)
            {
                double? v = ((Column<double?>)column)[r];
                if (v.HasValue) region.Set(region.Root, name, v.Value);
                else region.Set(region.Root, name, (object)null);
            }
            else if (type == ScalarTypes.Bool)
            {
                bool? v = ((Column<bool?>)column)[r];
                if (v.HasValue) region.Set(region.Root, name, v.Value);
                else region.Set(region.Root, name, (object)null);
            }
            else
            {
                region.Set(region.Root, name, (object)((Column<string>)column)[r]);
            }
        }

        // A result cell into the message's own field, where it lies. The view
        // checks the kind and the range: a value the field cannot hold is an
        // error, not a quiet truncation.
        private static void Write(RawRegion region, string name, Column column, int r)
        {
            View root = region.Root;
            TypeSymbol type = column.Type;
            if (type == ScalarTypes.Long)
            {
                long? v = ((Column<long?>)column)[r];
                if (!v.HasValue) throw NullInto(name);
                root[name] = v.Value;
            }
            else if (type == ScalarTypes.Int)
            {
                int? v = ((Column<int?>)column)[r];
                if (!v.HasValue) throw NullInto(name);
                root[name] = v.Value;
            }
            else if (type == ScalarTypes.Real)
            {
                double? v = ((Column<double?>)column)[r];
                if (!v.HasValue) throw NullInto(name);
                root[name] = v.Value;
            }
            else if (type == ScalarTypes.Bool)
            {
                bool? v = ((Column<bool?>)column)[r];
                if (!v.HasValue) throw NullInto(name);
                root[name] = v.Value;
            }
            else if (type == ScalarTypes.String)
            {
                region.SetText(region.Root, name, ((Column<string>)column)[r]);
            }
            else if (type == ScalarTypes.DateTime)
            {
                DateTime? v = ((Column<DateTime?>)column)[r];
                if (!v.HasValue) throw NullInto(name);
                // The ticks; the kind the field had (its top two bits) kept.
                WriteTicks(root[name], (ulong)v.Value.Ticks, keepFlags: true);
            }
            else if (type == ScalarTypes.TimeSpan)
            {
                TimeSpan? v = ((Column<TimeSpan?>)column)[r];
                if (!v.HasValue) throw NullInto(name);
                WriteTicks(root[name], (ulong)v.Value.Ticks, keepFlags: false);
            }
            else
            {
                throw new InvalidOperationException("'" + name + "' cannot be written where it lies");
            }
        }

        // A DateTime or TimeSpan in place: a struct of one 64-bit field.
        private static void WriteTicks(View value, ulong ticks, bool keepFlags)
        {
            FieldShape[] fields = value.Shape?.Fields;
            if (fields == null || fields.Length != 1)
                throw new InvalidOperationException("'" + value.TypeName + "' is not one 64-bit field: not written in place");
            string inner = fields[0].Name;
            if (keepFlags)
            {
                const ulong Flags = 0xC000000000000000;
                ticks = (ticks & ~Flags) | ((ulong)value[inner] & Flags);
            }
            if (fields[0].Kind == FieldKind.UInt64) value[inner] = ticks;
            else value[inner] = (long)ticks;
        }

        private static Exception NullInto(string name)
            => new InvalidOperationException("'" + name + "' holds a value: the query made it null");

        // ---- out: new messages ----

        // Over Expandos (a bag), a row's Value is the message: its entries come
        // first, the other columns after them, and a column of an entry's name
        // takes its place — as extend's edits in place do.
        private static int Materialize(EvaluationResult evaluated, Columns input)
        {
            using PipeWriter<Expando> output = Pipe.Write<Expando>();
            if (evaluated is TabularResult table)
            {
                var names = new List<string>();
                foreach (ColumnSymbol c in table.Value.Type.Columns) names.Add(c.Name);
                bool bag = input != null && input.Bag;
                int whole = bag ? IndexOf(table.Value.Type, WholeColumn) : -1;
                // An entry the query named and left as it was is in Value
                // already, of its own type; its dynamic copy is not taken.
                var skip = new bool[names.Count];
                for (int c = 0; c < names.Count; c++)
                    skip[c] = c == whole || names[c] == RowColumn
                              || (whole >= 0 && input.IsEntry(names[c], table.Value.Type.Columns[c].Type));
                foreach (ITableChunk chunk in table.Value.GetData())
                {
                    for (int r = 0; r < chunk.RowCount; r++)
                    {
                        var e = new Expando();
                        if (whole >= 0 && chunk.Columns[whole].RawData.GetValue(r) is JsonObject entries)
                            foreach (KeyValuePair<string, JsonNode> p in entries) e[p.Key] = Out(p.Value);
                        else if (whole >= 0)
                            e[WholeColumn] = Out(chunk.Columns[whole].RawData.GetValue(r));
                        for (int c = 0; c < names.Count; c++)
                            if (!skip[c]) e[names[c]] = Out(chunk.Columns[c].RawData.GetValue(r));
                        ReportStart();
                        output.Copy(e);
                    }
                }
                ReportStart();
            }
            else if (evaluated is ScalarResult scalar)
            {
                var e = new Expando();
                e.Add("value", Out(scalar.Value));
                output.Copy(e);
            }
            return 0;
        }

        // What a result cell becomes in a message: the types a pipe carries as
        // they are; dynamic as an Expando, an array, or its scalar; anything
        // else as its text.
        private static object Out(object value)
        {
            switch (value)
            {
                case null: return null;
                case bool or long or int or double or string or DateTime or TimeSpan: return value;
                case JsonObject o:
                {
                    var e = new Expando();
                    foreach (KeyValuePair<string, JsonNode> p in o) e[p.Key] = Out(p.Value);
                    return e;
                }
                case JsonArray a:
                {
                    var items = new object[a.Count];
                    for (int i = 0; i < items.Length; i++) items[i] = Out(a[i]);
                    return items;
                }
                case JsonValue v:
                    if (v.TryGetValue(out long l)) return l;
                    if (v.TryGetValue(out double d)) return d;
                    if (v.TryGetValue(out bool b)) return b;
                    if (v.TryGetValue(out string s)) return s;
                    return v.ToJsonString();
                default: return value.ToString();
            }
        }

        // ---- in: the columns of the pipe's type ----

        private enum ColumnKind { Long, Real, Bool, String, Char, DateTime, TimeSpan, Text, Dynamic }

        // A column and where its values are: a field of the message, or the whole message.
        private sealed class Source
        {
            public string Name;
            public ColumnKind Kind;
            public FieldShape Field;
            // An Expando entry the query names (step205): looked up in each
            // message, null where the message has none.
            public bool IsEntry;
        }

        private sealed class Columns
        {
            public Source[] Sources;
            public bool Bag;      // an Expando per message: the one column Value
            public bool Row;      // the hidden row number, last

            // A type of fields: every column is a field of the message.
            public bool Typed => !Bag && Sources.Length > 0 && Sources[0].Field != null;

            public Source Find(string name)
            {
                foreach (Source s in Sources) if (s.Name == name) return s;
                return null;
            }

            // The entries a query over Expandos names, as columns after Value.
            public void Name(List<string> names)
            {
                var all = new List<Source>(Sources);
                foreach (string n in names)
                    if (n != WholeColumn && n != RowColumn && Find(n) == null)
                        all.Add(new Source { Name = n, Kind = ColumnKind.Dynamic, IsEntry = true });
                Sources = all.ToArray();
            }

            public bool IsEntry(string name, TypeSymbol type)
            {
                Source s = Find(name);
                return s != null && s.IsEntry && type == ScalarTypes.Dynamic;
            }

            public static Columns Of(View root)
            {
                TypeShape shape = root.Shape;
                if (shape != null) return Of(shape);
                // A boxed number or bool: the value itself.
                ColumnKind kind = root.Kind switch
                {
                    ViewKind.Bool => ColumnKind.Bool,
                    ViewKind.Char => ColumnKind.Char,
                    ViewKind.Float => ColumnKind.Real,
                    _ => ColumnKind.Long,
                };
                return Whole(kind);
            }

            // No message came: the type the writer declared, if it did.
            public static Columns Declared(RawPipeReader input)
            {
                byte[] schema = PipeTransport.Schema(input.Handle, out ulong rootKey);
                RegionShapes shapes = schema == null ? null : RegionShapes.Parse(schema, out _);
                TypeShape root = shapes == null ? null : shapes[rootKey];
                if (root == null) return new Columns { Sources = new Source[0] };
                return Of(root);
            }

            private static Columns Of(TypeShape shape)
            {
                if (shape.BoxedValue != null) return Whole(KindOf(shape.BoxedValue));
                if (shape.IsString) return Whole(ColumnKind.String);
                if (shape.IsExpando)
                {
                    Columns bag = Whole(ColumnKind.Dynamic);
                    bag.Bag = true;
                    return bag;
                }
                if (shape.IsArray || shape.Fields == null) return Whole(ColumnKind.Dynamic);
                var sources = new Source[shape.Fields.Length];
                for (int i = 0; i < sources.Length; i++)
                    sources[i] = new Source { Name = shape.Fields[i].Name, Kind = KindOf(shape.Fields[i]), Field = shape.Fields[i] };
                return new Columns { Sources = sources };
            }

            private static Columns Whole(ColumnKind kind)
                => new Columns { Sources = new[] { new Source { Name = WholeColumn, Kind = kind } } };

            private static ColumnKind KindOf(FieldShape f)
            {
                switch (f.Kind)
                {
                    case FieldKind.Bool: return ColumnKind.Bool;
                    case FieldKind.Char: return ColumnKind.Char;
                    case FieldKind.Single:
                    case FieldKind.Double: return ColumnKind.Real;
                    case FieldKind.Reference: return f.TypeName == "System.String" ? ColumnKind.String : ColumnKind.Dynamic;
                    case FieldKind.Struct:
                        switch (f.TypeName)
                        {
                            case "System.DateTime": return ColumnKind.DateTime;
                            case "System.TimeSpan": return ColumnKind.TimeSpan;
                            case "System.Guid":
                            case "System.Decimal": return ColumnKind.Text;
                            default: return ColumnKind.Dynamic;
                        }
                    default: return ColumnKind.Long;
                }
            }

            public TableSymbol Symbol()
            {
                var columns = new List<ColumnSymbol>();
                foreach (Source s in Sources) columns.Add(new ColumnSymbol(s.Name, Scalar(s.Kind)));
                if (Row) columns.Add(new ColumnSymbol(RowColumn, ScalarTypes.Long));
                return new TableSymbol(TableName, columns);
            }

            private static TypeSymbol Scalar(ColumnKind kind) => kind switch
            {
                ColumnKind.Long => ScalarTypes.Long,
                ColumnKind.Real => ScalarTypes.Real,
                ColumnKind.Bool => ScalarTypes.Bool,
                ColumnKind.DateTime => ScalarTypes.DateTime,
                ColumnKind.TimeSpan => ScalarTypes.TimeSpan,
                ColumnKind.Dynamic => ScalarTypes.Dynamic,
                _ => ScalarTypes.String,
            };

            // One chunk of rows from the messages, in column arrays.
            public Column[] Chunk(List<RawRegion> regions, long firstRow)
            {
                int n = regions.Count;
                var columns = new Column[Sources.Length + (Row ? 1 : 0)];
                for (int c = 0; c < Sources.Length; c++) columns[c] = Build(Sources[c], regions);
                if (Row)
                {
                    var rows = new long?[n];
                    for (int r = 0; r < n; r++) rows[r] = firstRow + r;
                    columns[Sources.Length] = Column.Create(ScalarTypes.Long, rows);
                }
                return columns;
            }

            // A view lives in a local only (SOSR002): the cell is read where
            // the view is made.
            private static Column Build(Source s, List<RawRegion> regions)
            {
                int n = regions.Count;
                FieldShape f = s.Field;
                switch (s.Kind)
                {
                    case ColumnKind.Long:
                    {
                        var data = new long?[n];
                        for (int r = 0; r < n; r++)
                        {
                            View root = regions[r].Root;
                            data[r] = (long)(f == null ? root : root.FieldAt(f));
                        }
                        return Column.Create(ScalarTypes.Long, data);
                    }
                    case ColumnKind.Real:
                    {
                        var data = new double?[n];
                        for (int r = 0; r < n; r++)
                        {
                            View root = regions[r].Root;
                            data[r] = (double)(f == null ? root : root.FieldAt(f));
                        }
                        return Column.Create(ScalarTypes.Real, data);
                    }
                    case ColumnKind.Bool:
                    {
                        var data = new bool?[n];
                        for (int r = 0; r < n; r++)
                        {
                            View root = regions[r].Root;
                            data[r] = (bool)(f == null ? root : root.FieldAt(f));
                        }
                        return Column.Create(ScalarTypes.Bool, data);
                    }
                    case ColumnKind.DateTime:
                    {
                        var data = new DateTime?[n];
                        for (int r = 0; r < n; r++)
                        {
                            View root = regions[r].Root;
                            View v = f == null ? root : root.FieldAt(f);
                            if (TryTicks(v, out ulong bits))
                            {
                                // The ticks and the kind's two top bits, as DateTime keeps them.
                                ulong kind = bits >> 62;
                                data[r] = new DateTime((long)(bits & 0x3FFFFFFFFFFFFFFF),
                                                       kind == 1 ? DateTimeKind.Utc : kind >= 2 ? DateTimeKind.Local : DateTimeKind.Unspecified);
                            }
                            else data[r] = ViewCopy.ToHeapValue(v) is DateTime d ? d : null;
                        }
                        return Column.Create(ScalarTypes.DateTime, data);
                    }
                    case ColumnKind.TimeSpan:
                    {
                        var data = new TimeSpan?[n];
                        for (int r = 0; r < n; r++)
                        {
                            View root = regions[r].Root;
                            View v = f == null ? root : root.FieldAt(f);
                            if (TryTicks(v, out ulong bits)) data[r] = new TimeSpan((long)bits);
                            else data[r] = ViewCopy.ToHeapValue(v) is TimeSpan t ? t : null;
                        }
                        return Column.Create(ScalarTypes.TimeSpan, data);
                    }
                    case ColumnKind.Dynamic:
                    {
                        var data = new JsonNode?[n];
                        for (int r = 0; r < n; r++)
                        {
                            View root = regions[r].Root;
                            if (s.IsEntry) data[r] = root.Has(s.Name) ? Json(root[s.Name]) : null;
                            else data[r] = Json(f == null ? root : root.FieldAt(f));
                        }
                        return Column.Create(ScalarTypes.Dynamic, data);
                    }
                    default:
                    {
                        var data = new string?[n];
                        for (int r = 0; r < n; r++)
                        {
                            View root = regions[r].Root;
                            data[r] = Text(f == null ? root : root.FieldAt(f), s.Kind);
                        }
                        return Column.Create(ScalarTypes.String, data);
                    }
                }
            }

            // A DateTime or TimeSpan where it lies: a struct of one 64-bit
            // field, read as it is. A heap copy would need the type in this
            // image's catalog, and KQL's has neither (step205).
            private static bool TryTicks(View v, out ulong bits)
            {
                bits = 0;
                FieldShape[] fields = v.Kind == ViewKind.Object ? v.Shape?.Fields : null;
                if (fields == null || fields.Length != 1
                    || (fields[0].Kind != FieldKind.UInt64 && fields[0].Kind != FieldKind.Int64)) return false;
                bits = (ulong)v[fields[0].Name];
                return true;
            }

            private static string Text(View v, ColumnKind kind)
            {
                if (v.IsNull) return null;
                if (kind == ColumnKind.String) return (string)v;
                if (kind == ColumnKind.Char) return ((char)v).ToString();
                return ViewCopy.ToHeapValue(v)?.ToString();
            }

            // A value of the message as KQL's dynamic: objects as bags,
            // arrays as arrays, a DateTime as its round-trip text.
            private static JsonNode? Json(View v)
            {
                switch (v.Kind)
                {
                    case ViewKind.Null: return null;
                    case ViewKind.Bool: return JsonValue.Create((bool)v);
                    case ViewKind.Integer: return JsonValue.Create((long)v);
                    case ViewKind.Float: return JsonValue.Create((double)v);
                    case ViewKind.Char: return JsonValue.Create(((char)v).ToString());
                    case ViewKind.String: return JsonValue.Create((string)v);
                    case ViewKind.Array:
                    {
                        var a = new JsonArray();
                        foreach (View item in v) a.Add(Json(item));
                        return a;
                    }
                    default:
                    {
                        TypeShape t = v.Shape;
                        if (t != null && t.IsValueType)
                        {
                            // A std struct as its value; another image's as its fields.
                            object value = ViewCopy.ToHeapValue(v);
                            if (value is DateTime d) return JsonValue.Create(d.ToString("o"));
                            if (value is not Expando) return JsonValue.Create(value?.ToString());
                        }
                        var o = new JsonObject();
                        foreach (ViewField field in v.Fields) o[field.Name] = Json(field.Value);
                        return o;
                    }
                }
            }
        }

        // ---- in: the pipe as a table, chunk by chunk ----

        private sealed class PipeTable : ITableSource
        {
            private readonly Columns _columns;
            private readonly RawPipeReader _input;
            private RawRegion _first;
            private readonly bool _keep;      // regions held for Forward, by row number
            private readonly bool _release;   // ... and let go once the next chunk is asked for
            private readonly Dictionary<long, RawRegion> _held = new Dictionary<long, RawRegion>();
            private long _next;
            private bool _used;

            public PipeTable(TableSymbol type, Columns columns, RawPipeReader input, RawRegion first, bool keep, bool release)
            {
                Type = type;
                _columns = columns;
                _input = input;
                _first = first;
                _keep = keep;
                _release = release;
            }

            public TableSymbol Type { get; }

            public IEnumerable<ITableChunk> GetData()
            {
                if (_used) throw new InvalidOperationException("the input is a stream: a query can read it once");
                _used = true;
                var batch = new List<RawRegion>(ChunkRows);
                bool ended = false;
                while (!ended)
                {
                    RawRegion head = _first ?? _input.Next();
                    _first = null;
                    if (head == null) break;
                    batch.Clear();
                    batch.Add(head);
                    // What else has arrived, without waiting for more.
                    while (batch.Count < ChunkRows)
                    {
                        RawRegion more = _input.TryReceive();
                        if (more != null)
                        {
                            batch.Add(more);
                            continue;
                        }
                        if (_input.Status == PipeStatus.EndOfStream) ended = true;
                        else if (_input.Status == PipeStatus.Refused)
                            throw new PipeException(PipeStatus.Refused, "the input: a message could not be taken: " + _input.LastError);
                        break;
                    }
                    if (_release) Release();
                    long firstRow = _next;
                    _next += batch.Count;
                    Column[] chunk;
                    try
                    {
                        chunk = _columns.Chunk(batch, firstRow);
                    }
                    finally
                    {
                        for (int i = 0; i < batch.Count; i++)
                        {
                            if (_keep) _held[firstRow + i] = batch[i];
                            else batch[i].Dispose();
                        }
                    }
                    yield return new TableChunk(this, chunk);
                }
            }

            public IAsyncEnumerable<ITableChunk> GetDataAsync(CancellationToken cancellation = default) => GetData().ToAsyncEnumerable();

            /// <summary>The region of a row, now the caller's; null when it was let go.</summary>
            public RawRegion Take(long row)
            {
                if (!_held.TryGetValue(row, out RawRegion region)) return null;
                _held.Remove(row);
                return region;
            }

            private void Release()
            {
                foreach (RawRegion r in _held.Values) r.Dispose();
                _held.Clear();
            }

            public void Close()
            {
                Release();
                _first?.Dispose();
                _first = null;
            }
        }
    }
}
