// What Terminal.Gui needed NStack for, expressed on plain strings.
//
// The library was written against NStack's ustring because .NET had no rune
// type when it was written: it targets netstandard2.0 and net472, and a string
// there could not tell you how many codepoints it held or how many terminal
// columns it would occupy. Both questions matter to a layout engine, so the
// library took a whole string type to get them answered.
//
// We have neither problem, so ustring is gone from the port entirely — the
// library's sources now say `string`, and the two questions are answered here
// instead. Rune stays: a character is still not a cell, and every truncation
// and padding decision depends on knowing the difference.
//
// The width calculation delegates to XtermSharp's table, which is the same
// wcwidth data NStack carries with one difference that matters: NStack reads
// table[max] after being handed the row COUNT, so it walks off the end on CJK.
// We hit that once already and fixed it there (step144); using the fixed copy
// rather than porting the broken one is the entire reason this file is short.

using System;
using System.Collections.Generic;
using System.Text;

namespace Terminal.Gui
{
    /// <summary>
    /// Building strings out of runes — what ustring.Make used to do.
    /// </summary>
    // Named RuneText, not Text: Terminal.Gui has a Text property on View and
    // TextFormatter, and a static class of the same name loses to it in lookup —
    // `Text.Make (...)` inside those types silently became `this.Text.Make`.
    public static class RuneText
    {
        public static string Make(string s) => s ?? string.Empty;

        public static string Make(char ch) => ch.ToString();

        public static string Make(Rune rune) => rune.ToString();

        public static string Make(int rune) => new Rune((uint)rune).ToString();

        public static string Make(byte[] utf8) =>
            utf8 == null ? string.Empty : Encoding.UTF8.GetString(utf8);

        public static string Make(IEnumerable<Rune> runes)
        {
            if (runes == null) return string.Empty;

            var sb = new StringBuilder();
            foreach (Rune r in runes) sb.Append(r.ToString());
            return sb.ToString();
        }
    }

    /// <summary>
    /// UTF-8 encoding, for the one place that genuinely wants bytes: writing
    /// text out to a stream.
    /// </summary>
    /// <remarks>
    /// Note the contrast with Rune.RuneLen, which counts UTF-16 units because
    /// it produces string indices. Here the unit really is the byte, because
    /// the destination is a byte stream — the two are not in conflict, they
    /// answer different questions, and confusing them is exactly the bug this
    /// port had to avoid.
    /// </remarks>
    public static class Utf8
    {
        /// <summary>How many bytes this rune takes in UTF-8.</summary>
        public static int RuneLen(Rune rune) =>
            rune.Value < 0x80 ? 1 : rune.Value < 0x800 ? 2 : rune.Value < 0x10000 ? 3 : 4;

        /// <summary>
        /// Writes the rune at <paramref name="offset"/> and returns how many
        /// bytes it took.
        /// </summary>
        public static int EncodeRune(Rune rune, byte[] destination, int offset)
        {
            uint v = rune.Value;

            if (v < 0x80)
            {
                destination[offset] = (byte)v;
                return 1;
            }

            if (v < 0x800)
            {
                destination[offset] = (byte)(0xC0 | (v >> 6));
                destination[offset + 1] = (byte)(0x80 | (v & 0x3F));
                return 2;
            }

            if (v < 0x10000)
            {
                destination[offset] = (byte)(0xE0 | (v >> 12));
                destination[offset + 1] = (byte)(0x80 | ((v >> 6) & 0x3F));
                destination[offset + 2] = (byte)(0x80 | (v & 0x3F));
                return 3;
            }

            destination[offset] = (byte)(0xF0 | (v >> 18));
            destination[offset + 1] = (byte)(0x80 | ((v >> 12) & 0x3F));
            destination[offset + 2] = (byte)(0x80 | ((v >> 6) & 0x3F));
            destination[offset + 3] = (byte)(0x80 | (v & 0x3F));
            return 4;
        }
    }

    /// <summary>
    /// The two things a layout engine asks of text that a string does not
    /// answer for itself.
    /// </summary>
    public static class TextExtensions
    {
        extension(string s)
        {
            /// <summary>
            /// Codepoints, not UTF-16 units. The two differ exactly where a
            /// surrogate pair appears, and counting units there would report
            /// one character as two.
            /// </summary>
            public int RuneCount
            {
                get
                {
                    if (s == null) return 0;

                    int count = 0;
                    for (int i = 0; i < s.Length; i++)
                    {
                        if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length
                            && char.IsLowSurrogate(s[i + 1]))
                            i++;
                        count++;
                    }
                    return count;
                }
            }

            /// <summary>
            /// Terminal columns. Wide characters take two, combining marks take
            /// none, and this is the number every padding decision needs — the
            /// character count is the wrong one and looks right in English.
            /// </summary>
            public int ConsoleWidth
            {
                get
                {
                    if (s == null) return 0;

                    int width = 0;
                    foreach (Rune r in s.ToRunes())
                        width += r.ColumnWidth();
                    return width;
                }
            }

            /// <summary>
            /// The string's codepoints, in order. An array rather than a lazy
            /// sequence, as NStack's was: callers index it and ask for its
            /// Length, and a sequence would also be re-walked on every use.
            /// </summary>
            public Rune[] ToRunes()
            {
                if (s == null) return new Rune[0];

                var list = new List<Rune>();
                for (int i = 0; i < s.Length; i++)
                {
                    if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length
                        && char.IsLowSurrogate(s[i + 1]))
                    {
                        list.Add(new Rune((uint)char.ConvertToUtf32(s[i], s[i + 1])));
                        i++;
                    }
                    else
                    {
                        list.Add(new Rune(s[i]));
                    }
                }
                return list.ToArray();
            }

            /// <summary>
            /// The codepoints as a list. NStack had both this and ToRunes, and
            /// the library uses each where it fits — a list where it edits, an
            /// array where it reads.
            /// </summary>
            public List<Rune> ToRuneList()
            {
                var list = new List<Rune>();
                foreach (Rune r in s.ToRunes()) list.Add(r);
                return list;
            }

            /// <summary>
            /// The half-open range [start, end), with a null end meaning "to
            /// the end". This is ustring's two-index slice, which a string has
            /// no operator for — the eight call sites now say .Slice (a, b)
            /// instead of [a, b].
            ///
            /// Indices are UTF-16 units, matching Rune.RuneLen, which is what
            /// produces them.
            /// </summary>
            public string Slice(int start, int? end)
            {
                if (s == null) return string.Empty;

                int from = start < 0 ? 0 : start > s.Length ? s.Length : start;
                int to = end == null ? s.Length : end.Value;
                if (to > s.Length) to = s.Length;
                if (to <= from) return string.Empty;

                return s.Substring(from, to - from);
            }

            /// <summary>
            /// Where the codepoint first appears, as a UTF-16 index, or -1.
            /// </summary>
            public int IndexOf(Rune rune)
            {
                if (s == null) return -1;

                for (int i = 0; i < s.Length; i++)
                {
                    uint value = s[i];
                    if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length
                        && char.IsLowSurrogate(s[i + 1]))
                        value = (uint)char.ConvertToUtf32(s[i], s[i + 1]);

                    if (value == rune.Value) return i;
                }
                return -1;
            }

            /// <summary>
            /// A substring counted in CODEPOINTS, not UTF-16 units — which is
            /// what the callers mean, since they got the numbers from RuneCount.
            /// The two agree until a surrogate pair appears, and then only this
            /// one is right.
            /// </summary>
            public string RuneSubstring(int start, int count)
            {
                if (s == null) return string.Empty;

                Rune[] runes = s.ToRunes();
                if (start < 0) start = 0;
                if (start >= runes.Length) return string.Empty;
                if (count > runes.Length - start) count = runes.Length - start;
                if (count <= 0) return string.Empty;

                var sb = new StringBuilder();
                for (int i = start; i < start + count; i++)
                    sb.Append(runes[i].ToString());
                return sb.ToString();
            }

            /// <summary>Does this string contain the given codepoint?</summary>
            public bool Contains(Rune rune)
            {
                if (s == null) return false;

                foreach (Rune r in s.ToRunes())
                    if (r.Value == rune.Value) return true;
                return false;
            }

            /// <summary>Empty in the ustring sense: null counts as empty.</summary>
            public bool IsEmpty => s == null || s.Length == 0;

            /// <summary>
            /// Trims whitespace at both ends. NStack spelled Trim this way and
            /// the library's calls kept the name.
            /// </summary>
            public string TrimSpace() => s == null ? string.Empty : s.Trim();
        }
    }
}
