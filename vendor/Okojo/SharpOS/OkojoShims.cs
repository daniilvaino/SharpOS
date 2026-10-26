// SharpOS glue for Okojo (not upstream code): what it needs that std lacks.
//
// System.Text.RegularExpressions here is NOT the BCL's: our std has no .NET
// Regex yet (donext: "Разборщики BCL", Regex). Okojo uses it for eight fixed
// internal patterns ([GeneratedRegex]); this layer runs them through Okojo's
// own ECMAScript engine (Okojo.Text.RegularExpressions), whose syntax covers
// them, and supplies the bodies the source generator would have written.
// It goes away when std gets the real Regex.

using System.Collections.Generic;
using Okojo.Text.RegularExpressions;

namespace System.Text.RegularExpressions
{
    [Flags]
    public enum RegexOptions
    {
        None = 0,
        IgnoreCase = 1,
        Multiline = 2,
        ExplicitCapture = 4,
        Compiled = 8,
        Singleline = 16,
        IgnorePatternWhitespace = 32,
        RightToLeft = 64,
        ECMAScript = 256,
        CultureInvariant = 512,
        NonBacktracking = 1024,
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    public sealed class GeneratedRegexAttribute : Attribute
    {
        public GeneratedRegexAttribute(string pattern) => Pattern = pattern;
        public GeneratedRegexAttribute(string pattern, RegexOptions options) : this(pattern) => Options = options;
        public GeneratedRegexAttribute(string pattern, RegexOptions options, int matchTimeoutMilliseconds) : this(pattern, options) { }
        public string Pattern { get; }
        public RegexOptions Options { get; }
    }

    public class Regex
    {
        private readonly RegExp _regexp;

        public Regex(string pattern, RegexOptions options = RegexOptions.None)
        {
            var flags = "";
            if ((options & RegexOptions.IgnoreCase) != 0) flags += "i";
            if ((options & RegexOptions.Multiline) != 0) flags += "m";
            if ((options & RegexOptions.Singleline) != 0) flags += "s";
            _regexp = RegExp.Compile(pattern, flags);
        }

        public bool IsMatch(string input) => _regexp.IsMatch(input);

        public Match Match(string input) => Match(input, 0);

        private Match Match(string input, int start)
        {
            var captures = new CaptureRange[_regexp.RequiredCaptureCount];
            return _regexp.TryMatch(input, start, captures, out _)
                ? new Match(_regexp, input, captures)
                : System.Text.RegularExpressions.Match.Empty;
        }

        public MatchCollection Matches(string input)
        {
            var found = new List<Match>();
            var at = 0;
            while (at <= input.Length)
            {
                var m = Match(input, at);
                if (!m.Success) break;
                found.Add(m);
                at = m.Index + Math.Max(m.Length, 1);
            }
            return new MatchCollection(found);
        }
    }

    public class Group
    {
        internal Group(bool success, string value, int index, int length)
        {
            Success = success;
            Value = value;
            Index = index;
            Length = length;
        }

        public bool Success { get; }
        public string Value { get; }
        public int Index { get; }
        public int Length { get; }
    }

    public sealed class Match : Group
    {
        internal static readonly Match Empty = new(null, "", null);

        private readonly RegExp _regexp;
        private readonly string _input;
        private readonly CaptureRange[] _captures;

        internal Match(RegExp regexp, string input, CaptureRange[] captures)
            : base(
                captures is not null,
                captures is null ? "" : input.Substring(captures[0].Index, captures[0].Length),
                captures is null ? 0 : captures[0].Index,
                captures is null ? 0 : captures[0].Length)
        {
            _regexp = regexp;
            _input = input;
            _captures = captures;
        }

        public GroupCollection Groups => new(this);

        internal Group GroupAt(int index)
        {
            if (_captures is null || index < 0 || index >= _captures.Length || !_captures[index].Success)
                return new Group(false, "", 0, 0);
            var c = _captures[index];
            return new Group(true, _input.Substring(c.Index, c.Length), c.Index, c.Length);
        }

        internal Group GroupNamed(string name)
        {
            if (_captures is null) return new Group(false, "", 0, 0);
            foreach (var index in _regexp.GetCaptureIndices(name))
                if (_captures[index].Success)
                    return GroupAt(index);
            return new Group(false, "", 0, 0);
        }
    }

    public sealed class GroupCollection
    {
        private readonly Match _match;
        internal GroupCollection(Match match) => _match = match;
        public Group this[int index] => _match.GroupAt(index);
        public Group this[string name] => _match.GroupNamed(name);
    }

    public sealed class MatchCollection
    {
        private readonly List<Match> _matches;
        internal MatchCollection(List<Match> matches) => _matches = matches;
        public int Count => _matches.Count;
        public Match this[int index] => _matches[index];
    }
}

// The bodies [GeneratedRegex] would have generated, same patterns and options.
namespace Okojo.JavaScript.Execution
{
    using System.Text.RegularExpressions;

    public partial class Intrinsics
    {
        private static Regex? s_ecmaIsoDateTime, s_ecmaPartialDate, s_legacyLocalDateString, s_parseFloat, s_timeZone;

        private static partial Regex EcmaIsoDateTimeRegex() => s_ecmaIsoDateTime ??= new(
            @"^(?<year>[+-]?\d{4,6})-(?<month>\d{2})-(?<day>\d{2})(?:T(?<hour>\d{2}):(?<minute>\d{2})(?::(?<second>\d{2})(?:\.(?<millisecond>\d{1,3}))?)?(?<offset>Z|[+-]\d{2}:\d{2})?)?$",
            RegexOptions.CultureInvariant);

        private static partial Regex EcmaPartialDateRegex() => s_ecmaPartialDate ??= new(
            @"^(?<year>[+-]?\d{4,6})(?:-(?<month>\d{2})(?:-(?<day>\d{2}))?)?$",
            RegexOptions.CultureInvariant);

        private static partial Regex LegacyLocalDateStringRegex() => s_legacyLocalDateString ??= new(
            @"^(?<weekday>[A-Za-z]{3}) (?<monthName>[A-Za-z]{3}) (?<day>\d{2}) (?<year>[+-]?\d{4,6}) (?<hour>\d{2}):(?<minute>\d{2}):(?<second>\d{2}) GMT(?<sign>[+-])(?<offsetHour>\d{2})(?::?(?<offsetMinute>\d{2}))?(?: \(.+\))?$",
            RegexOptions.CultureInvariant);

        private static partial Regex ParseFloatRegex => s_parseFloat ??= new(
            @"^[\+\-]?(Infinity|((([0-9]+(\.[0-9]*)?)|(\.[0-9]+))([eE][\+\-]?[0-9]+)?))");

        private static partial Regex TimeZoneRegex() => s_timeZone ??= new(
            @"^[A-Za-z._+-]+(?:/[A-Za-z0-9._+-]+)+$", RegexOptions.CultureInvariant);
    }

    internal sealed partial class JsDurationFormatObject
    {
        private static Regex? s_durationPattern;

        private static partial Regex DurationPatternRegex() => s_durationPattern ??= new(
            @"^([+-])?P(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)(?:[.,](\d{1,9}))?H)?(?:(\d+)(?:[.,](\d{1,9}))?M)?(?:(\d+)(?:[.,](\d{1,9}))?S)?)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}

namespace Okojo.JavaScript.SourceMaps
{
    using System.Text.RegularExpressions;

    public sealed partial class SourceMapScriptLoader
    {
        private static Regex? s_sourceMappingUrl;

        private static partial Regex SourceMappingUrlRegex() => s_sourceMappingUrl ??= new(
            @"(?://[@#]\s*sourceMappingURL=(?<url>\S+)|/\*[#@]\s*sourceMappingURL=(?<url>[^*\r\n]+)\*/)",
            RegexOptions.CultureInvariant);
    }
}

namespace Okojo.Globalization
{
    using System.Text.RegularExpressions;

    public static partial class Locale
    {
        private static Regex? s_languageTag;

        private static partial Regex LanguageTagRegex() => s_languageTag ??= new(
            "^[a-zA-Z]{2,8}(?:-[a-zA-Z0-9]{1,8})*$", RegexOptions.CultureInvariant);
    }
}
