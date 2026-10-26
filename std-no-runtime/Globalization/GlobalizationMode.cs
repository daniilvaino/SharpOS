// The two switch holders the ported date/time code (DateTimeFormatInfo,
// DateTimeFormat, DateTimeParse, CultureData) asks. SharpOS, not BCL: in
// dotnet/runtime they read runtime configuration; here the answers are fixed.
//
//   * GlobalizationMode.Invariant is true: std has one culture, the invariant
//     one (Globalization.cs), so upstream's invariant-mode branches are the
//     ones that run.
//   * The LocalAppContextSwitches the date/time code reads are at their
//     defaults (false), as when nothing configures them.

namespace System.Globalization
{
    internal static partial class GlobalizationMode
    {
        internal static bool Invariant => true;
    }
}

namespace System
{
    internal static partial class LocalAppContextSwitches
    {
        internal static bool EnforceJapaneseEraYearRanges => false;
        internal static bool FormatJapaneseFirstYearAsANumber => false;
        internal static bool EnforceLegacyJapaneseDateParsing => false;
    }
}
