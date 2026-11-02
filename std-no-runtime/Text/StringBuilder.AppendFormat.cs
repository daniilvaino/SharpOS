// StringBuilder.AppendFormat — the BCL's overloads (String.Format composite
// formatting, appended). Step232: AvaloniaEdit's HighlightingColor.ToCss.
//
// SharpOS: upstream formats straight into the builder (StringBuilder.AppendFormat
// parses the format itself); here the text is made by string.Format and
// appended — the same characters, one string more.

namespace System.Text
{
    public sealed partial class StringBuilder
    {
        public StringBuilder AppendFormat(string format, object? arg0) => Append(string.Format(format, arg0));

        public StringBuilder AppendFormat(string format, object? arg0, object? arg1) => Append(string.Format(format, arg0, arg1));

        public StringBuilder AppendFormat(string format, object? arg0, object? arg1, object? arg2) => Append(string.Format(format, arg0, arg1, arg2));

        public StringBuilder AppendFormat(string format, params object?[] args) => Append(string.Format(format, args));

        public StringBuilder AppendFormat(IFormatProvider? provider, string format, object? arg0) => Append(string.Format(provider, format, arg0));

        public StringBuilder AppendFormat(IFormatProvider? provider, string format, object? arg0, object? arg1) => Append(string.Format(provider, format, arg0, arg1));

        public StringBuilder AppendFormat(IFormatProvider? provider, string format, object? arg0, object? arg1, object? arg2) => Append(string.Format(provider, format, arg0, arg1, arg2));

        public StringBuilder AppendFormat(IFormatProvider? provider, string format, params object?[] args) => Append(string.Format(provider, format, args));
    }
}
