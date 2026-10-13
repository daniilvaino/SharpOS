// The .env format as Docker Compose reads it (step207): NAME=value,
// `export`, `#` comments, single quotes literal, double quotes with escapes
// and ${...} substitution (Template.cs). Written after compose-go's
// dotenv/parser.go and godotenv.go so that it accepts and refuses the same
// files with the same messages; their tests, carried over, run in
// ../Dotenv.Tests. compose-go's dotenv notice: LICENSE-dotenv (MIT, John
// Barton — a fork of joho/godotenv).
//
// Go strings are bytes and `range` walks runes; here a string is UTF-16 and
// the walk is by char — the same for every character of the Basic
// Multilingual Plane. Go's escapeSeqRegex and exportRegex are scans below;
// the escapes strconv.UnquoteChar decodes are decoded in place. Files are
// not read here: the kernel and the apps read them and pass the text.

using System;
using System.Collections.Generic;
using System.Text;

namespace SharpOS.Std.Dotenv
{
    /// <summary>A file the parser refuses: the line and why, in the original's words.</summary>
    public sealed class DotenvException : Exception
    {
        public DotenvException(string message) : base(message) { }
    }

    internal sealed class Parser
    {
        private const char CharComment = '#';
        private const char PrefixSingleQuote = '\'';
        private const char PrefixDoubleQuote = '"';

        private int _line = 1;

        internal int Line => _line;

        internal void Parse(string src, Dictionary<string, string> output, LookupFn lookupFn)
        {
            string cutset = src;
            lookupFn ??= NoLookupFn;
            while (true)
            {
                cutset = GetStatementStart(cutset);
                if (cutset == "")
                    break;   // reached end of file

                string key = LocateKeyName(cutset, out string left, out bool inherited);
                if (key.IndexOf(' ') >= 0)
                    throw new DotenvException("line " + _line.ToString() + ": key cannot contain a space");

                if (inherited)
                {
                    if (lookupFn(key, out string inheritedValue))
                        output[key] = inheritedValue;
                    cutset = left;
                    continue;
                }

                string value = ExtractVarValue(left, output, lookupFn, out left);
                output[key] = value;
                cutset = left;
            }
        }

        private static bool NoLookupFn(string name, out string value)
        {
            value = "";
            return false;
        }

        // getStatementStart returns the statement's beginning, skipping comment
        // lines and space.
        internal string GetStatementStart(string src)
        {
            while (true)
            {
                int pos = IndexOfNonSpaceChar(src);
                if (pos == -1) return "";

                src = src.Substring(pos);
                if (src[0] != CharComment) return src;

                // skip comment section
                pos = src.IndexOf('\n');
                if (pos == -1) return "";
                src = src.Substring(pos);
            }
        }

        // locateKeyName locates and parses the key name and returns the rest.
        private string LocateKeyName(string src, out string cutset, out bool inherited)
        {
            string key = "";
            inherited = false;
            // trim "export" and space at the beginning (exportRegex: ^export\s+)
            if (src.StartsWith("export", StringComparison.Ordinal) && src.Length > 6 && IsRegexSpace(src[6]))
                src = TrimLeftSpace(src.Substring(6));

            // locate the key name's end and validate it in a single loop
            int offset = 0;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (IsSpace(c)) continue;

                bool end = false;
                switch (c)
                {
                    case '=':
                    case ':':
                    case '\n':
                        // library also supports yaml-style value declaration
                        key = src.Substring(0, i);
                        offset = i + 1;
                        inherited = c == '\n';
                        end = true;
                        break;
                    case '_':
                    case '.':
                    case '-':
                    case '[':
                    case ']':
                        break;
                    default:
                        // variable name should match [A-Za-z0-9_.-]
                        if (char.IsLetter(c) || char.IsDigit(c) || char.IsSurrogate(c)) break;
                        int lineEnd = src.IndexOf('\n');
                        string first = lineEnd < 0 ? src : src.Substring(0, lineEnd);
                        throw new DotenvException("line " + _line.ToString() + ": unexpected character \"" + c + "\" in variable name \"" + first + "\"");
                }
                if (end) break;
            }

            if (src == "")
                throw new DotenvException("zero length string");

            if (inherited && key.IndexOf(' ') < 0)
                _line++;

            // trim whitespace
            key = key.TrimEnd();
            cutset = TrimLeftSpace(src.Substring(offset));
            return key;
        }

        // extractVarValue extracts the variable's value and returns the rest.
        private string ExtractVarValue(string src, Dictionary<string, string> envMap, LookupFn lookupFn, out string rest)
        {
            bool isQuoted = HasQuotePrefix(src, out char quote);
            if (!isQuoted)
            {
                // unquoted value - read until new line
                int newline = src.IndexOf('\n');
                string value = newline < 0 ? src : src.Substring(0, newline);
                rest = newline < 0 ? "" : src.Substring(newline + 1);
                _line++;

                // Remove inline comments on unquoted lines
                int comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0) value = value.Substring(0, comment);
                value = value.TrimEnd();
                string expanded = DotenvParser.ExpandVariables(value, envMap, lookupFn, out TemplateException error);
                if (error != null) throw error;
                return expanded;
            }

            bool previousCharIsEscape = false;
            // look up the quoted string's terminator
            var chars = new StringBuilder();
            for (int i = 1; i < src.Length; i++)
            {
                char c = src[i];
                if (c == '\n') _line++;
                if (c != quote)
                {
                    if (!previousCharIsEscape && c == '\\')
                    {
                        previousCharIsEscape = true;
                        continue;
                    }
                    if (previousCharIsEscape)
                    {
                        previousCharIsEscape = false;
                        chars.Append('\\');
                    }
                    chars.Append(c);
                    continue;
                }

                // skip escaped quote symbol (\" or \', depends on quote)
                if (previousCharIsEscape)
                {
                    previousCharIsEscape = false;
                    chars.Append(c);
                    continue;
                }

                // trim quotes
                string value = chars.ToString();
                if (quote == PrefixDoubleQuote)
                {
                    // expand standard shell escape sequences & then interpolate
                    // variables on the result
                    value = DotenvParser.ExpandVariables(ExpandEscapes(value), envMap, lookupFn, out TemplateException error);
                    if (error != null) throw error;
                }

                rest = src.Substring(i + 1);
                return value;
            }

            // return formatted error if quoted string is not terminated
            int valEndIndex = src.IndexOf('\n');
            if (valEndIndex == -1) valEndIndex = src.Length;
            throw new DotenvException("line " + _line.ToString() + ": unterminated quoted value " + src.Substring(0, valEndIndex));
        }

        // escapeSeqRegex `(\\(?:[abcfnrtv$"\\]|0\d{0,3}))`, each match decoded
        // as strconv.UnquoteChar would: `\$` becomes `$$` (the template's
        // literal dollar), `\0ddd` an octal byte, anything it cannot decode
        // stays as written.
        internal static string ExpandEscapes(string str)
        {
            if (str.IndexOf('\\') < 0) return str;
            var output = new StringBuilder(str.Length);
            int i = 0;
            while (i < str.Length)
            {
                char c = str[i];
                if (c != '\\' || i + 1 >= str.Length)
                {
                    output.Append(c);
                    i++;
                    continue;
                }
                char e = str[i + 1];
                switch (e)
                {
                    case 'a': output.Append('\a'); i += 2; continue;
                    case 'b': output.Append('\b'); i += 2; continue;
                    case 'c': output.Append("\\c"); i += 2; continue;   // matched, not a Go escape: kept
                    case 'f': output.Append('\f'); i += 2; continue;
                    case 'n': output.Append('\n'); i += 2; continue;
                    case 'r': output.Append('\r'); i += 2; continue;
                    case 't': output.Append('\t'); i += 2; continue;
                    case 'v': output.Append('\v'); i += 2; continue;
                    case '$': output.Append("$$"); i += 2; continue;
                    case '"': output.Append('"'); i += 2; continue;
                    case '\\': output.Append('\\'); i += 2; continue;
                    case '0':
                    {
                        int digits = 0;
                        while (digits < 3 && i + 2 + digits < str.Length && str[i + 2 + digits] >= '0' && str[i + 2 + digits] <= '9') digits++;
                        string match = str.Substring(i, 2 + digits);
                        // `\0ddd` → `\ddd`: an octal escape takes exactly three octal digits, up to \377.
                        int value = 0;
                        bool ok = digits == 3;
                        for (int d = 0; ok && d < 3; d++)
                        {
                            char o = str[i + 2 + d];
                            if (o > '7') ok = false;
                            else value = value * 8 + (o - '0');
                        }
                        if (ok && value <= 255) output.Append((char)value);
                        else output.Append(match);
                        i += match.Length;
                        continue;
                    }
                    default:
                        output.Append(c);
                        i++;
                        continue;
                }
            }
            return output.ToString();
        }

        private int IndexOfNonSpaceChar(string src)
        {
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (c == '\n') _line++;
                if (!char.IsWhiteSpace(c)) return i;
            }
            return -1;
        }

        // hasQuotePrefix reports whether the text starts with a single or double quote, and which.
        private static bool HasQuotePrefix(string src, out char quote)
        {
            quote = '\0';
            if (src == "") return false;
            if (src[0] == PrefixDoubleQuote || src[0] == PrefixSingleQuote)
            {
                quote = src[0];
                return true;
            }
            return false;
        }

        // isSpace: a space character but not a line break (unicode.IsSpace counts line breaks too).
        private static bool IsSpace(char r)
        {
            switch (r)
            {
                case '\t':
                case '\v':
                case '\f':
                case '\r':
                case ' ':
                case '\u0085':
                case '\u00A0':
                    return true;
            }
            return false;
        }

        // \s in Go's regexp: [\t\n\f\r ].
        private static bool IsRegexSpace(char c) => c == '\t' || c == '\n' || c == '\f' || c == '\r' || c == ' ';

        private static string TrimLeftSpace(string s)
        {
            int i = 0;
            while (i < s.Length && IsSpace(s[i])) i++;
            return i == 0 ? s : s.Substring(i);
        }
    }

    /// <summary>A lookup of a variable outside the file: false when it is not there.</summary>
    public delegate bool LookupFn(string name, out string value);

    /// <summary>The package's entry points (godotenv.go): text in, variables out.</summary>
    public static class DotenvParser
    {
        /// <summary>
        /// UnmarshalWithLookup: the variables of <paramref name="src"/>, in the
        /// order they first appear. <paramref name="lookupFn"/> answers names
        /// the file takes from outside (a line of a name alone, `$X` of a name
        /// it does not set). A malformed file throws DotenvException; a
        /// failed substitution throws the TemplateException.
        /// </summary>
        public static Dictionary<string, string> UnmarshalWithLookup(string src, LookupFn lookupFn = null)
        {
            var output = new Dictionary<string, string>();
            ParseWithLookup(src, output, lookupFn);
            return output;
        }

        /// <summary>
        /// parseWithLookup: the variables of <paramref name="src"/> into
        /// <paramref name="vars"/>, over what is there — how GetEnvFromFile
        /// reads one file after another into one map.
        /// </summary>
        public static void ParseWithLookup(string src, Dictionary<string, string> vars, LookupFn lookupFn = null)
        {
            // The original trims a UTF-8 BOM from the bytes; as text, U+FEFF.
            if (src.Length > 0 && src[0] == '\uFEFF') src = src.Substring(1);
            new Parser().Parse(src, vars, lookupFn);
        }

        /// <summary>expandVariables: the value substituted; on an error, the value as it was.</summary>
        internal static string ExpandVariables(string value, Dictionary<string, string> envMap, LookupFn lookupFn, out TemplateException error)
        {
            string result = TemplateSubstitution.Substitute(value, (string k, out string v) =>
            {
                if (lookupFn != null && lookupFn(k, out v)) return true;
                return envMap.TryGetValue(k, out v);
            }, out error);
            return error != null ? value : result;
        }
    }
}
