using System;
using System.Collections.Generic;
using System.Runtime;
using Kusto.Language;
using Kusto.Language.Editor;
using Kusto.Language.Symbols;
using SharpOS.AppSdk;

namespace KqlApps
{
    // KQLTEST.EXE — Kusto.Language (vendor/KustoLanguage) on the app tier
    // (step198): a query parsed and bound against a table, its diagnostics,
    // completion at a cursor, classification for colouring. Exit code =
    // checks passed.
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
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var table = new TableSymbol("T", "(Level:long, Text:string, Weight:real)");
            GlobalState globals = GlobalState.Default.WithDatabase(new DatabaseSymbol("db", table));
            Console.WriteLine("[kqltest] globals in " + clock.ElapsedMilliseconds.ToString() + " ms");

            // A correct query: no diagnostics, the result columns known.
            clock.Restart();
            KustoCode good = KustoCode.ParseAndAnalyze("T | where Level >= 3 | project Text, Level | take 5", globals);
            Console.WriteLine("[kqltest] parse and bind in " + clock.ElapsedMilliseconds.ToString() + " ms");
            IReadOnlyList<Diagnostic> none = good.GetDiagnostics();
            Check("a correct query has no diagnostics", none.Count == 0, none.Count == 0 ? null : none[0].Message);
            Check("its result is a table of Text and Level",
                  good.ResultType is TableSymbol result && result.Columns.Count == 2
                  && result.Columns[0].Name == "Text" && result.Columns[1].Name == "Level",
                  good.ResultType?.Name);

            // Errors with their positions.
            KustoCode bad = KustoCode.ParseAndAnalyze("T | where Levl >= 3", globals);
            IReadOnlyList<Diagnostic> errors = bad.GetDiagnostics();
            Check("an unknown column is an error at its place",
                  errors.Count > 0 && errors[0].Start == 10,
                  errors.Count > 0 ? errors[0].Start.ToString() + ": " + errors[0].Message : "no diagnostics");
            KustoCode broken = KustoCode.ParseAndAnalyze("T | where Level >= ", globals);
            Check("a cut expression is a syntax error", broken.GetDiagnostics().Count > 0);

            // Completion: after "where " the columns are offered.
            string partial = "T | where ";
            var service = new KustoCodeService(partial, globals);
            clock.Restart();
            CompletionInfo completion = service.GetCompletionItems(partial.Length);
            if (completion.Items.Count == 0)
            {
                // The service swallows what went wrong; the completer itself does not.
                try { new KustoCompleter(KustoCode.ParseAndAnalyze(partial, globals), CompletionOptions.Default, default).GetCompletionItems(partial.Length); }
                catch (Exception e) { Console.WriteLine("[kqltest] completer threw " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace); }
            }
            Console.WriteLine("[kqltest] completion in " + clock.ElapsedMilliseconds.ToString() + " ms, "
                              + completion.Items.Count.ToString() + " items");
            bool level = false, text = false;
            foreach (CompletionItem item in completion.Items)
            {
                if (item.DisplayText == "Level") level = true;
                if (item.DisplayText == "Text") text = true;
            }
            Check("completion after 'where' offers the columns", level && text);

            string afterPipe = "T | ";
            CompletionInfo operators = new KustoCodeService(afterPipe, globals).GetCompletionItems(afterPipe.Length);
            bool where = false, project = false;
            foreach (CompletionItem item in operators.Items)
            {
                if (item.DisplayText == "where") where = true;
                if (item.DisplayText == "project") project = true;
            }
            Check("completion after '|' offers the operators", where && project);

            // Classification: what the editor colours.
            string query = "T | where Level >= 3";
            ClassificationInfo classes = new KustoCodeService(query, globals).GetClassifications(0, query.Length);
            bool keyword = false, column = false, literal = false;
            foreach (ClassifiedRange range in classes.Classifications)
            {
                if (range.Kind == ClassificationKind.QueryOperator || range.Kind == ClassificationKind.Keyword) keyword = true;
                if (range.Kind == ClassificationKind.Column) column = true;
                if (range.Kind == ClassificationKind.Literal) literal = true;
            }
            Check("classification marks operator, column and literal", keyword && column && literal);

            Console.WriteLine("[kqltest] done: passed " + s_passed.ToString() + ", failed " + s_failed.ToString());
            return s_passed;
        }
    }
}
