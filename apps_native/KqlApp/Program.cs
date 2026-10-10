using System;
using System.Collections.Generic;
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
    // The columns are the type of the pipe's messages: integers and enums
    // long, float and double real, bool, string and char string, DateTime
    // datetime, TimeSpan timespan, Guid and decimal their text, anything else
    // (objects, arrays, structs) dynamic. A message that is not an object of
    // fields — an Expando, an array, a string, a number — is one column,
    // Value: an Expando's entries are `Value.Name`.
    //
    // The input is read in chunks of what has arrived (up to 256 messages,
    // never waiting for more than one); operators that need everything (sort,
    // summarize) wait for the end, `where` answers as messages come, `take`
    // stops reading. A query of where, take and sort alone sends the input's
    // own blocks on — no copy, no translation, the input's type — found by a
    // hidden row number. Over Expandos, extend does too: its columns, when
    // they are numbers, bools or strings, are written into each message in
    // place (ExpandoEdits), and the message keeps everything else it held.
    // Any other result goes out as Expando messages — over Expandos, the
    // entries of Value with the other columns beside them.
    //
    // Exit code: 0; 1 for a query that fails (the message says where); 2 for
    // usage; 141 when the next stage left.
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
                Console.WriteLine("KQL: " + (e is PipeException ? "" : e.GetType().Name + ": ") + e.Message);
                return 1;
            }
        }

        private static int OverInput(string query, Plan plan)
        {
            RawPipeReader input = Pipe.Read();
            RawRegion first = null;
            PipeTable source = null;
            try
            {
                first = input.Next();
                Columns columns = first != null ? Columns.Of(first.Root) : Columns.Declared(input);
                columns.Row = plan.Forward || (plan.Extend && columns.Bag);
                TableSymbol schema = columns.Symbol();
                if (!Analyze(query, schema, out KustoCode code,
                             columns.Sources.Length == 0 ? "the input is empty and declares no type" : null))
                    return 1;

                // The input's blocks go on as they are, or with the new
                // columns written into each message; else new messages.
                Addition[] added = null;
                bool forward = false;
                if (columns.Row && code.ResultType is TableSymbol result)
                {
                    if (plan.Forward && SameColumns(result, schema)) forward = true;
                    else if (plan.Extend && columns.Bag && Additions(result, out added)) forward = true;
                }
                source = new PipeTable(schema, columns, input, first, keep: forward, release: plan.Streaming);
                first = null;

                var engine = new BabyKustoEngine();
                engine.AddGlobalTable(source);
                EvaluationResult evaluated = engine.Evaluate(query);
                return forward ? Forward(evaluated, source, input, added) : Materialize(evaluated, columns.Bag);
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
            if (!Analyze(query, null, out _, null)) return 1;
            return Materialize(new BabyKustoEngine().Evaluate(query), false);
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
        private static bool Additions(TableSymbol result, out Addition[] added)
        {
            added = null;
            var list = new List<Addition>();
            bool value = false, row = false;
            for (int i = 0; i < result.Columns.Count; i++)
            {
                ColumnSymbol c = result.Columns[i];
                if (c.Name == WholeColumn && c.Type == ScalarTypes.Dynamic) { value = true; continue; }
                if (c.Name == RowColumn && c.Type == ScalarTypes.Long) { row = true; continue; }
                if (c.Type != ScalarTypes.Long && c.Type != ScalarTypes.Int && c.Type != ScalarTypes.Real
                    && c.Type != ScalarTypes.Bool && c.Type != ScalarTypes.String) return false;
                list.Add(new Addition { Index = i, Name = new ExpandoName(c.Name) });
            }
            if (!value || !row) return false;
            added = list.ToArray();
            return true;
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

            public static Plan Of(string query)
            {
                var plan = new Plan();
                KustoCode.Parse(query).Syntax.WalkElements(e =>
                {
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
        private static int Forward(EvaluationResult evaluated, PipeTable source, RawPipeReader input, Addition[] added)
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
                        if (added != null)
                        {
                            try
                            {
                                for (int a = 0; a < added.Length; a++)
                                    Assign(region, added[a].Name, chunk.Columns[added[a].Index], r);
                            }
                            catch
                            {
                                region.Dispose();
                                throw;
                            }
                        }
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
                if (output != 0) PipeTransport.Close(output);
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

        // ---- out: new messages ----

        // Over Expandos (a bag), a row's Value is the message: its entries come
        // first, the other columns after them, and a column of an entry's name
        // takes its place — as extend's edits in place do.
        private static int Materialize(EvaluationResult evaluated, bool bag)
        {
            using PipeWriter<Expando> output = Pipe.Write<Expando>();
            if (evaluated is TabularResult table)
            {
                var names = new List<string>();
                foreach (ColumnSymbol c in table.Value.Type.Columns) names.Add(c.Name);
                int whole = bag ? IndexOf(table.Value.Type, WholeColumn) : -1;
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
                            if (c != whole && names[c] != RowColumn) e[names[c]] = Out(chunk.Columns[c].RawData.GetValue(r));
                        output.Copy(e);
                    }
                }
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
        }

        private sealed class Columns
        {
            public Source[] Sources;
            public bool Bag;      // an Expando per message: the one column Value
            public bool Row;      // the hidden row number, last

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
                            data[r] = ViewCopy.ToHeapValue(f == null ? root : root.FieldAt(f)) is DateTime d ? d : null;
                        }
                        return Column.Create(ScalarTypes.DateTime, data);
                    }
                    case ColumnKind.TimeSpan:
                    {
                        var data = new TimeSpan?[n];
                        for (int r = 0; r < n; r++)
                        {
                            View root = regions[r].Root;
                            data[r] = ViewCopy.ToHeapValue(f == null ? root : root.FieldAt(f)) is TimeSpan t ? t : null;
                        }
                        return Column.Create(ScalarTypes.TimeSpan, data);
                    }
                    case ColumnKind.Dynamic:
                    {
                        var data = new JsonNode?[n];
                        for (int r = 0; r < n; r++)
                        {
                            View root = regions[r].Root;
                            data[r] = Json(f == null ? root : root.FieldAt(f));
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
