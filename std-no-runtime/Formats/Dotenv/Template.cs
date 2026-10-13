// The ${...} substitution of the .env format (step207), written after
// compose-go's template/template.go so that it behaves the same: the same
// rules, names and messages. compose-go's notice: LICENSE-compose-go,
// NOTICE-compose-go (Apache-2.0, The Compose Specification Authors).
//
// compose-go finds the substitutions with one regular expression; there is
// no Regex here, so Match scans for exactly what that pattern matches —
//     \$(?i:(?P<escaped>\$)|(?P<named>[_a-z][_a-z0-9]*)|{(?:(?P<braced>[_a-z][_a-z0-9]*(?::?[-+?](.*))?)}|(?P<invalid>)))
// the alternatives in their order, the name greedy, `:?` and the optional
// group preferred, `.*` greedy and not across a line break. No warnings for
// unset variables (compose-go logs them), no ExtractVariables (compose files).

using System;
using System.Text;

namespace SharpOS.Std.Dotenv
{
    /// <summary>Maps a variable name to its value; false when it is absent (as opposed to empty).</summary>
    public delegate bool Mapping(string name, out string value);

    /// <summary>A substitution rule: the value, whether it applied, an error.</summary>
    public delegate string SubstituteFunc(string substitution, Mapping mapping, out bool applied, out TemplateException error);

    /// <summary>Applied to each match: the replacement and an error.</summary>
    public delegate string ReplacementFunc(string substring, Mapping mapping, Config cfg, out TemplateException error);

    public abstract class TemplateException : Exception
    {
        protected TemplateException(string message) : base(message) { }
    }

    /// <summary>InvalidTemplateError: the template is not in a valid format.</summary>
    public sealed class InvalidTemplateException : TemplateException
    {
        public string Template { get; internal set; }

        public InvalidTemplateException(string template) : base("Invalid template: " + Quote(template)) => Template = template;

        public override string Message => "Invalid template: " + Quote(Template);

        // %#v of a Go string: in double quotes.
        private static string Quote(string s) => "\"" + (s ?? "") + "\"";
    }

    /// <summary>MissingRequiredError: a required variable has no value.</summary>
    public sealed class MissingRequiredException : TemplateException
    {
        public string Variable { get; }
        public string Reason { get; }

        public MissingRequiredException(string variable, string reason)
            : base(reason != ""
                ? "required variable " + variable + " is missing a value: " + reason
                : "required variable " + variable + " is missing a value")
        {
            Variable = variable;
            Reason = reason;
        }
    }

    public sealed class Config
    {
        internal SubstituteFunc SubstituteFunc;
        internal ReplacementFunc ReplacementFunc;
    }

    public static class TemplateSubstitution
    {
        private const string GroupEscaped = "escaped", GroupNamed = "named", GroupBraced = "braced";

        // ---- the pattern, scanned by hand ----

        private static bool IsNameStart(char c) => c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        private static bool IsNameChar(char c) => IsNameStart(c) || (c >= '0' && c <= '9');

        /// <summary>
        /// The pattern at <paramref name="at"/>: the length matched (0 for no
        /// match) and the one named group that took part, with its text.
        /// </summary>
        internal static int Match(string s, int at, out string group, out string text)
        {
            group = null;
            text = "";
            int n = s.Length;
            if (at >= n || s[at] != '$' || at + 1 >= n) return 0;
            char c = s[at + 1];
            if (c == '$')
            {
                group = GroupEscaped;
                text = "$";
                return 2;
            }
            if (IsNameStart(c))
            {
                int j = at + 2;
                while (j < n && IsNameChar(s[j])) j++;
                group = GroupNamed;
                text = s.Substring(at + 1, j - at - 1);
                return j - at;
            }
            if (c != '{') return 0;

            int k = at + 2;
            if (k < n && IsNameStart(s[k]))
            {
                int nameEnd = k + 1;
                while (nameEnd < n && IsNameChar(s[nameEnd])) nameEnd++;
                // The name greedy, giving back one character at a time.
                for (int e = nameEnd; e > k; e--)
                {
                    int end = OptionalThenBrace(s, e);
                    if (end < 0 && e < n && s[e] == '}') end = e + 1;
                    if (end < 0) continue;
                    group = GroupBraced;
                    text = s.Substring(k, end - 1 - k);
                    return end - at;
                }
            }
            // (?P<invalid>): "${" alone.
            group = "invalid";
            text = "";
            return 2;
        }

        // (?::?[-+?](.*)) then '}': the end after that brace, or -1. `:?`
        // greedy (with the colon first), `.*` greedy up to the line's end.
        private static int OptionalThenBrace(string s, int e)
        {
            int n = s.Length;
            for (int colon = 1; colon >= 0; colon--)
            {
                int op = e;
                if (colon == 1)
                {
                    if (op >= n || s[op] != ':') continue;
                    op++;
                }
                if (op >= n || (s[op] != '-' && s[op] != '+' && s[op] != '?')) continue;
                int from = op + 1;
                int lineEnd = s.IndexOf('\n', from);
                if (lineEnd < 0) lineEnd = n;
                int brace = lineEnd > from ? s.LastIndexOf('}', lineEnd - 1, lineEnd - from) : -1;
                if (brace >= from) return brace + 1;
            }
            return -1;
        }

        /// <summary>The first match from <paramref name="from"/> on: its start, or -1.</summary>
        private static int Find(string s, int from, out int length, out string group, out string text)
        {
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] != '$') continue;
                length = Match(s, i, out group, out text);
                if (length > 0) return i;
            }
            length = 0;
            group = null;
            text = "";
            return -1;
        }

        // ---- template.go ----

        /// <summary>SubstituteWithOptions: every match replaced; the result and the first error.</summary>
        public static string SubstituteWithOptions(string template, Mapping mapping, Config cfg, out TemplateException returnErr)
        {
            returnErr = null;
            cfg ??= new Config();
            cfg.ReplacementFunc ??= DefaultReplacementFunc;

            var result = new StringBuilder();
            int copied = 0;
            int at = 0;
            while (true)
            {
                int start = Find(template, at, out int length, out _, out _);
                if (start < 0) break;
                result.Append(template, copied, start - copied);
                string substring = template.Substring(start, length);
                string replacement = cfg.ReplacementFunc(substring, mapping, cfg, out TemplateException err);
                if (err != null)
                {
                    // Add the template for template errors
                    if (err is InvalidTemplateException invalid && string.IsNullOrEmpty(invalid.Template))
                        invalid.Template = template;
                    // Save the first error to be returned
                    returnErr ??= err;
                }
                result.Append(replacement);
                copied = at = start + length;
            }
            result.Append(template, copied, template.Length - copied);
            return result.ToString();
        }

        public static string DefaultReplacementFunc(string substring, Mapping mapping, Config cfg, out TemplateException error)
            => DefaultReplacementAppliedFunc(substring, mapping, cfg, out _, out error);

        public static string DefaultReplacementAppliedFunc(string substring, Mapping mapping, Config cfg, out bool applied, out TemplateException error)
        {
            applied = false;
            error = null;
            SubstituteFunc subsFunc = cfg.SubstituteFunc;
            if (subsFunc == null) GetSubstitutionFunctionForTemplate(substring, out subsFunc);

            int closingBraceIndex = GetFirstBraceClosingIndex(substring);
            string rest = "";
            if (closingBraceIndex > -1)
            {
                rest = substring.Substring(closingBraceIndex + 1);
                substring = substring.Substring(0, closingBraceIndex + 1);
            }

            Find(substring, 0, out _, out string group, out string text);
            if (group == GroupEscaped)
            {
                applied = true;
                return text;
            }

            bool braced = false;
            string substitution = group == GroupNamed ? text : "";
            if (substitution == "")
            {
                substitution = group == GroupBraced ? text : "";
                braced = true;
            }

            if (substitution == "")
            {
                error = new InvalidTemplateException("");
                return "";
            }

            if (braced)
            {
                string value = subsFunc(substitution, mapping, out bool done, out TemplateException err);
                if (err != null)
                {
                    error = err;
                    return "";
                }
                if (done)
                {
                    string interpolatedNested = SubstituteWith(rest, mapping, null, out err);
                    if (err != null)
                    {
                        error = err;
                        return "";
                    }
                    applied = true;
                    return value + interpolatedNested;
                }
            }

            applied = mapping(substitution, out string found);
            return applied ? found : "";
        }

        /// <summary>SubstituteWith: with an additional substitute function.</summary>
        public static string SubstituteWith(string template, Mapping mapping, SubstituteFunc subsFunc, out TemplateException error)
            => SubstituteWithOptions(template, mapping, new Config { SubstituteFunc = subsFunc }, out error);

        /// <summary>Substitute: variables in the string with their values.</summary>
        public static string Substitute(string template, Mapping mapping, out TemplateException error)
            => SubstituteWith(template, mapping, null, out error);

        public static string SubstituteWithReplacement(string template, Mapping mapping, ReplacementFunc replacement, out TemplateException error)
            => SubstituteWithOptions(template, mapping, new Config { ReplacementFunc = replacement }, out error);

        /// <summary>The rule whose operator comes first in the template (the sort in the original).</summary>
        internal static string GetSubstitutionFunctionForTemplate(string template, out SubstituteFunc func)
        {
            string[] symbols = { ":?", "?", ":-", "-", ":+", "+" };
            SubstituteFunc[] funcs =
            {
                RequiredErrorWhenEmptyOrUnset, RequiredErrorWhenUnset,
                DefaultWhenEmptyOrUnset, DefaultWhenUnset,
                DefaultWhenNotEmpty, DefaultWhenSet,
            };
            int best = 0;
            int bestIndex = template.IndexOf(symbols[0], StringComparison.Ordinal);
            for (int i = 1; i < symbols.Length; i++)
            {
                int index = template.IndexOf(symbols[i], StringComparison.Ordinal);
                if (index < 0) continue;
                // An earlier position wins; an equal one keeps the earlier entry (":?" before "?").
                if (bestIndex < 0 || index < bestIndex)
                {
                    best = i;
                    bestIndex = index;
                }
            }
            func = funcs[best];
            return symbols[best];
        }

        internal static int GetFirstBraceClosingIndex(string s)
        {
            int openVariableBraces = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '}')
                {
                    openVariableBraces--;
                    if (openVariableBraces == 0) return i;
                }
                if (s[i] == '{')
                {
                    openVariableBraces++;
                    i++;
                }
            }
            return -1;
        }

        // Soft default (fall back if unset or empty)
        private static string DefaultWhenEmptyOrUnset(string substitution, Mapping mapping, out bool applied, out TemplateException error)
            => WithDefaultWhenAbsence(substitution, mapping, true, out applied, out error);

        // Hard default (fall back if-and-only-if empty)
        private static string DefaultWhenUnset(string substitution, Mapping mapping, out bool applied, out TemplateException error)
            => WithDefaultWhenAbsence(substitution, mapping, false, out applied, out error);

        private static string DefaultWhenNotEmpty(string substitution, Mapping mapping, out bool applied, out TemplateException error)
            => WithDefaultWhenPresence(substitution, mapping, true, out applied, out error);

        private static string DefaultWhenSet(string substitution, Mapping mapping, out bool applied, out TemplateException error)
            => WithDefaultWhenPresence(substitution, mapping, false, out applied, out error);

        private static string RequiredErrorWhenEmptyOrUnset(string substitution, Mapping mapping, out bool applied, out TemplateException error)
            => WithRequired(substitution, mapping, ":?", true, out applied, out error);

        private static string RequiredErrorWhenUnset(string substitution, Mapping mapping, out bool applied, out TemplateException error)
            => WithRequired(substitution, mapping, "?", false, out applied, out error);

        private static string WithDefaultWhenPresence(string substitution, Mapping mapping, bool notEmpty, out bool applied, out TemplateException error)
        {
            applied = false;
            error = null;
            string sep = notEmpty ? ":+" : "+";
            if (substitution.IndexOf(sep, StringComparison.Ordinal) < 0) return "";
            Partition(substitution, sep, out string name, out string defaultValue);
            bool ok = mapping(name, out string value);
            applied = true;
            if (ok && (!notEmpty || value != ""))
            {
                string substituted = Substitute(defaultValue, mapping, out error);
                if (error != null)
                {
                    applied = false;
                    return "";
                }
                return substituted;
            }
            return ok ? value : "";
        }

        private static string WithDefaultWhenAbsence(string substitution, Mapping mapping, bool emptyOrUnset, out bool applied, out TemplateException error)
        {
            applied = false;
            error = null;
            string sep = emptyOrUnset ? ":-" : "-";
            if (substitution.IndexOf(sep, StringComparison.Ordinal) < 0) return "";
            Partition(substitution, sep, out string name, out string defaultValue);
            bool ok = mapping(name, out string value);
            applied = true;
            if (!ok || (emptyOrUnset && value == ""))
            {
                string substituted = Substitute(defaultValue, mapping, out error);
                if (error != null)
                {
                    applied = false;
                    return "";
                }
                return substituted;
            }
            return value;
        }

        // valid: false for "?" (any value will do), true for ":?" (not empty).
        private static string WithRequired(string substitution, Mapping mapping, string sep, bool notEmpty, out bool applied, out TemplateException error)
        {
            applied = false;
            error = null;
            if (substitution.IndexOf(sep, StringComparison.Ordinal) < 0) return "";
            Partition(substitution, sep, out string name, out string errorMessage);
            bool ok = mapping(name, out string value);
            if (!ok || (notEmpty && value == ""))
            {
                string reason = Substitute(errorMessage, mapping, out TemplateException err);
                if (err != null)
                {
                    error = err;
                    return "";
                }
                applied = true;
                error = new MissingRequiredException(name, reason);
                return "";
            }
            applied = true;
            return value;
        }

        // Split the string at the first occurrence of sep: the part before
        // it, and the part after. Without it, the string and "".
        private static void Partition(string s, string sep, out string before, out string after)
        {
            int at = s.IndexOf(sep, StringComparison.Ordinal);
            if (at < 0)
            {
                before = s;
                after = "";
                return;
            }
            before = s.Substring(0, at);
            after = s.Substring(at + sep.Length);
        }
    }
}
