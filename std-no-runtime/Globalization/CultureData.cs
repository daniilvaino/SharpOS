// Ported from dotnet/runtime (.NET 10 fork, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Globalization/CultureData.cs
//
// The invariant slice: the date/time members DateTimeFormatInfo reads, with the
// invariant culture's data exactly as upstream's CreateCultureWithInvariantData
// sets it. Globalization in std is invariant-only (Globalization.cs), so every
// culture's data is CultureData.Invariant and each getter is upstream's
// GlobalizationMode.Invariant branch.
//
// SharpOS cuts:
//   * every field and member not on the date/time path (identity, language,
//     region, numbers, currency, text info, code pages) - NumberFormatInfo and
//     CultureInfo carry their own invariant data in std;
//   * GetCultureData / CreateCultureData / the culture cache and every ICU/NLS
//     loader (GetLocaleInfoCore, GetTimeFormatsCore, CalendarData.GetCalendarsCore,
//     user overrides) - there is no locale database;
//   * Invariant is a lazily initialised static (upstream: `field ??=`), as upstream.

using System.Diagnostics;

namespace System.Globalization
{
    internal sealed partial class CultureData
    {
        private const int undef = -1;

        // Override flag
        private string _sRealName = null!; // Name you passed in (ie: en-US, en, or de-DE_phoneb). Initialized by helper called during initialization.
        #pragma warning disable CS0414 // a field of the BCL original, kept with the port; what uses it was cut
        private string? _sWindowsName; // Name OS thinks the object is (ie: de-DE_phoneb, or en-US (even if en was passed in))
        #pragma warning restore CS0414

        // Identity
        private string? _sName; // locale name (ie: en-us, NO sort info, but could be neutral)

        // Language
        private string? _sISO639Language; // ISO 639 Language Name

        // Time
        private string? _sAM1159; // (user can override) AM designator
        private string? _sPM2359; // (user can override) PM designator
        private string? _sTimeSeparator;
        private string[]? _saLongTimes; // (user can override) time format
        private string[]? _saShortTimes; // (user can override) short time format

        // Calendar specific data
        private int _iFirstDayOfWeek = undef; // (user can override) first day of week (gregorian really)
        private int _iFirstWeekOfYear = undef; // (user can override) first week of year (gregorian really)
        private CalendarId[]? _waCalendars; // all available calendar type(s).  The first one is the default calendar

        #pragma warning disable CS0414 // a field of the BCL original, kept with the port; what uses it was cut
        private bool _bUseOverrides; // use user overrides? this depends on user setting and if is user default locale.
        #pragma warning restore CS0414
        private bool _bUseOverridesUserSetting; // the setting the user requested for.
        #pragma warning disable CS0414 // a field of the BCL original, kept with the port; what uses it was cut
        private bool _bNeutral; // Flags for the culture (ie: neutral or not right now)
        #pragma warning restore CS0414

        private static CultureData? s_Invariant;

        private static CultureData CreateCultureWithInvariantData()
        {
            // Make a new culturedata
            CultureData invariant = new CultureData();

            // Basics
            // Note that we override the resources since this IS NOT supposed to change (by definition)
            invariant._bUseOverrides = false;
            invariant._bUseOverridesUserSetting = false;
            invariant._sRealName = "";                     // Name you passed in (ie: en-US, en, or de-DE_phoneb)
            invariant._sWindowsName = "";                     // Name OS thinks the object is (ie: de-DE_phoneb, or en-US (even if en was passed in))

            // Identity
            invariant._sName = "";                     // locale name (ie: en-us)
            invariant._bNeutral = false;                   // Flags for the culture (ie: neutral or not right now)

            // Language
            invariant._sISO639Language = "iv";                   // ISO 639 Language Name

            // SharpOS cut: region, numbers, percent, currency, misc - not on the date/time path.

            // Time
            invariant._sTimeSeparator = ":";
            invariant._sAM1159 = "AM";                   // AM designator
            invariant._sPM2359 = "PM";                   // PM designator
            invariant._saLongTimes = ["HH:mm:ss"];                             // time format
            invariant._saShortTimes = ["HH:mm", "hh:mm tt", "H:mm", "h:mm tt"]; // short time format

            // Calendar specific data
            invariant._iFirstDayOfWeek = 0;                      // first day of week
            invariant._iFirstWeekOfYear = 0;                      // first week of year

            // Calendar information is expensive size-wise, especially for AOT.
            // CalendarData.Invariant is special cased in _waCalendars and _calendars
            // accessors and gets initialized lazily.

            // SharpOS cut: text information, LCID and code pages.

            return invariant;
        }

        /// <summary>
        /// Build our invariant information
        /// We need an invariant instance, which we build hard-coded
        /// </summary>
        internal static CultureData Invariant => s_Invariant ??= CreateCultureWithInvariantData(); // SharpOS: upstream uses the `field` keyword

        // SharpOS cut: GetCultureData(string, bool), GetCultureData(int, bool), CreateCultureData,
        // NormalizeCultureName and the culture cache - the only culture is the invariant one.

        /// <summary>
        /// The real name used to construct the locale (ie: de-DE_phoneb)
        /// </summary>
        internal string CultureName
        {
            get
            {
                Debug.Assert(_sRealName != null, "[CultureData.CultureName] Expected _sRealName to be populated by already");
                // since windows doesn't know about zh-CHS and zh-CHT,
                // we leave sRealName == zh-Hanx but we still need to
                // pretend that it was zh-CHX.
                return _sName is "zh-CHS" or "zh-CHT" ? _sName : _sRealName;
            }
        }

        /// <summary>
        /// Did the user request to use overrides?
        /// </summary>
        internal bool UseUserOverride => _bUseOverridesUserSetting;

        internal string TwoLetterISOLanguageName => _sISO639Language!; // SharpOS: upstream falls back to GetLocaleInfoCore

        internal bool IsInvariantCulture => string.IsNullOrEmpty(_sName);

        /// <summary>
        /// AM designator
        /// (user can override)
        /// </summary>
        internal string AMDesignator => _sAM1159!; // SharpOS: upstream falls back to GetLocaleInfoCoreUserOverride

        /// <summary>
        /// PM designator
        /// (user can override)
        /// </summary>
        internal string PMDesignator => _sPM2359!; // SharpOS: upstream falls back to GetLocaleInfoCoreUserOverride

        /// <summary>
        /// time format
        /// (user can override)
        /// </summary>
        internal string[] LongTimes => _saLongTimes!; // SharpOS: upstream's GlobalizationMode.Invariant branch

        /// <summary>
        /// Short times (derived from long times format)
        /// (user can override)
        /// </summary>
        internal string[] ShortTimes => _saShortTimes!; // SharpOS: upstream's GlobalizationMode.Invariant branch

        /// <summary>
        /// first day of week
        /// (user can override)
        /// </summary>
        internal int FirstDayOfWeek => _iFirstDayOfWeek; // SharpOS: upstream's GlobalizationMode.Invariant branch

        /// <summary>
        /// first week of year
        /// (user can override)
        /// </summary>
        internal int CalendarWeekRule => _iFirstWeekOfYear; // SharpOS: always set for the invariant culture

        /// <summary>
        /// (user can override default only) short date format
        /// </summary>
        internal string[] ShortDates(CalendarId calendarId)
        {
            return GetCalendar(calendarId).saShortDates;
        }

        /// <summary>
        /// long date format
        /// (user can override default only)
        /// </summary>
        internal string[] LongDates(CalendarId calendarId)
        {
            return GetCalendar(calendarId).saLongDates;
        }

        /// <summary>
        /// date year/month format.
        /// (user can override default only)
        /// </summary>
        internal string[] YearMonths(CalendarId calendarId)
        {
            return GetCalendar(calendarId).saYearMonths;
        }

        internal string[] DayNames(CalendarId calendarId)
        {
            return GetCalendar(calendarId).saDayNames;
        }

        internal string[] AbbreviatedDayNames(CalendarId calendarId)
        {
            return GetCalendar(calendarId).saAbbrevDayNames;
        }

        internal string[] SuperShortDayNames(CalendarId calendarId)
        {
            return GetCalendar(calendarId).saSuperShortDayNames;
        }

        internal string[] MonthNames(CalendarId calendarId)
        {
            return GetCalendar(calendarId).saMonthNames;
        }

        internal string[] GenitiveMonthNames(CalendarId calendarId)
        {
            return GetCalendar(calendarId).saMonthGenitiveNames;
        }

        internal string[] AbbreviatedMonthNames(CalendarId calendarId)
        {
            return GetCalendar(calendarId).saAbbrevMonthNames;
        }

        internal string[] AbbreviatedGenitiveMonthNames(CalendarId calendarId)
        {
            return GetCalendar(calendarId).saAbbrevMonthGenitiveNames;
        }

        /// <remarks>>
        /// Note: This only applies to Hebrew, and it basically adds a "1" to the 6th month name
        /// the non-leap names skip the 7th name in the normal month name array
        /// </remarks>
        internal string[] LeapYearMonthNames(CalendarId calendarId)
        {
            return GetCalendar(calendarId).saLeapYearMonthNames;
        }

        internal string MonthDay(CalendarId calendarId)
        {
            return GetCalendar(calendarId).sMonthDay;
        }

        /// <summary>
        /// All available calendar type(s). The first one is the default calendar.
        /// </summary>
        internal CalendarId[] CalendarIds
        {
            get
            {
                if (_waCalendars == null)
                {
                    // We do this lazily as opposed in CreateCultureWithInvariantData to avoid introducing
                    // enum arrays into apps that otherwise wouldn't have them. This helps with size in AOT.
                    _waCalendars = [CalendarId.GREGORIAN];
                    // SharpOS cut: the OS calendar list (CalendarData.GetCalendarsCore) - invariant only.
                }
                return _waCalendars;
            }
        }

        /// <summary>
        /// Native calendar names. Index of optional calendar - 1, empty if
        /// no optional calendar at that number
        /// </summary>
        internal string CalendarName(CalendarId calendarId)
        {
            return GetCalendar(calendarId).sNativeName;
        }

        internal CalendarData GetCalendar(CalendarId calendarId)
        {
            return CalendarData.Invariant; // SharpOS: upstream's GlobalizationMode.Invariant branch; per-culture calendars cut
        }

        /// <summary>
        /// Compare info name (including sorting key) to use if custom
        /// </summary>
        internal string SortName
        {
            get
            {
                Debug.Assert(_sRealName != null, "[CultureData.SortName] Expected _sRealName to be populated by already");
                return _sRealName;
            }
        }

        /// <summary>
        /// All of our era names
        /// </summary>
        internal string[] EraNames(CalendarId calendarId)
        {
            Debug.Assert(calendarId > 0, "[CultureData.saEraNames] Expected Calendar.ID > 0");
            return GetCalendar(calendarId).saEraNames;
        }

        internal string[] AbbrevEraNames(CalendarId calendarId)
        {
            Debug.Assert(calendarId > 0, "[CultureData.saAbbrevEraNames] Expected Calendar.ID > 0");
            return GetCalendar(calendarId).saAbbrevEraNames;
        }

        internal string[] AbbreviatedEnglishEraNames(CalendarId calendarId)
        {
            Debug.Assert(calendarId > 0, "[CultureData.saAbbrevEraNames] Expected Calendar.ID > 0");
            return GetCalendar(calendarId).saAbbrevEnglishEraNames;
        }

        /// <summary>
        /// Time separator (derived from time format)
        /// </summary>
        internal string TimeSeparator => _sTimeSeparator!; // SharpOS: upstream's GlobalizationMode.Invariant branch

        /// <summary>
        /// Date separator (derived from short date format)
        /// </summary>
        internal string DateSeparator(CalendarId calendarId)
        {
            return "/"; // SharpOS: upstream's GlobalizationMode.Invariant branch
        }
    }
}
