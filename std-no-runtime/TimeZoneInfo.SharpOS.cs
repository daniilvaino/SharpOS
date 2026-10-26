// System.TimeZoneInfo — the platform part: where time zones come from.
//
// Takes the place of upstream's TimeZoneInfo.Unix.cs + TimeZoneInfo.Unix.NonAndroid.cs
// + TimeZoneInfo.FullGlobalizationData*.cs (dotnet/runtime .NET 10 fork, MIT). The
// rest of TimeZoneInfo (AdjustmentRule, TransitionTime, CreateCustomTimeZone,
// ConvertTime*, GetUtcOffset, IsDaylightSavingTime, ...) is the BCL port in
// TimeZoneInfo*.cs.
//
// SharpOS cut: the zone source. There is no time-zone database here (no
// /usr/share/zoneinfo, no TZif reader, no ICU display names), so this is
// upstream's own time-zone-invariant mode (TimeZoneInfo.Invariant,
// DOTNET_SYSTEM_TIMEZONE_INVARIANT=1) under globalization-invariant mode:
//
//   * the local zone is UTC;
//   * the only system zone is UTC: GetSystemTimeZones returns it alone, and
//     FindSystemTimeZoneById throws TimeZoneNotFoundException for any other id;
//   * the UTC aliases upstream's Unix part recognises ("Etc/UTC", "Etc/UCT",
//     "Etc/Universal", "Etc/Zulu", "UCT", "Universal", "Zulu") resolve, through
//     upstream's alternative-id path, to a zone equal to UTC under that id;
//   * display names are upstream's invariant ones: "Coordinated Universal Time",
//     "(UTC) Coordinated Universal Time".
//
// Members below that are verbatim from TimeZoneInfo.Unix.cs say so.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace System
{
    public sealed partial class TimeZoneInfo
    {
        // SharpOS: from TimeZoneInfo.FullGlobalizationData.Unix.cs.
        private const string InvariantUtcStandardDisplayName = "Coordinated Universal Time";

        // Set fallback values using abbreviations, base offset, and id
        // These are expected in environments without time zone globalization data
        private string? _standardAbbrevName;
        private string? _daylightAbbrevName;

        // Verbatim from TimeZoneInfo.Unix.cs.
        // Handle UTC and its aliases per https://github.com/unicode-org/cldr/blob/master/common/bcp47/timezone.xml
        // Hard-coded because we need to treat all aliases of UTC the same even when globalization data is not available.
        // (This list is not likely to change.)
        private static bool IsUtcAlias(string id)
        {
            switch ((ushort)id[0])
            {
                case 69: // e
                case 101: // E
                    return string.Equals(id, "Etc/UTC", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(id, "Etc/UCT", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(id, "Etc/Universal", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(id, "Etc/Zulu", StringComparison.OrdinalIgnoreCase);
                case 85: // u
                case 117: // U
                    return string.Equals(id, "UCT", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(id, "Universal", StringComparison.OrdinalIgnoreCase);
                case 90: // z
                case 122: // Z
                    return string.Equals(id, "Zulu", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        // Verbatim from TimeZoneInfo.Unix.cs.
        // The TransitionTime fields are not used when AdjustmentRule.NoDaylightTransitions == true.
        // However, there are some cases in the past where DST = true, and the daylight savings offset
        // now equals what the current BaseUtcOffset is.  In that case, the AdjustmentRule.DaylightOffset
        // is going to be TimeSpan.Zero.  But we still need to return 'true' from AdjustmentRule.HasDaylightSaving.
        // To ensure we always return true from HasDaylightSaving, make a "special" dstStart that will make the logic
        // in HasDaylightSaving return true.
        private static readonly TransitionTime s_daylightRuleMarker = TransitionTime.CreateFixedDateRule(DateTime.MinValue.AddMilliseconds(2), 1, 1);

        // Truncate the date and the time to Milliseconds precision
        private static DateTime GetTimeOnlyInMillisecondsPrecision(DateTime input) => new DateTime((input.TimeOfDay.Ticks / TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond);

        /// <summary>
        /// Returns a cloned array of AdjustmentRule objects
        /// </summary>
        public AdjustmentRule[] GetAdjustmentRules()
        {
            // Verbatim from TimeZoneInfo.Unix.cs: custom zones carry rules as given, and the
            // Unix shape of GetAdjustmentRules is the one that matches this file's s_daylightRuleMarker.
            if (_adjustmentRules == null)
            {
                return Array.Empty<AdjustmentRule>();
            }

            // The rules we use in Unix care mostly about the start and end dates but don't fill the transition start and end info.
            // as the rules now is public, we should fill it properly so the caller doesn't have to know how we use it internally
            // and can use it as it is used in Windows

            List<AdjustmentRule> rulesList = new List<AdjustmentRule>(_adjustmentRules.Length);

            for (int i = 0; i < _adjustmentRules.Length; i++)
            {
                AdjustmentRule rule = _adjustmentRules[i];

                if (rule.NoDaylightTransitions &&
                    rule.DaylightTransitionStart != s_daylightRuleMarker &&
                    rule.DaylightDelta == TimeSpan.Zero && rule.BaseUtcOffsetDelta == TimeSpan.Zero)
                {
                    // This rule has no time transition, ignore it.
                    continue;
                }

                DateTime start = rule.DateStart.Kind == DateTimeKind.Utc ?
                            // At the daylight start we didn't start the daylight saving yet then we convert to Local time
                            // by adding the _baseUtcOffset to the UTC time
                            new DateTime(rule.DateStart.Ticks + _baseUtcOffset.Ticks, DateTimeKind.Unspecified) :
                            rule.DateStart;
                DateTime end = rule.DateEnd.Kind == DateTimeKind.Utc ?
                            // At the daylight saving end, the UTC time is mapped to local time which is already shifted by the daylight delta
                            // we calculate the local time by adding _baseUtcOffset + DaylightDelta to the UTC time
                            new DateTime(rule.DateEnd.Ticks + _baseUtcOffset.Ticks + rule.DaylightDelta.Ticks, DateTimeKind.Unspecified) :
                            rule.DateEnd;

                if (start.Year == end.Year || !rule.NoDaylightTransitions)
                {
                    // If the rule is covering only one year then the start and end transitions would occur in that year, we don't need to split the rule.
                    // Also, rule.NoDaylightTransitions be false in case the rule was created from a POSIX time zone string and having a DST transition. We can represent this in one rule too
                    TransitionTime startTransition = rule.NoDaylightTransitions ? TransitionTime.CreateFixedDateRule(GetTimeOnlyInMillisecondsPrecision(start), start.Month, start.Day) : rule.DaylightTransitionStart;
                    TransitionTime endTransition   = rule.NoDaylightTransitions ? TransitionTime.CreateFixedDateRule(GetTimeOnlyInMillisecondsPrecision(end), end.Month, end.Day) : rule.DaylightTransitionEnd;
                    rulesList.Add(AdjustmentRule.CreateAdjustmentRule(start.Date, end.Date, rule.DaylightDelta, startTransition, endTransition, rule.BaseUtcOffsetDelta));
                }
                else
                {
                    // For rules spanning more than one year. The time transition inside this rule would apply for the whole time spanning these years
                    // and not for partial time of every year.
                    // AdjustmentRule cannot express such rule using the DaylightTransitionStart and DaylightTransitionEnd because
                    // the DaylightTransitionStart and DaylightTransitionEnd express the transition for every year.
                    // We split the rule into more rules. The first rule will start from the start year of the original rule and ends at the end of the same year.
                    // The second split rule would cover the middle range of the original rule and ranging from the year start+1 to
                    // year end-1. The transition time in this rule would start from Jan 1st to end of December.
                    // The last split rule would start from the Jan 1st of the end year of the original rule and ends at the end transition time of the original rule.

                    // Add the first rule.
                    DateTime endForFirstRule = new DateTime(start.Year + 1, 1, 1).AddMilliseconds(-1); // At the end of the first year
                    TransitionTime startTransition = TransitionTime.CreateFixedDateRule(GetTimeOnlyInMillisecondsPrecision(start), start.Month, start.Day);
                    TransitionTime endTransition = TransitionTime.CreateFixedDateRule(GetTimeOnlyInMillisecondsPrecision(endForFirstRule), endForFirstRule.Month, endForFirstRule.Day);
                    rulesList.Add(AdjustmentRule.CreateAdjustmentRule(start.Date, endForFirstRule.Date, rule.DaylightDelta, startTransition, endTransition, rule.BaseUtcOffsetDelta));

                    // Check if there is range of years between the start and the end years
                    if (end.Year - start.Year > 1)
                    {
                        // Add the middle rule.
                        DateTime middleYearStart = new DateTime(start.Year + 1, 1, 1);
                        DateTime middleYearEnd   = new DateTime(end.Year, 1, 1).AddMilliseconds(-1);
                        startTransition = TransitionTime.CreateFixedDateRule(GetTimeOnlyInMillisecondsPrecision(middleYearStart), middleYearStart.Month, middleYearStart.Day);
                        endTransition = TransitionTime.CreateFixedDateRule(GetTimeOnlyInMillisecondsPrecision(middleYearEnd), middleYearEnd.Month, middleYearEnd.Day);
                        rulesList.Add(AdjustmentRule.CreateAdjustmentRule(middleYearStart.Date, middleYearEnd.Date, rule.DaylightDelta, startTransition, endTransition, rule.BaseUtcOffsetDelta));
                    }

                    // Add the end rule.
                    DateTime endYearStart = new DateTime(end.Year, 1, 1); // At the beginning of the last year
                    startTransition = TransitionTime.CreateFixedDateRule(GetTimeOnlyInMillisecondsPrecision(endYearStart), endYearStart.Month, endYearStart.Day);
                    endTransition = TransitionTime.CreateFixedDateRule(GetTimeOnlyInMillisecondsPrecision(end), end.Month, end.Day);
                    rulesList.Add(AdjustmentRule.CreateAdjustmentRule(endYearStart.Date, end.Date, rule.DaylightDelta, startTransition, endTransition, rule.BaseUtcOffsetDelta));
                }
            }

            return rulesList.ToArray();
        }

        // Verbatim from TimeZoneInfo.Unix.cs, the GlobalizationMode.Invariant branches (there is no ICU to ask).
        private string? PopulateDisplayName()
        {
            if (IsUtcAlias(Id))
                return GetUtcFullDisplayName(Id, StandardName);

            // Set fallback value using abbreviations, base offset, and id
            // These are expected in environments without time zone globalization data
            // SharpOS: upstream formats this with string.Create(null, stackalloc char[256], $"...{_baseUtcOffset:hh\\:mm}...");
            // the same text through TimeSpan.ToString, without the span interpolation handler.
            string? displayName = "(UTC" + (_baseUtcOffset >= TimeSpan.Zero ? '+' : '-') + _baseUtcOffset.ToString(@"hh\:mm", null) + ") " + _id;
            return displayName; // SharpOS cut: GetFullValueForDisplayNameField (ICU) - GlobalizationMode.Invariant
        }

        private string? PopulateStandardDisplayName()
        {
            if (IsUtcAlias(Id))
                return GetUtcStandardDisplayName();

            string? standardDisplayName = _standardAbbrevName;
            return standardDisplayName; // SharpOS cut: GetStandardDisplayName (ICU) - GlobalizationMode.Invariant
        }

        private string? PopulateDaylightDisplayName()
        {
            if (IsUtcAlias(Id))
                return StandardName;

            string? daylightDisplayName = _daylightAbbrevName ?? _standardAbbrevName;
            return daylightDisplayName; // SharpOS cut: GetDaylightDisplayName (ICU) - GlobalizationMode.Invariant
        }

        // From TimeZoneInfo.Unix.cs, the TimeZoneInfo.Invariant branch: UTC is the only system zone.
        private static void PopulateAllSystemTimeZones(CachedData cachedData)
        {
            Debug.Assert(Monitor.IsEntered(cachedData));

            cachedData._systemTimeZones ??= new Dictionary<string, TimeZoneInfo>(StringComparer.OrdinalIgnoreCase)
            {
                { UtcId, s_utcTimeZone }
            };

            // SharpOS cut: enumerating zone.tab (GetTimeZoneIds) - Invariant is always true here.
        }

        /// <summary>
        /// Helper function for retrieving the local system time zone.
        /// Assumes cachedData lock is taken.
        /// </summary>
        /// <returns>A new TimeZoneInfo instance.</returns>
        private static TimeZoneInfo GetLocalTimeZone(CachedData cachedData)
        {
            Debug.Assert(Monitor.IsEntered(cachedData));

            return Utc; // SharpOS: upstream's GetLocalTimeZoneCore, TimeZoneInfo.Invariant branch - the local zone is UTC
        }

        // Never reached: TryGetTimeZoneUsingId returns before it whenever Invariant is true.
        private static TimeZoneInfoResult TryGetTimeZoneFromLocalMachine(string id, out TimeZoneInfo? value, out Exception? e)
        {
            // SharpOS cut: reading TZif files (TryGetTimeZoneFromLocalMachineCore) - there is no zone database.
            value = null;
            e = null;
            return TimeZoneInfoResult.TimeZoneNotFoundException;
        }

        // Verbatim from TimeZoneInfo.Unix.cs.
        private static TimeZoneInfoResult TryGetTimeZone(string id, out TimeZoneInfo? timeZone, out Exception? e, CachedData cachedData)
            => TryGetTimeZone(id, false, out timeZone, out e, cachedData, alwaysFallbackToLocalMachine: true);

        // Verbatim from TimeZoneInfo.Unix.cs.
        // DateTime.Now fast path that avoids allocating an historically accurate TimeZoneInfo.Local and just creates a 1-year (current year) accurate time zone
        internal static TimeSpan GetDateTimeNowUtcOffsetFromUtc(DateTime time, out bool isAmbiguousLocalDst)
        {
            // Use the standard code path for Unix since there isn't a faster way of handling current-year-only time zones
            return GetUtcOffsetFromUtc(time, Local, out _, out isAmbiguousLocalDst);
        }

        // From TimeZoneInfo.FullGlobalizationData.Unix.cs, the GlobalizationMode.Invariant branch.
        // Helper function to get the standard display name for the UTC static time zone instance
        private static string GetUtcStandardDisplayName()
        {
            return InvariantUtcStandardDisplayName;
        }

        // Verbatim from TimeZoneInfo.FullGlobalizationData.Unix.cs.
        // Helper function to get the full display name for the UTC static time zone instance
        private static string GetUtcFullDisplayName(string timeZoneId, string standardDisplayName)
        {
            return "(UTC) " + standardDisplayName; // SharpOS: upstream interpolates $"(UTC) {standardDisplayName}"
        }

        // SharpOS: upstream asks ICU for the alternative (Windows <-> IANA) id. With no
        // database the only alternatives known are UTC's aliases, which upstream's Unix
        // part recognises by name (IsUtcAlias); they resolve to the UTC zone.
        private static string? GetAlternativeId(string id, out bool idIsIana)
        {
            idIsIana = true;
            return IsUtcAlias(id) && !string.Equals(id, UtcId, StringComparison.OrdinalIgnoreCase) ? UtcId : null;
        }

        // SharpOS: as TimeZoneInfo.MinimalGlobalizationData.cs - no IANA <-> Windows id mapping.
        private static bool TryConvertIanaIdToWindowsId(string ianaId, bool allocate, out string? windowsId)
        {
            windowsId = null;
            return false;
        }

        private static bool TryConvertWindowsIdToIanaId(string windowsId, string? region, bool allocate, out string? ianaId)
        {
            ianaId = null;
            return false;
        }
    }
}
