using System;
using System.Collections.Generic;
using System.Runtime;
using System.Text;
using System.Text.RegularExpressions;
using SharpOS.AppSdk;

namespace RegexCheckApp
{
    // REGEXTEST.EXE — System.Text.RegularExpressions (std-no-runtime/Text/
    // RegularExpressions) on the app tier: the backtracking interpreter, the
    // same patterns through NonBacktracking, and Compiled, which runs on the
    // interpreter here. Expected values are what .NET gives for the same calls.
    // The exit code is the number of failed checks.
    internal static unsafe partial class AppEntry
    {
        private static int s_passed;
        private static int s_failed;

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return AppRuntime.Finish(Main());
        }

        private static void Check(string name, bool ok)
        {
            if (ok) s_passed++; else s_failed++;
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
        }

        private static void Check(string name, string expected, string actual)
        {
            bool ok = expected == actual;
            Check(name, ok);
            if (!ok)
            {
                Console.WriteLine("        expected: " + expected);
                Console.WriteLine("        actual:   " + actual);
            }
        }

        private static int Main()
        {
            Console.WriteLine("[regexcheck] begin");
            Run("Checks", Checks);
            foreach (var options in new[] { RegexOptions.None, RegexOptions.Compiled, RegexOptions.NonBacktracking })
            {
                RegexOptions o = options;
                Run("Engine " + Name(o), () => Engine(o));
            }
            Run("Backtracking", Backtracking);
            Run("Errors", Errors);
            Run("Generated", Generated);
            Console.WriteLine("[regexcheck] done: passed " + s_passed.ToString() + ", failed " + s_failed.ToString());
            return s_failed;
        }

        private static void Run(string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                Check(name + " threw " + e.GetType().Name + ": " + e.Message, false);
                Console.WriteLine(e.StackTrace);
            }
        }

        private static string Name(RegexOptions o) =>
            o == RegexOptions.Compiled ? "Compiled" : o == RegexOptions.NonBacktracking ? "NonBacktracking" : "interpreter";

        // What every engine must agree on: matches, captures of the last
        // iteration, anchors, classes, case, Unicode categories.
        private static void Engine(RegexOptions o)
        {
            string n = Name(o) + ": ";
            Check(n + "IsMatch", Regex.IsMatch("abc123", @"\d+", o));
            Check(n + "no match", !Regex.IsMatch("abcdef", @"\d", o));

            Match m = new Regex(@"(\w+)@(\w+)\.com", o).Match("mail: joe@example.com, ann@test.com");
            Check(n + "Match value", "joe@example.com", m.Value);
            Check(n + "group 1 and 2", "joe|example", m.Groups[1].Value + "|" + m.Groups[2].Value);
            Check(n + "Index and Length", "6,15", m.Index.ToString() + "," + m.Length.ToString());
            Check(n + "NextMatch", "ann@test.com", m.NextMatch().Value);

            var all = new StringBuilder();
            foreach (Match x in Regex.Matches("a1b22c333", @"\d+", o))
                all.Append(x.Value).Append(';');
            Check(n + "Matches", "1;22;333;", all.ToString());
            Check(n + "Count", "3", Regex.Count("a1b22c333", @"\d+", o).ToString());

            Check(n + "IgnoreCase", Regex.IsMatch("HeLLo", "^hello$", o | RegexOptions.IgnoreCase));
            Check(n + "IgnoreCase Cyrillic", Regex.IsMatch("ПРИВЕТ", "^привет$", o | RegexOptions.IgnoreCase));
            Check(n + "Multiline ^ and $", "2", Regex.Count("one\ntwo\nthree", "^t", o | RegexOptions.Multiline).ToString());
            Check(n + "Singleline dot", Regex.IsMatch("a\nb", "a.b", o | RegexOptions.Singleline));
            Check(n + "dot without Singleline", !Regex.IsMatch("a\nb", "a.b", o));
            Check(n + @"\p{L} over Cyrillic and Greek", "Жук|λόγος", string.Join("|", Values(Regex.Matches("12 Жук, 7 λόγος!", @"\p{L}+", o))));
            Check(n + "character class subtraction", "bcd", string.Join("", Values(Regex.Matches("abcde", "[a-e-[ae]]", o))));
            Check(n + @"\b word boundary", "2", Regex.Count("cat concat cat", @"\bcat\b", o).ToString());
            Check(n + "alternation, leftmost", "foo", Regex.Match("xfoobar", "bar|foo", o).Value);
            Check(n + "{2,3} quantifier", "aaa", Regex.Match("aaaa", "a{2,3}", o).Value);
            Check(n + "named group", "2026", new Regex(@"(?<year>\d{4})-\d\d", o).Match("on 2026-11 we").Groups["year"].Value);
            Check(n + "Replace with $1", "b-a d-c", Regex.Replace("a-b c-d", @"(\w)-(\w)", "$2-$1", o));
            Check(n + "Split", "a|b|c", string.Join("|", Regex.Split("a, b;c", @"[,;]\s*", o)));
            Check(n + "RegexOptions.ExplicitCapture", "1", new Regex(@"(a)(?<n>b)", o | RegexOptions.ExplicitCapture).Match("ab").Groups.Count == 2 ? "1" : "0");

            int found = 0;
            foreach (ValueMatch v in new Regex(@"[aeiou]", o).EnumerateMatches("education"))
                found += v.Length;
            Check(n + "EnumerateMatches", "5", found.ToString());
        }

        // Only the backtracking engines: what NonBacktracking does not support.
        private static void Backtracking()
        {
            Check("backreference", Regex.IsMatch("abcabc", @"^(abc)\1$"));
            Check("lookahead", "foo", Regex.Match("foobar foobaz", @"foo(?=baz)").Value + (Regex.Match("foobar foobaz", @"foo(?=baz)").Index == 7 ? "" : "!"));
            Check("negative lookbehind", "2", Regex.Count("x1 y1 x1 z1", @"(?<!x)1").ToString());
            Check("lazy quantifier", "<a>", Regex.Match("<a><b>", "<.+?>").Value);
            Check("atomic group refuses to give back", !Regex.IsMatch("aaab", "(?>a+)ab"));
            Check("conditional", "yes|no", Regex.Replace("(1) 2", @"^(\()?\d+(?(1)\))", "yes") == "yes 2" ? "yes|no" : "?");
            Check("RightToLeft", "333", Regex.Match("1 22 333", @"\d+", RegexOptions.RightToLeft).Value);
            Check("captures of a repeated group", "a,b,c", string.Join(",", Captures(Regex.Match("abc", @"(\w)+").Groups[1])));
            Check("balancing group", Regex.IsMatch("(()())", @"^(?:(?<o>\()|(?<-o>\)))+(?(o)(?!))$"));
            Check("static cache: same pattern twice", Regex.IsMatch("x9", @"\d") && Regex.IsMatch("y8", @"\d"));
        }

        private static void Errors()
        {
            try
            {
                _ = new Regex("a(b");
                Check("unterminated group is RegexParseException", false);
            }
            catch (RegexParseException e)
            {
                Check("unterminated group is RegexParseException (" + e.Message + ")",
                      e.Error == RegexParseError.InsufficientClosingParentheses && e.Offset == 3);
            }

            try
            {
                Regex.Match(new string('a', 30) + "!", "^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50));
                Check("catastrophic pattern times out", false);
            }
            catch (RegexMatchTimeoutException)
            {
                Check("catastrophic pattern times out", true);
            }

            Check("NonBacktracking: the same pattern does not explode",
                  !Regex.IsMatch(new string('a', 30) + "!", "^(a+)+$", RegexOptions.NonBacktracking));

            try
            {
                _ = new Regex(@"(a)\1", RegexOptions.NonBacktracking);
                Check("NonBacktracking refuses a backreference", false);
            }
            catch (NotSupportedException)
            {
                Check("NonBacktracking refuses a backreference", true);
            }
        }

        // [GeneratedRegex] through the .NET 10 pack's generator: Okojo's patterns
        // (Date.parse, parseFloat) and a case-insensitive alternation.
        [GeneratedRegex(@"^(?<year>[+-]?\d{4,6})-(?<month>\d{2})-(?<day>\d{2})(?:T(?<hour>\d{2}):(?<minute>\d{2})(?::(?<second>\d{2})(?:\.(?<millisecond>\d{1,3}))?)?(?<offset>Z|[+-]\d{2}:\d{2})?)?$", RegexOptions.CultureInvariant)]
        private static partial Regex IsoDateTime();

        [GeneratedRegex(@"^[\+\-]?(Infinity|((([0-9]+(\.[0-9]*)?)|(\.[0-9]+))([eE][\+\-]?[0-9]+)?))")]
        private static partial Regex ParseFloat { get; }

        [GeneratedRegex("cat|dog|bird", RegexOptions.IgnoreCase)]
        private static partial Regex Animals();

        private static void Generated()
        {
            Match d = IsoDateTime().Match("2024-01-02T03:04:05.678Z");
            Check("generated: ISO date-time groups", "2024|01|02|03|04|05|678|Z",
                  d.Groups["year"].Value + "|" + d.Groups["month"].Value + "|" + d.Groups["day"].Value + "|" + d.Groups["hour"].Value + "|" +
                  d.Groups["minute"].Value + "|" + d.Groups["second"].Value + "|" + d.Groups["millisecond"].Value + "|" + d.Groups["offset"].Value);
            Check("generated: ISO date rejects a bad month", !IsoDateTime().IsMatch("2024-1-02"));
            Check("generated: parseFloat prefix", "-1.5e3", ParseFloat.Match("-1.5e3xyz").Value);
            Check("generated: IgnoreCase alternation", "DOG,Bird", string.Join(",", Values(Animals().Matches("a DOG and a Bird"))));
        }

        private static void Checks()
        {
            Check("Escape", @"a\.b\*c\ d", Regex.Escape("a.b*c d"));
            Check("Unescape", "a.b*c d", Regex.Unescape(@"a\.b\*c\ d"));
            Check("GetGroupNames", "0|1|word", string.Join("|", new Regex(@"(x)(?<word>\w+)").GetGroupNames()));
            Check("ToString is the pattern", @"\d+", new Regex(@"\d+").ToString());
            Check("Match.Success false on no match", !Regex.Match("abc", @"\d").Success);
            Check("Replace with MatchEvaluator", "a2b4", Regex.Replace("a1b2", @"\d", x => (int.Parse(x.Value) * 2).ToString()));
        }

        private static List<string> Values(MatchCollection matches)
        {
            var list = new List<string>();
            foreach (Match x in matches) list.Add(x.Value);
            return list;
        }

        private static List<string> Captures(Group g)
        {
            var list = new List<string>();
            foreach (Capture c in g.Captures) list.Add(c.Value);
            return list;
        }
    }
}
