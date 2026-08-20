// The handful of date-format facts a UI needs to lay out a field.
//
// A date entry box has to know where the separators go and how wide each part
// is before the user types anything, and it asks the culture. The real
// DateTimeFormatInfo carries the whole locale database — month names, era
// names, dozens of patterns; this carries the two answers our one culture has,
// and says so rather than pretending to be configurable.

namespace System.Globalization
{
    public sealed class DateTimeFormatInfo
    {
        internal DateTimeFormatInfo() { }

        // Properties, not fields: a static holder here would need a class
        // constructor, which this environment cannot run (limits §1).
        public static DateTimeFormatInfo InvariantInfo => new DateTimeFormatInfo();
        public static DateTimeFormatInfo CurrentInfo => InvariantInfo;

        public string DateSeparator => "/";
        public string TimeSeparator => ":";

        public string ShortDatePattern => "MM/dd/yyyy";
        public string LongDatePattern => "yyyy-MM-dd";
        public string ShortTimePattern => "HH:mm";
        public string LongTimePattern => "HH:mm:ss";

        public string FullDateTimePattern => "yyyy-MM-dd HH:mm:ss";
        public string SortableDateTimePattern => "yyyy-MM-dd HH:mm:ss";
    }
}
