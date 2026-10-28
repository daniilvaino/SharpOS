using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace HistApp
{
    // HIST [FIELD] [--bins N] [--width W] — an ASCII histogram of one field of
    // whatever type comes down the pipe (read through views, so a type no
    // program declares works too). Without FIELD on a text stream: the lines.
    internal static unsafe class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return AppRuntime.Finish(Run(PipeApps.StressArgs.Apply(AppHost.Arguments)));
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run(AppHost.Arguments);

        private const int ExitOk = 0, ExitNoData = 1, ExitUsage = 2;

        // The standard output: the next stage of the pipe, a file, or the screen
        // when nothing follows. Console.WriteLine would go to the screen always.
        private static TextWriter Out;

        private static int Run(string[] args)
        {
            using TextWriter output = Pipe.WriteText();
            Out = output;
            return Draw(args);
        }

        private static int Draw(string[] args)
        {
            Options o;
            try
            {
                o = Options.Parse(args);
            }
            catch (UsageException e)
            {
                Console.Error.WriteLine("hist: " + e.Message);
                Console.Error.WriteLine("try: hist --help");
                return ExitUsage;
            }
            if (o.Help)
            {
                PrintHelp();
                return ExitOk;
            }

            var tally = new Tally();
            string firstType = null;
            List<string> firstFields = null;

            // A byte stream (cat, READ) is text cut anywhere: gathered whole, split into lines at the end.
            byte[] gathered = null;
            int gatheredLength = 0;

            foreach (View message in Pipe.Read())
            {
                // A view is valid only for this step of the loop: take what is needed now.
                if (o.Field == null && message.Kind == ViewKind.Array && message.TypeName == "System.Byte[]")
                {
                    int length = message.Length;
                    if (gathered == null) gathered = new byte[Math.Max(4096, length)];
                    if (gatheredLength + length > gathered.Length)
                        Array.Resize(ref gathered, Math.Max(gathered.Length * 2, gatheredLength + length));
                    for (int i = 0; i < length; i++) gathered[gatheredLength + i] = (byte)message[i];
                    gatheredLength += length;
                    if (firstType == null) firstType = "bytes";
                    continue;
                }
                tally.Records++;
                if (firstType == null)
                {
                    firstType = message.TypeName ?? "null";
                    if (message.Kind == ViewKind.Object)
                        firstFields = message.Fields.Select(f => f.Name).ToList();
                }

                if (o.Field == null)
                {
                    if (message.Kind != ViewKind.String)
                    {
                        Console.Error.WriteLine("hist: the stream carries " + firstType + ", not text: name a field");
                        if (firstFields != null && firstFields.Count > 0)
                            Console.Error.WriteLine("hist: its fields: " + string.Join(", ", firstFields));
                        return ExitUsage;
                    }
                    tally.Add(message);
                    continue;
                }

                // A dotted path through nested objects ("Inner.X"), walked here:
                // a view may not leave this step of the loop, not even through an out.
                View value = message;
                bool found = true;
                foreach (string part in o.Path)
                {
                    if (value.Kind != ViewKind.Object || !value.Has(part)) { found = false; break; }
                    value = value[part];
                }
                if (!found)
                {
                    tally.Missing++;
                    continue;
                }
                tally.Add(value);
            }

            if (gatheredLength > 0)
            {
                string text = Encoding.UTF8.GetString(gathered, 0, gatheredLength);
                string[] split = text.Split('\n');
                int count = split.Length;
                if (count > 0 && split[count - 1].Length == 0) count--;   // "a\nb\n" is two lines
                for (int i = 0; i < count; i++)
                {
                    tally.Records++;
                    tally.AddCategory(split[i].TrimEnd('\r'));
                }
            }

            if (tally.Records == 0)
            {
                Console.Error.WriteLine("hist: the input is empty");
                return ExitNoData;
            }
            if (tally.Seen == 0)
            {
                Console.Error.WriteLine("hist: no record has '" + o.Field + "' (" + tally.Records.ToString() + " records of " + firstType + ")");
                if (firstFields != null && firstFields.Count > 0)
                    Console.Error.WriteLine("hist: its fields: " + string.Join(", ", firstFields));
                return ExitNoData;
            }

            int width = o.Width > 0 ? o.Width : ScreenWidth();
            Render(tally, o, firstType, width);
            return ExitOk;
        }

        // The screen's width; a narrow or unknown one falls back to 80.
        private static int ScreenWidth()
        {
            int w = Console.WindowWidth;
            return w >= 40 ? w : 80;
        }

        // ---- counting ----

        private sealed class Tally
        {
            public long Records, Missing, Seen;
            public string ValueType;
            public bool Mixed;

            // Numbers are kept until the end (bins need min and max); categories are counted.
            public readonly List<double> Numbers = new List<double>();
            public bool AllIntegral = true;
            public readonly Dictionary<string, long> Categories = new Dictionary<string, long>(StringComparer.Ordinal);

            public void Add(View v)
            {
                Seen++;
                string type = v.TypeName ?? "null";
                // Kept past this message: a copy of our own, not the region's text.
                if (ValueType == null) ValueType = new string(type.AsSpan());
                else if (ValueType != type) Mixed = true;

                bool number = (v.Kind == ViewKind.Integer && type.StartsWith("System.", StringComparison.Ordinal))
                              || v.Kind == ViewKind.Float;
                if (number)
                {
                    double d = (double)v;
                    if (v.Kind == ViewKind.Float && d != Math.Floor(d)) AllIntegral = false;
                    Numbers.Add(d);
                    return;
                }
                string key = v.Kind == ViewKind.String ? (string)v : v.ToString();
                CountCategory(key ?? "null");
            }

            // A line of a byte stream: text already, no view behind it.
            public void AddCategory(string line)
            {
                Seen++;
                if (ValueType == null) ValueType = "System.String";
                CountCategory(line);
            }

            private void CountCategory(string key)
            {
                Categories.TryGetValue(key, out long n);
                Categories[key] = n + 1;
            }
        }

        // ---- output ----

        private static void Render(Tally t, Options o, string recordType, int width)
        {
            string what = o.Field ?? "line";
            var head = new StringBuilder();
            head.Append("hist: ").Append(t.Seen.ToString()).Append(' ').Append(what).Append(" of ")
                .Append(t.Records.ToString()).Append(' ').Append(recordType);
            if (t.ValueType != null && o.Field != null) head.Append(", ").Append(t.Mixed ? "mixed types" : t.ValueType);
            if (t.Missing > 0) head.Append(", ").Append(t.Missing.ToString()).Append(" without it");
            Out.WriteLine(head.ToString());

            var rows = new List<(string Label, long Count)>();
            if (t.Numbers.Count > 0)
            {
                double min = double.MaxValue, max = double.MinValue, sum = 0;
                foreach (double x in t.Numbers)
                {
                    if (x < min) min = x;
                    if (x > max) max = x;
                    sum += x;
                }
                double mean = sum / t.Numbers.Count;
                Out.WriteLine("min " + Num(min) + "  max " + Num(max) + "  mean " + Num(mean));

                // Few distinct integers: a row each. Counted until there are too many to list.
                int limit = Math.Max(o.Bins, 30);
                Dictionary<double, long> each = null;
                if (t.AllIntegral)
                {
                    each = new Dictionary<double, long>();
                    foreach (double x in t.Numbers)
                    {
                        each.TryGetValue(x, out long n);
                        each[x] = n + 1;
                        if (each.Count > limit) { each = null; break; }
                    }
                }
                if (each != null)
                {
                    var keys = new List<double>(each.Keys);
                    keys.Sort();
                    foreach (double k in keys)
                        rows.Add((Num(k), each[k]));
                }
                else
                {
                    int bins = o.Bins;
                    var counts = new long[bins];
                    double span = max - min;
                    foreach (double x in t.Numbers)
                    {
                        int i = span == 0 ? 0 : (int)((x - min) / span * bins);
                        if (i >= bins) i = bins - 1;
                        counts[i]++;
                    }
                    for (int i = 0; i < bins; i++)
                    {
                        double lo = min + span * i / bins, hi = min + span * (i + 1) / bins;
                        rows.Add(("[" + Num(lo) + ", " + Num(hi) + (i == bins - 1 ? "]" : ")"), counts[i]));
                    }
                }
            }
            // Categories: most frequent first, then by name.
            var cats = new List<KeyValuePair<string, long>>(t.Categories);
            cats.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            int shownCats = Math.Min(cats.Count, o.Top);
            for (int i = 0; i < shownCats; i++)
                rows.Add((Quote(cats[i].Key), cats[i].Value));
            long restRecords = 0;
            for (int i = shownCats; i < cats.Count; i++) restRecords += cats[i].Value;
            string rest = cats.Count > shownCats
                ? "... " + (cats.Count - shownCats).ToString() + " more values, " + restRecords.ToString() + " records (--top to see more)"
                : null;

            int longest = 0;
            long top = 0;
            foreach (var r in rows)
            {
                if (r.Label.Length > longest) longest = r.Label.Length;
                if (r.Count > top) top = r.Count;
            }
            int labelWidth = Math.Min(longest, Math.Max(10, width / 3));
            int countWidth = top.ToString().Length;
            int barRoom = Math.Max(1, width - labelWidth - countWidth - 4);

            foreach (var (label, count) in rows)
            {
                string shown = label.Length > labelWidth ? label.Substring(0, labelWidth - 1) + "~" : label.PadRight(labelWidth);
                int bar = top == 0 ? 0 : (int)Math.Round((double)count * barRoom / top);
                if (count > 0 && bar == 0) bar = 1;
                Out.WriteLine(shown + " " + count.ToString().PadLeft(countWidth) + " " + new string('#', bar));
            }
            if (rest != null) Out.WriteLine(rest);
        }

        private static string Num(double d)
        {
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // Control characters would move the cursor: show them escaped.
        private static string Quote(string s)
        {
            var b = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\t') b.Append("\\t");
                else if (c == '\r') b.Append("\\r");
                else if (c == '\n') b.Append("\\n");
                else if (c < ' ') b.Append("\\x").Append(((int)c).ToString("x2"));
                else b.Append(c);
            }
            return b.Length == 0 ? "\"\"" : b.ToString();
        }

        private static void PrintHelp()
        {
            Out.WriteLine("hist [FIELD] [--bins N] [--top N] [--width W] - an ASCII histogram of a field of the stream");
            Out.WriteLine("  FIELD      a field by name; a dotted path reaches into nested objects (Inner.X)");
            Out.WriteLine("             omitted on a text stream: the lines themselves are counted");
            Out.WriteLine("  --bins N   intervals for numbers with many values (default 10)");
            Out.WriteLine("  --top N    categories shown, most frequent first (default 20)");
            Out.WriteLine("  --width W  line width (default: the screen's, or 80)");
            Out.WriteLine("numbers with few distinct integer values get a row each; strings, bools and");
            Out.WriteLine("enums are counted as categories, most frequent first.");
            Out.WriteLine("examples: pipegen 500 | hist Level    synth 10000 | hist Weight --bins 8");
            Out.WriteLine("exit: 0 drawn, 1 no data or no such field, 2 bad arguments");
        }

        // ---- arguments ----

        private sealed class UsageException : Exception
        {
            public UsageException(string message) : base(message) { }
        }

        private sealed class Options
        {
            public string Field;
            public string[] Path;
            public int Bins = 10;
            public int Top = 20;
            public int Width;
            public bool Help;

            public static Options Parse(string[] args)
            {
                var o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    switch (a)
                    {
                        case "-h":
                        case "--help":
                        case "-?":
                            o.Help = true;
                            break;
                        case "--bins":
                            o.Bins = PositiveInt(a, ++i < args.Length ? args[i] : null, 1, 200);
                            break;
                        case "--top":
                            o.Top = PositiveInt(a, ++i < args.Length ? args[i] : null, 1, 100000);
                            break;
                        case "--width":
                            o.Width = PositiveInt(a, ++i < args.Length ? args[i] : null, 20, 1000);
                            break;
                        default:
                            if (a.StartsWith("-", StringComparison.Ordinal))
                                throw new UsageException("unknown option '" + a + "'");
                            if (o.Field != null)
                                throw new UsageException("one field only: '" + o.Field + "' and '" + a + "'");
                            if (a.Split('.').Any(p => p.Length == 0))
                                throw new UsageException("'" + a + "' is not a field path");
                            o.Field = a;
                            o.Path = a.Split('.');
                            break;
                    }
                }
                return o;
            }

            private static int PositiveInt(string option, string text, int min, int max)
            {
                if (text == null) throw new UsageException(option + " needs a number");
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
                    throw new UsageException(option + " takes " + min.ToString() + ".." + max.ToString() + ", not '" + text + "'");
                return n;
            }
        }
    }
}
