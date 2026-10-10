using System;
using System.Collections.Generic;
using System.Runtime;
using System.Text.Json.Nodes;
using BabyKusto.Core;
using BabyKusto.Core.Evaluation;
using Kusto.Language.Symbols;
using SharpOS.AppSdk;

namespace KqlApps
{
    // KQLBABY.EXE — BabyKusto (vendor/BabyKusto) executing KQL on the app
    // tier (step200): a table in memory, queries over it, their rows checked.
    // Exit code = checks passed.
    internal static unsafe class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run();
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run();

        private static int s_passed, s_failed;

        private static void Check(string name, bool ok, string detail = null)
        {
            if (ok) s_passed++; else s_failed++;
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name + (detail != null ? " — " + detail : ""));
        }

        private static int Run()
        {
            var engine = new BabyKustoEngine();
            engine.AddGlobalTable(MakeTable());

            // where, project, take: the rows keep their order.
            List<object[]> rows = Rows(engine, "T | where Level >= 3 | project Text, Level | take 5", out string shape);
            Check("where/project/take: 3 rows of Text, Level",
                  rows != null && rows.Count == 3 && shape == "Text:string, Level:long"
                  && (string)rows[0][0] == "disk full" && (long)rows[0][1] == 4,
                  Describe(rows, shape));

            // summarize: one row per level, counted.
            rows = Rows(engine, "T | summarize n = count() by Level | sort by Level asc", out shape);
            Check("summarize count() by Level",
                  rows != null && rows.Count == 5 && (long)rows[0][0] == 1 && (long)rows[0][1] == 2
                  && (long)rows[4][0] == 5 && (long)rows[4][1] == 1,
                  Describe(rows, shape));

            // sort and take: the heaviest two.
            rows = Rows(engine, "T | sort by Weight desc | take 2 | project Text", out shape);
            Check("sort by Weight desc | take 2",
                  rows != null && rows.Count == 2 && (string)rows[0][0] == "kernel panic" && (string)rows[1][0] == "disk full",
                  Describe(rows, shape));

            // extend: an expression per row, doubles stay doubles.
            rows = Rows(engine, "T | extend W2 = Weight * 2 | where Level == 2 | project W2", out shape);
            Check("extend W2 = Weight * 2",
                  rows != null && rows.Count == 1 && (double)rows[0][0] == 5.0,
                  Describe(rows, shape));

            // Strings: contains, case-insensitive, as KQL has it.
            rows = Rows(engine, "T | where Text contains \"DISK\" | count", out shape);
            Check("where Text contains \"DISK\" | count",
                  rows != null && rows.Count == 1 && (long)rows[0][0] == 2,
                  Describe(rows, shape));

            // print: no table at all.
            rows = Rows(engine, "print x = 1 + 2, s = strcat(\"a\", \"b\")", out shape);
            Check("print x = 1 + 2, s = strcat(\"a\", \"b\")",
                  rows != null && rows.Count == 1 && (long)rows[0][0] == 3 && (string)rows[0][1] == "ab",
                  Describe(rows, shape));

            // dynamic (step201): a bag per row, compared with a number, a
            // string, a bool and another dynamic; a missing entry is null.
            var bags = new BabyKustoEngine();
            bags.AddGlobalTable(MakeBags());
            rows = Rows(bags, "D | where Bag.n >= 3 | project N = tolong(Bag.n)", out shape);
            Check("dynamic: where Bag.n >= 3 | project N = tolong(Bag.n)",
                  rows != null && rows.Count == 2 && (long)rows[0][0] == 4 && (long)rows[1][0] == 7,
                  Describe(rows, shape));
            string counts = Count(bags, "Bag.kind == \"a\"") + ", " + Count(bags, "Bag.r > 1.0") + ", "
                            + Count(bags, "Bag.ok == true") + ", " + Count(bags, "Bag.n > Bag.r") + ", "
                            + Count(bags, "Bag.absent == 1");
            Check("dynamic: == string, > real, == bool, dynamic > dynamic, a missing entry: 2, 3, 3, 3, 0 (" + counts + ")",
                  counts == "2, 3, 3, 3, 0");

            // Arithmetic: typed by the other operand, two dynamics as dynamic.
            rows = Rows(bags, "D | project T = Bag.n * 2, H = Bag.r / 2.0, S = Bag.n + Bag.r", out shape);
            Check("dynamic: Bag.n * 2 (long), Bag.r / 2.0 (real), Bag.n + Bag.r (dynamic)",
                  rows != null && rows.Count == 4 && shape == "T:long, H:real, S:dynamic"
                  && (long)rows[1][0] == 8 && (double)rows[1][1] == 1.25 && rows[1][2].ToString() == "6.5"
                  && (long)rows[3][0] == 14 && rows[0][2].ToString() == "1.5",
                  Describe(rows, shape));

            Console.WriteLine("[kqlbaby] done: passed " + s_passed.ToString() + ", failed " + s_failed.ToString());
            return s_passed;
        }

        private static InMemoryTableSource MakeTable()
        {
            var type = new TableSymbol("T", "(Level:long, Text:string, Weight:real)");
            long?[] level = { 1, 4, 2, 3, 1, 5 };
            string?[] text = { "boot", "disk full", "slow tick", "disk retry", "login", "kernel panic" };
            double?[] weight = { 0.5, 7.25, 2.5, 3.0, 1.0, 9.75 };
            return new InMemoryTableSource(type, new Column[]
            {
                Column.Create(ScalarTypes.Long, level),
                Column.Create(ScalarTypes.String, text),
                Column.Create(ScalarTypes.Real, weight),
            });
        }

        private static InMemoryTableSource MakeBags()
        {
            var type = new TableSymbol("D", "(Bag:dynamic)");
            JsonNode?[] bag =
            {
                JsonNode.Parse("{\"n\":1,\"kind\":\"a\",\"r\":0.5,\"ok\":true}"),
                JsonNode.Parse("{\"n\":4,\"kind\":\"b\",\"r\":2.5,\"ok\":false}"),
                JsonNode.Parse("{\"n\":2,\"kind\":\"a\",\"r\":3.5,\"ok\":true}"),
                JsonNode.Parse("{\"n\":7,\"kind\":\"c\",\"r\":3.0,\"ok\":true}"),
            };
            return new InMemoryTableSource(type, new Column[] { Column.Create(ScalarTypes.Dynamic, bag) });
        }

        // Rows of D the condition keeps; "?" when the query does not run.
        private static string Count(BabyKustoEngine engine, string condition)
        {
            List<object[]> rows = Rows(engine, "D | where " + condition + " | count", out _);
            return rows != null && rows.Count == 1 ? ((long)rows[0][0]).ToString() : "?";
        }

        // The rows of a tabular result, with its schema as "Name:type, …";
        // null (and the exception printed) when the query does not run.
        private static List<object[]> Rows(BabyKustoEngine engine, string query, out string shape)
        {
            shape = null;
            try
            {
                if (engine.Evaluate(query) is not TabularResult table) return null;
                var parts = new List<string>();
                foreach (ColumnSymbol column in table.Value.Type.Columns)
                    parts.Add(column.Name + ":" + column.Type.Name);
                shape = string.Join(", ", parts);

                var rows = new List<object[]>();
                foreach (ITableChunk chunk in table.Value.GetData())
                {
                    for (int r = 0; r < chunk.RowCount; r++)
                    {
                        var row = new object[chunk.Columns.Length];
                        for (int c = 0; c < row.Length; c++) row[c] = chunk.Columns[c].RawData.GetValue(r);
                        rows.Add(row);
                    }
                }
                return rows;
            }
            catch (Exception e)
            {
                Console.WriteLine("[kqlbaby] " + query + " threw " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
                return null;
            }
        }

        private static string Describe(List<object[]> rows, string shape)
        {
            if (rows == null) return "no result";
            var lines = new List<string>();
            foreach (object[] row in rows)
            {
                var cells = new List<string>();
                foreach (object cell in row) cells.Add(cell == null ? "null" : cell.ToString());
                lines.Add(string.Join(" | ", cells));
            }
            return "(" + shape + ") " + string.Join("; ", lines);
        }
    }
}
