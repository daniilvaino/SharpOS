using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UtilsApp
{
    // head, tail, grep, wc, sort, uniq. Messages of any type go through as they
    // came — the block itself, whatever follows still sees the type; bytes and
    // files go as lines. Nothing here parses text an object could have answered.
    internal static class Filters
    {
        private static bool Count(Dictionary<string, string> o, Options options, out long n)
        {
            n = 10;
            if (!o.TryGetValue("n", out string text)) return true;
            if (long.TryParse(text, out n) && n >= 0) return true;
            options.Wrong = "not a count: " + text;
            return false;
        }

        // head [-n N | -N] [file...]: stops reading after N.
        public static int Head(string[] args)
        {
            var options = new Options("head [-n N | -N] [file...] — the first N (10) messages or lines");
            Dictionary<string, string> o = options.Parse(args, "", "n");
            if (options.Failed || !Count(o, options, out long n)) return options.Refuse();

            var source = new Source(options.Words);
            using (var output = new Output(source))
            {
                for (long seen = 0; seen < n; seen++)
                {
                    Item item = source.Next();
                    if (item == null) break;
                    output.Send(item);
                }
                source.Close();
            }
            return source.Failed ? 1 : 0;
        }

        // tail [-n N | -N] [file...]: reads to the end, holding the last N.
        public static int Tail(string[] args)
        {
            var options = new Options("tail [-n N | -N] [file...] — the last N (10) messages or lines");
            Dictionary<string, string> o = options.Parse(args, "", "n");
            if (options.Failed || !Count(o, options, out long n)) return options.Refuse();

            var source = new Source(options.Words);
            var last = new Queue<Item>();
            using (var output = new Output(source))
            {
                Item item;
                while ((item = source.Next()) != null)
                {
                    last.Enqueue(item);
                    if (last.Count > n) last.Dequeue().Dispose();
                }
                while (last.Count > 0) output.Send(last.Dequeue());
                source.Close();
            }
            return source.Failed ? 1 : 0;
        }

        // grep [-i] [-v] [-F] [-c] [-n] [-q] [-k FIELD] PATTERN [file...]: an
        // object is matched by its screen line, or by a field with -k, and goes
        // on as itself. -c and -n make text. 0 found, 1 none, 2 an error.
        public static int Grep(string[] args)
        {
            var options = new Options("grep [-i] [-v] [-F] [-c] [-n] [-q] [-k FIELD] [-e] PATTERN [file...] — the messages or lines that match");
            Dictionary<string, string> o = options.Parse(args, "ivFcnq", "ke");
            if (options.Failed) return options.Refuse();
            string pattern;
            if (o.TryGetValue("e", out pattern)) { }
            else if (options.Words.Count > 0)
            {
                pattern = options.Words[0];
                options.Words.RemoveAt(0);
            }
            else
            {
                options.Wrong = "no pattern";
                return options.Refuse();
            }

            Regex regex;
            try
            {
                regex = new Regex(o.ContainsKey("F") ? Regex.Escape(pattern) : pattern,
                                  o.ContainsKey("i") ? RegexOptions.IgnoreCase : RegexOptions.None);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine("grep: " + e.Message);
                return 2;
            }
            bool invert = o.ContainsKey("v"), count = o.ContainsKey("c"), numbers = o.ContainsKey("n"), quiet = o.ContainsKey("q");
            KeyRule rule = o.TryGetValue("k", out string field) ? new KeyRule(field, false, false) : null;
            bool prefix = options.Words.Count > 1;

            var source = new Source(options.Words);
            long found = 0, at = 0;
            using (var output = new Output(source))
            {
                Item item;
                while ((item = source.Next()) != null)
                {
                    at++;
                    string text = rule != null ? rule.Of(item).Text : item.Text();
                    if (regex.IsMatch(text) == invert)
                    {
                        item.Dispose();
                        continue;
                    }
                    found++;
                    if (quiet)
                    {
                        item.Dispose();
                        break;
                    }
                    if (count)
                    {
                        item.Dispose();
                    }
                    else if (numbers || (prefix && item.Line != null))
                    {
                        string line = item.Text();
                        item.Dispose();
                        output.Line((prefix ? item.File + ":" : "") + (numbers ? at.ToString() + ":" : "") + line);
                    }
                    else
                    {
                        output.Send(item);
                    }
                }
                source.Close();
                if (count && !quiet) output.Line(found.ToString());
            }
            return source.Failed ? 2 : found > 0 ? 0 : 1;
        }

        // wc [-l] [-w] [-c] [file...]: messages or lines, words, bytes.
        public static int Wc(string[] args)
        {
            var options = new Options("wc [-l] [-w] [-c] [file...] — messages or lines, words, bytes of the text");
            Dictionary<string, string> o = options.Parse(args, "lwc", "");
            if (options.Failed) return options.Refuse();
            bool lines = o.ContainsKey("l"), words = o.ContainsKey("w"), bytes = o.ContainsKey("c");
            if (!lines && !words && !bytes) lines = words = bytes = true;

            var source = new Source(options.Words);
            long l = 0, w = 0, c = 0, tl = 0, tw = 0, tc = 0;
            int files = 0;
            string current = null;
            using (var output = new Output(source))
            {
                Item item;
                while ((item = source.Next()) != null)
                {
                    if (item.File != current && options.Words.Count > 0)
                    {
                        if (current != null) output.Line(Counts(lines, words, bytes, l, w, c, current));
                        tl += l; tw += w; tc += c;
                        l = w = c = 0;
                        current = item.File;
                        files++;
                    }
                    string text = item.Text();
                    if (!item.Unterminated) l++;
                    w += Words(text);
                    c += item.Line != null ? item.RawBytes : System.Text.Encoding.UTF8.GetByteCount(text) + 1;
                    item.Dispose();
                }
                source.Close();
                output.Line(Counts(lines, words, bytes, l, w, c, current));
                if (files > 1) output.Line(Counts(lines, words, bytes, tl + l, tw + w, tc + c, "total"));
            }
            return source.Failed ? 1 : 0;
        }

        private static string Counts(bool lines, bool words, bool bytes, long l, long w, long c, string name)
        {
            string text = "";
            if (lines) text += Pad(l);
            if (words) text += Pad(w);
            if (bytes) text += Pad(c);
            return name != null ? text + " " + name : text;
        }

        private static string Pad(long n) => n.ToString().PadLeft(8);

        private static long Words(string text)
        {
            long n = 0;
            bool inside = false;
            foreach (char ch in text)
            {
                bool space = char.IsWhiteSpace(ch);
                if (!space && !inside) n++;
                inside = !space;
            }
            return n;
        }

        // sort [-r] [-n] [-f] [-u] [-k KEY] [file...]: reads to the end, then
        // the blocks go on in order. KEY is a field of an object (a.b), its
        // numbers compared as numbers, or a column of a line. Stable.
        public static int Sort(string[] args)
        {
            var options = new Options("sort [-r] [-n] [-f] [-u] [-k FIELD|COLUMN] [file...] — in order, whole objects");
            Dictionary<string, string> o = options.Parse(args, "rnfu", "k");
            if (options.Failed) return options.Refuse();
            o.TryGetValue("k", out string keyName);
            var rule = new KeyRule(keyName, o.ContainsKey("n"), o.ContainsKey("f"));
            int direction = o.ContainsKey("r") ? -1 : 1;

            var source = new Source(options.Words);
            var rows = new List<Row>();
            using (var output = new Output(source))
            {
                Item item;
                while ((item = source.Next()) != null)
                    rows.Add(new Row { Item = item, Key = rule.Of(item), Index = rows.Count });
                source.Close();

                Row[] sorted = rows.ToArray();
                Array.Sort(sorted, (a, b) =>
                {
                    int c = rule.Compare(a.Key, b.Key) * direction;
                    return c != 0 ? c : a.Index - b.Index;
                });

                bool unique = o.ContainsKey("u");
                for (int i = 0; i < sorted.Length; i++)
                {
                    if (unique && i > 0 && rule.Compare(sorted[i - 1].Key, sorted[i].Key) == 0)
                        sorted[i].Item.Dispose();
                    else
                        output.Send(sorted[i].Item);
                }
            }
            return source.Failed ? 1 : 0;
        }

        private sealed class Row
        {
            public Item Item;
            public Key Key;
            public int Index;
        }

        // uniq [-c] [-d] [-u] [-i] [-k KEY] [file...]: runs of equal neighbours
        // as their first. -c makes text: the count, then the line.
        public static int Uniq(string[] args)
        {
            var options = new Options("uniq [-c] [-d] [-u] [-i] [-k FIELD|COLUMN] [file...] — one for each run of equal neighbours");
            Dictionary<string, string> o = options.Parse(args, "cdui", "k");
            if (options.Failed) return options.Refuse();
            o.TryGetValue("k", out string keyName);
            var rule = new KeyRule(keyName, false, o.ContainsKey("i"));
            bool counts = o.ContainsKey("c"), repeated = o.ContainsKey("d"), single = o.ContainsKey("u");

            var source = new Source(options.Words);
            using (var output = new Output(source))
            {
                Item first = null;
                Key firstKey = default;
                long run = 0;
                while (true)
                {
                    Item item = source.Next();
                    Key key = item != null ? rule.Of(item) : default;
                    if (first != null && item != null && rule.Compare(firstKey, key) == 0)
                    {
                        run++;
                        item.Dispose();
                        continue;
                    }
                    if (first != null)
                    {
                        if ((repeated && run < 2) || (single && run > 1))
                        {
                            first.Dispose();
                        }
                        else if (counts)
                        {
                            string line = first.Text();
                            first.Dispose();
                            output.Line(run.ToString().PadLeft(7) + " " + line);
                        }
                        else
                        {
                            output.Send(first);
                        }
                    }
                    if (item == null) break;
                    first = item;
                    firstKey = key;
                    run = 1;
                }
                source.Close();
            }
            return source.Failed ? 1 : 0;
        }
    }
}
