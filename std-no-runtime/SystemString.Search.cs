// System.String — the search, replace and case members that take a
// StringComparison or a CultureInfo, Concat over spans, and Join over any
// IEnumerable<T>. Signatures and results are the BCL's (dotnet/runtime main,
// .NET 10: String.Searching.cs, String.Manipulation.cs, String.Comparison.cs,
// MIT).
//
// Comparisons as in globalization-invariant mode, the only mode std has
// (Globalization/CompareInfo.cs): the culture values compare ordinally, the
// IgnoreCase ones by simple case mapping. The span searches are in
// Runtime/MemoryExtensions.Comparison.cs; these delegate to them.
//
// SharpOS: Replace(…, StringComparison) for the ignore-case values finds
// matches through MemoryExtensions.IndexOf instead of CompareInfo.IndexOf
// with a match length — in invariant mode a match is as long as the value.

using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace System
{
    public sealed partial class String
    {
        public bool Contains(string value, StringComparison comparisonType)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return IndexOf(value, comparisonType) >= 0;
        }

        public bool Contains(char value, StringComparison comparisonType) => IndexOf(value, comparisonType) != -1;

        public int IndexOf(char value, StringComparison comparisonType)
        {
            switch (comparisonType)
            {
                case StringComparison.CurrentCulture:
                case StringComparison.InvariantCulture:
                case StringComparison.Ordinal:
                    return IndexOf(value);
                case StringComparison.CurrentCultureIgnoreCase:
                case StringComparison.InvariantCultureIgnoreCase:
                case StringComparison.OrdinalIgnoreCase:
                    return this.AsSpan().IndexOf(MemoryMarshal.CreateReadOnlySpan(ref value, 1), comparisonType);
                default:
                    throw new ArgumentException("The string comparison type passed in is currently not supported.", nameof(comparisonType));
            }
        }

        public int IndexOf(string value, int startIndex, StringComparison comparisonType)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if ((uint)startIndex > (uint)Length) throw new ArgumentOutOfRangeException(nameof(startIndex), "Index was out of range. Must be non-negative and less than or equal to the size of the collection.");
            int found = this.AsSpan(startIndex).IndexOf(value.AsSpan(), comparisonType);
            return found < 0 ? found : found + startIndex;
        }

        public int LastIndexOf(string value, StringComparison comparisonType)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return this.AsSpan().LastIndexOf(value.AsSpan(), comparisonType);
        }

        public string Replace(string oldValue, string? newValue, StringComparison comparisonType)
        {
            switch (comparisonType)
            {
                case StringComparison.CurrentCulture:
                case StringComparison.InvariantCulture:
                case StringComparison.Ordinal:
                    return Replace(oldValue, newValue ?? "");
                case StringComparison.CurrentCultureIgnoreCase:
                case StringComparison.InvariantCultureIgnoreCase:
                case StringComparison.OrdinalIgnoreCase:
                    return ReplaceIgnoreCase(oldValue, newValue ?? "");
                default:
                    throw new ArgumentException("The string comparison type passed in is currently not supported.", nameof(comparisonType));
            }
        }

        public string Replace(string oldValue, string? newValue, bool ignoreCase, CultureInfo? culture)
            => ignoreCase ? ReplaceIgnoreCase(oldValue, newValue ?? "") : Replace(oldValue, newValue ?? "");

        private string ReplaceIgnoreCase(string oldValue, string newValue)
        {
            if (oldValue == null) throw new ArgumentNullException(nameof(oldValue));
            if (oldValue.Length == 0) throw new ArgumentException("String cannot be of zero length.", nameof(oldValue));

            ReadOnlySpan<char> rest = this.AsSpan();
            int at = rest.IndexOf(oldValue.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (at < 0) return this;

            var result = new StringBuilder(Length);
            do
            {
                result.Append(rest.Slice(0, at));
                result.Append(newValue);
                rest = rest.Slice(at + oldValue.Length);
                at = rest.IndexOf(oldValue.AsSpan(), StringComparison.OrdinalIgnoreCase);
            }
            while (at >= 0);
            result.Append(rest);
            return result.ToString();
        }

        // BCL String.Comparison.cs: through the culture's CompareInfo.
        public static int Compare(string? strA, string? strB, CultureInfo? culture, CompareOptions options)
            => (culture ?? CultureInfo.CurrentCulture).CompareInfo.Compare(strA, strB, options);

        public static int Compare(string? strA, string? strB, bool ignoreCase, CultureInfo? culture)
            => Compare(strA, strB, culture, ignoreCase ? CompareOptions.IgnoreCase : CompareOptions.None);

        // One culture, the invariant one (Globalization.cs): its casing.
        public string ToLower(CultureInfo? culture) => ToLowerInvariant();

        public string ToUpper(CultureInfo? culture) => ToUpperInvariant();

        // ---- Concat over spans ----

        public static string Concat(ReadOnlySpan<char> str0, ReadOnlySpan<char> str1)
        {
            int length = checked(str0.Length + str1.Length);
            if (length == 0) return Empty;

            var chars = new char[length];
            str0.CopyTo(chars);
            str1.CopyTo(new Span<char>(chars, str0.Length, str1.Length));
            return new string(chars);
        }

        public static string Concat(ReadOnlySpan<char> str0, ReadOnlySpan<char> str1, ReadOnlySpan<char> str2)
        {
            int length = checked(str0.Length + str1.Length + str2.Length);
            if (length == 0) return Empty;

            var chars = new char[length];
            str0.CopyTo(chars);
            str1.CopyTo(new Span<char>(chars, str0.Length, str1.Length));
            str2.CopyTo(new Span<char>(chars, str0.Length + str1.Length, str2.Length));
            return new string(chars);
        }

        public static string Concat(ReadOnlySpan<char> str0, ReadOnlySpan<char> str1, ReadOnlySpan<char> str2, ReadOnlySpan<char> str3)
        {
            int length = checked(str0.Length + str1.Length + str2.Length + str3.Length);
            if (length == 0) return Empty;

            var chars = new char[length];
            str0.CopyTo(chars);
            str1.CopyTo(new Span<char>(chars, str0.Length, str1.Length));
            str2.CopyTo(new Span<char>(chars, str0.Length + str1.Length, str2.Length));
            str3.CopyTo(new Span<char>(chars, str0.Length + str1.Length + str2.Length, str3.Length));
            return new string(chars);
        }

        // ---- Join over any sequence ----

        public static string Join<T>(char separator, IEnumerable<T> values) => JoinCore(separator.ToString(), values);

        public static string Join<T>(string? separator, IEnumerable<T> values) => JoinCore(separator ?? "", values);

        public static string Join(char separator, params object?[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            return JoinCore(separator.ToString(), values);
        }

        private static string JoinCore<T>(string separator, IEnumerable<T> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));

            var result = new StringBuilder();
            bool first = true;
            foreach (T value in values)
            {
                if (!first) result.Append(separator);
                first = false;
                if (value != null) result.Append(value.ToString());
            }
            return result.ToString();
        }
    }
}
