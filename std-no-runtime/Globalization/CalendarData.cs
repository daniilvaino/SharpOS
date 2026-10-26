// Ported from dotnet/runtime (.NET 10 fork, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Globalization/CalendarData.cs (+ CalendarData.Unix.cs GetTwoDigitYearMax)
// SharpOS cuts: everything that loads a culture's calendar from the OS (ICU/NLS) - globalization is
// invariant-only, so CalendarData.Invariant is the only instance (marked inline).
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace System.Globalization
{
    // List of calendar data
    // Note the we cache overrides.
    // Note that localized names (resource names) aren't available from here.
    //
    //  NOTE: Calendars depend on the locale name that creates it.  Only a few
    //        properties are available without locales using CalendarData.GetCalendar(CalendarData)
    //
    internal sealed partial class CalendarData
    {
        // Max calendars
        internal const int MAX_CALENDARS = 23;

        // Identity
        internal string sNativeName = null!; // Calendar Name for the locale

        // Formats
        internal string[] saShortDates = null!; // Short Data format, default first
        internal string[] saYearMonths = null!; // Year/Month Data format, default first
        internal string[] saLongDates = null!; // Long Data format, default first
        internal string sMonthDay = null!; // Month/Day format

        // Calendar Parts Names
        internal string[] saEraNames = null!; // Names of Eras
        internal string[] saAbbrevEraNames = null!; // Abbreviated Era Names
        internal string[] saAbbrevEnglishEraNames = null!; // Abbreviated Era Names in English
        internal string[] saDayNames = null!; // Day Names, null to use locale data, starts on Sunday
        internal string[] saAbbrevDayNames = null!; // Abbrev Day Names, null to use locale data, starts on Sunday
        internal string[] saSuperShortDayNames = null!; // Super short Day of week names
        internal string[] saMonthNames = null!; // Month Names (13)
        internal string[] saAbbrevMonthNames = null!; // Abbrev Month Names (13)
        internal string[] saMonthGenitiveNames = null!; // Genitive Month Names (13)
        internal string[] saAbbrevMonthGenitiveNames = null!; // Genitive Abbrev Month Names (13)
        internal string[] saLeapYearMonthNames = null!; // Multiple strings for the month names in a leap year.

        internal int iTwoDigitYearMax = 2049; // Max 2 digit year
        private int iCurrentEra;  // current era # (usually 1)

        // Use overrides?
        internal bool bUseUserOverrides; // True if we want user overrides.

        // Static invariant for the invariant locale
        internal static readonly CalendarData Invariant = CreateInvariant();

        // Private constructor
        private CalendarData()
        {
        }

        // Invariant factory
        private static CalendarData CreateInvariant()
        {
            // Set our default/gregorian US calendar data
            // Calendar IDs are 1-based, arrays are 0 based.
            CalendarData invariant = new CalendarData();

            // Set default data for calendar
            // Note that we don't load resources since this IS NOT supposed to change (by definition)
            invariant.sNativeName = "Gregorian Calendar";  // Calendar Name

            // Year
            invariant.iTwoDigitYearMax = 2049; // Max 2 digit year
            invariant.iCurrentEra = 1; // Current era #

            // Formats
            invariant.saShortDates = ["MM/dd/yyyy", "yyyy-MM-dd"];          // short date format
            invariant.saLongDates = ["dddd, dd MMMM yyyy"];                 // long date format
            invariant.saYearMonths = ["yyyy MMMM"];                         // year month format
            invariant.sMonthDay = "MMMM dd";                                            // Month day pattern

            // Calendar Parts Names
            invariant.saEraNames = ["A.D."];     // Era names
            invariant.saAbbrevEraNames = ["AD"];      // Abbreviated Era names
            invariant.saAbbrevEnglishEraNames = ["AD"];     // Abbreviated era names in English
            invariant.saDayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"]; // day names
            invariant.saAbbrevDayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];     // abbreviated day names
            invariant.saSuperShortDayNames = ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"];      // The super short day names
            invariant.saMonthNames = [ "January", "February", "March", "April", "May", "June",
                                       "July", "August", "September", "October", "November", "December", string.Empty ]; // month names
            invariant.saAbbrevMonthNames = [ "Jan", "Feb", "Mar", "Apr", "May", "Jun",
                                             "Jul", "Aug", "Sep", "Oct", "Nov", "Dec", string.Empty ]; // abbreviated month names
            invariant.saMonthGenitiveNames = invariant.saMonthNames;              // Genitive month names (same as month names for invariant)
            invariant.saAbbrevMonthGenitiveNames = invariant.saAbbrevMonthNames;    // Abbreviated genitive month names (same as abbrev month names for invariant)
            invariant.saLeapYearMonthNames = invariant.saMonthNames;              // leap year month names are unused in Gregorian English (invariant)

            invariant.bUseUserOverrides = false;

            return invariant;
        }

        // SharpOS cut: internal CalendarData(string localeName, CalendarId calendarId, bool bUseUserOverrides),
        // InitializeEraNames, InitializeAbbreviatedEraNames - they load a culture's calendar from ICU/NLS;
        // the only data here is Invariant (globalization is invariant-only).

        // SharpOS: from CalendarData.Unix.cs, the invariant branch.
        internal static int GetTwoDigitYearMax(CalendarId calendarId) => Invariant.iTwoDigitYearMax;

        internal static int GetCalendarCurrentEra(Calendar calendar)
        {
            // SharpOS cut: the per-culture lookup (CalendarIdToCultureName, CultureInfo.GetCultureInfo(...)._cultureData)
            // and SystemSupportsTaiwaneseCalendar - GlobalizationMode.Invariant is always true here.
            return Invariant.iCurrentEra;
        }
    }
}
