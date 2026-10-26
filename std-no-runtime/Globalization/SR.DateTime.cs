// Resource strings for the ported date/time stack (DateTime, DateTimeOffset,
// TimeSpan, DateOnly, TimeOnly, TimeZoneInfo, Globalization date/time
// formatting and parsing), with the texts of dotnet/runtime's
// System.Private.CoreLib Strings.resx (.NET 10 fork, MIT). Only the keys those
// files use that std's other SR partials do not already define.

namespace System
{
    internal static partial class SR
    {
        internal const string Arg_ArgumentOutOfRangeException = "Specified argument was out of the range of valid values.";
        internal const string Arg_ArrayZeroError = "Array must not be of length zero.";
        internal const string Arg_CannotBeNaN = "TimeSpan does not accept floating point Not-a-Number values.";
        internal const string Arg_MustBeDateOnly = "Object must be of type DateOnly.";
        internal const string Arg_MustBeDateTime = "Object must be of type DateTime.";
        internal const string Arg_MustBeDateTimeOffset = "Object must be of type DateTimeOffset.";
        internal const string Arg_MustBeTimeOnly = "Object must be of type TimeOnly.";
        internal const string Arg_MustBeTimeSpan = "Object must be of type TimeSpan.";
        internal const string Arg_OleAutDateInvalid = "Not a legal OleAut date.";
        internal const string Arg_OleAutDateScale = "OleAut date did not convert to a DateTime correctly.";
        internal const string ArgumentNull_String = "String reference not set to an instance of a String.";
        internal const string ArgumentOutOfRange_AddValue = "Value to add was out of range.";
        internal const string ArgumentOutOfRange_BadHourMinuteSecond = "Hour, Minute, and Second parameters describe an un-representable DateTime.";
        internal const string ArgumentOutOfRange_BadYearMonthDay = "Year, Month, and Day parameters describe an un-representable DateTime.";
        internal const string ArgumentOutOfRange_DateArithmetic = "The added or subtracted value results in an un-representable DateTime.";
        internal const string ArgumentOutOfRange_DateTimeBadMonths = "Months value must be between +/-120000.";
        internal const string ArgumentOutOfRange_DateTimeBadTicks = "Ticks must be between DateTime.MinValue.Ticks and DateTime.MaxValue.Ticks.";
        internal const string ArgumentOutOfRange_DateTimeBadYears = "Years value must be between +/-10000.";
        internal const string ArgumentOutOfRange_DayNumber = "Day number must be between 0 and DateOnly.MaxValue.DayNumber.";
        internal const string ArgumentOutOfRange_DayOfWeek = "The DayOfWeek enumeration must be in the range 0 through 6.";
        internal const string ArgumentOutOfRange_DayParam = "The Day parameter must be in the range 1 through 31.";
        internal const string ArgumentOutOfRange_FileTimeInvalid = "Not a valid Win32 FileTime.";
        internal const string ArgumentOutOfRange_InvalidEraValue = "Era value was not valid.";
        internal const string ArgumentOutOfRange_Month = "Month must be between one and twelve.";
        internal const string ArgumentOutOfRange_MonthParam = "The Month parameter must be in the range 1 through 12.";
        internal const string ArgumentOutOfRange_TimeOnlyBadTicks = "Ticks must be between 0 and and TimeOnly.MaxValue.Ticks.";
        internal const string ArgumentOutOfRange_UtcOffset = "The TimeSpan parameter must be within plus or minus 14.0 hours.";
        internal const string ArgumentOutOfRange_UtcOffsetAndDaylightDelta = "The sum of the BaseUtcOffset and DaylightDelta properties must within plus or minus 14.0 hours.";
        internal const string ArgumentOutOfRange_Week = "The Week parameter must be in the range 1 through 5.";
        internal const string ArgumentOutOfRange_Week_ISO = "The week parameter must be in the range 1 through 53.";
        internal const string ArgumentOutOfRange_Year = "Year must be between 1 and 9999.";
        internal const string Argument_AdjustmentRulesNoNulls = "The AdjustmentRule array cannot contain null elements.";
        internal const string Argument_AdjustmentRulesOutOfOrder = "The elements of the AdjustmentRule array must be in chronological order and must not overlap.";
        internal const string Argument_ConflictingDateTimeRoundtripStyles = "The DateTimeStyles value RoundtripKind cannot be used with the values AssumeLocal, AssumeUniversal or AdjustToUniversal.";
        internal const string Argument_ConflictingDateTimeStyles = "The DateTimeStyles values AssumeLocal and AssumeUniversal cannot be used together.";
        internal const string Argument_ConvertMismatch = "The conversion could not be completed because the supplied DateTime did not have the Kind property set correctly.  For example, when the Kind property is DateTimeKind.Local, the source time zone must be TimeZoneInfo.Local.";
        internal const string Argument_DateTimeBadBinaryData = "The binary data must result in a DateTime with ticks between DateTime.MinValue.Ticks and DateTime.MaxValue.Ticks.";
        internal const string Argument_DateTimeHasTicks = "The supplied DateTime must have the Year, Month, and Day properties set to 1.  The time cannot be specified more precisely than whole milliseconds.";
        internal const string Argument_DateTimeHasTimeOfDay = "The supplied DateTime includes a TimeOfDay setting.   This is not supported.";
        internal const string Argument_DateTimeIsInvalid = "The supplied DateTime represents an invalid time.  For example, when the clock is adjusted forward, any time in the period that is skipped is invalid.";
        internal const string Argument_DateTimeIsNotAmbiguous = "The supplied DateTime is not in an ambiguous time range.";
        internal const string Argument_DateTimeKindMustBeUnspecified = "The supplied DateTime must have the Kind property set to DateTimeKind.Unspecified.";
        internal const string Argument_DateTimeKindMustBeUnspecifiedOrUtc = "The supplied DateTime must have the Kind property set to DateTimeKind.Unspecified or DateTimeKind.Utc.";
        internal const string Argument_DateTimeOffsetInvalidDateTimeStyles = "The DateTimeStyles value 'NoCurrentDateDefault' is not allowed when parsing DateTimeOffset.";
        internal const string Argument_DateTimeOffsetIsNotAmbiguous = "The supplied DateTimeOffset is not in an ambiguous time range.";
        internal const string Argument_InvalidArrayLength = "Length of the array must be {0}.";
        internal const string Argument_InvalidCalendar = "Not a valid calendar for the given culture.";
        internal const string Argument_InvalidDateStyles = "The only allowed values for the styles are AllowWhiteSpaces, AllowTrailingWhite, AllowLeadingWhite, and AllowInnerWhite.";
        internal const string Argument_InvalidDateTimeKind = "Invalid DateTimeKind value.";
        internal const string Argument_InvalidDateTimeStyles = "An undefined DateTimeStyles value is being used.";
        internal const string Argument_InvalidSerializedString = "The specified serialized string '{0}' is not supported.";
        internal const string Argument_InvalidTimeSpanStyles = "An undefined TimeSpanStyles value is being used.";
        internal const string Argument_OffsetLocalMismatch = "The UTC Offset of the local dateTime parameter does not match the offset argument.";
        internal const string Argument_OffsetOutOfRange = "Offset must be within plus or minus 14 hours.";
        internal const string Argument_OffsetPrecision = "Offset must be specified in whole minutes.";
        internal const string Argument_OffsetUtcMismatch = "The UTC Offset for Utc DateTime instances must be 0.";
        internal const string Argument_OutOfOrderDateTimes = "The DateStart property must come before the DateEnd property.";
        internal const string Argument_ResultCalendarRange = "The result is out of the supported range for this calendar. The result should be between {0} (Gregorian date) and {1} (Gregorian date), inclusive.";
        internal const string Argument_TimeSpanHasSeconds = "The TimeSpan parameter cannot be specified more precisely than whole minutes.";
        internal const string Argument_TransitionTimesAreIdentical = "The DaylightTransitionStart property must not equal the DaylightTransitionEnd property.";
        internal const string Argument_UTCOutOfRange = "The UTC time represented when the offset is applied must be between year 0 and 10,000.";
        internal const string Format_BadDateOnly = "String '{0}' was not recognized as a valid DateOnly.";
        internal const string Format_BadDatePattern = "Could not determine the order of year, month, and date from '{0}'.";
        internal const string Format_BadDateTime = "String '{0}' was not recognized as a valid DateTime.";
        internal const string Format_BadDateTimeCalendar = "The DateTime represented by the string '{0}' is not supported in calendar '{1}'.";
        internal const string Format_BadDayOfWeek = "String '{0}' was not recognized as a valid DateTime because the day of week was incorrect.";
        internal const string Format_BadFormatSpecifier = "Format specifier '{0}' was invalid.";
        internal const string Format_BadQuote = "Cannot find a matching quote character for the character '{0}'.";
        internal const string Format_BadTimeOnly = "String '{0}' was not recognized as a valid TimeOnly.";
        internal const string Format_BadTimeSpan = "String '{0}' was not recognized as a valid TimeSpan.";
        internal const string Format_DateOutOfRange = "The DateTime represented by the string '{0}' is out of range.";
        internal const string Format_DateTimeOnlyContainsNoneDateParts = "String '{0}' contains parts which are not specific to the {1}.";
        internal const string Format_InvalidString = "Input string was not in a correct format.";
        internal const string Format_MissingIncompleteDate = "There must be at least a partial date with a year present in the input string '{0}'.";
        internal const string Format_NoFormatSpecifier = "No format specifiers were provided.";
        internal const string Format_OffsetOutOfRange = "The time zone offset of string '{0}' must be within plus or minus 14 hours.";
        internal const string Format_RepeatDateTimePattern = "DateTime pattern '{0}' appears more than once with different values.";
        internal const string Format_UTCOutOfRange = "The UTC representation of the date '{0}' falls outside the year range 1-9999.";
        internal const string Format_UnknownDateTimeWord = "The string '{0}' was not recognized as a valid DateTime. There is an unknown word starting at index '{1}'.";
        internal const string InvalidOperation_TimeProviderNullLocalTimeZone = "The operation cannot be performed when TimeProvider.LocalTimeZone is null.";
        internal const string Overflow_Duration = "The duration cannot be returned for TimeSpan.MinValue because the absolute value of TimeSpan.MinValue exceeds the value of TimeSpan.MaxValue.";
        internal const string Overflow_NegateTwosCompNum = "Negating the minimum value of a twos complement number is invalid.";
        internal const string Overflow_TimeSpanElementTooLarge = "The TimeSpan string '{0}' could not be parsed because at least one of the numeric components is out of range or contains too many digits.";
        internal const string Overflow_TimeSpanTooLong = "TimeSpan overflowed because the duration is too long.";
        internal const string SerializationException = "Serialization error.";
        internal const string Serialization_InvalidData = "An error occurred while deserializing the object.  The serialized data is corrupt.";
        internal const string Serialization_InvalidEscapeSequence = "The serialized data contained an invalid escape sequence '\\\\{0}'.";
        internal const string TimeZoneNotFound_MissingData = "The time zone ID '{0}' was not found on the local computer.";
    }
}
