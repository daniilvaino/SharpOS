// System.MemoryExtensions — the char-span members that take a
// StringComparison, and searches for a value inside or outside a range.
// Signatures and results are the BCL's (dotnet/runtime main, .NET 10:
// MemoryExtensions.Globalization.cs and MemoryExtensions.cs, MIT); the bodies
// are scalar loops, as in the rest of this class.
//
// Comparisons: std has one culture, the invariant one, in globalization-
// invariant mode (Globalization/CompareInfo.cs). There the BCL compares the
// culture values ordinally and the IgnoreCase ones by simple case mapping,
// which is what these do.
//
//   Equals / CompareTo / Contains / IndexOf / LastIndexOf (span, span, StringComparison)
//   IndexOfAnyInRange / IndexOfAnyExceptInRange / LastIndexOf… / ContainsAny…InRange   Span + ReadOnlySpan

using System.Globalization;

namespace System
{
    public static partial class MemoryExtensions
    {
        // ---- StringComparison ----

        public static bool Equals(this ReadOnlySpan<char> span, ReadOnlySpan<char> other, StringComparison comparisonType)
        {
            CheckStringComparison(comparisonType);
            return EqualsCore(span, other, IgnoresCase(comparisonType));
        }

        public static int CompareTo(this ReadOnlySpan<char> span, ReadOnlySpan<char> other, StringComparison comparisonType)
        {
            CheckStringComparison(comparisonType);
            return CultureInfo.InvariantCulture.CompareInfo.Compare(span, other,
                IgnoresCase(comparisonType) ? CompareOptions.OrdinalIgnoreCase : CompareOptions.Ordinal);
        }

        public static bool Contains(this ReadOnlySpan<char> span, ReadOnlySpan<char> value, StringComparison comparisonType)
            => IndexOf(span, value, comparisonType) >= 0;

        public static int IndexOf(this ReadOnlySpan<char> span, ReadOnlySpan<char> value, StringComparison comparisonType)
        {
            CheckStringComparison(comparisonType);
            if (!IgnoresCase(comparisonType))
                return span.IndexOf(value);

            if (value.Length == 0)
                return 0;
            for (int i = 0; i <= span.Length - value.Length; i++)
            {
                if (EqualsCore(span.Slice(i, value.Length), value, ignoreCase: true))
                    return i;
            }
            return -1;
        }

        public static int LastIndexOf(this ReadOnlySpan<char> span, ReadOnlySpan<char> value, StringComparison comparisonType)
        {
            CheckStringComparison(comparisonType);
            if (!IgnoresCase(comparisonType))
                return span.LastIndexOf(value);

            if (value.Length == 0)
                return span.Length;
            for (int i = span.Length - value.Length; i >= 0; i--)
            {
                if (EqualsCore(span.Slice(i, value.Length), value, ignoreCase: true))
                    return i;
            }
            return -1;
        }

        // string.CheckStringComparison: a value outside the enum is the caller's error.
        private static void CheckStringComparison(StringComparison comparisonType)
        {
            if ((uint)comparisonType > (uint)StringComparison.OrdinalIgnoreCase)
                throw new ArgumentException("The string comparison type passed in is currently not supported.", nameof(comparisonType));
        }

        // ---- Ranges ----
        // SpanHelpers.IndexOfAnyInRange and friends, the non-vectorized path:
        // low <= x <= high through IComparable<T>.

        public static int IndexOfAnyInRange<T>(this Span<T> span, T lowInclusive, T highInclusive) where T : IComparable<T>
            => IndexOfAnyInRange((ReadOnlySpan<T>)span, lowInclusive, highInclusive);

        public static int IndexOfAnyInRange<T>(this ReadOnlySpan<T> span, T lowInclusive, T highInclusive) where T : IComparable<T>
        {
            if (lowInclusive is null || highInclusive is null)
                ThrowNullLowHighInclusive(lowInclusive, highInclusive);

            for (int i = 0; i < span.Length; i++)
            {
                if (InRange(span[i], lowInclusive, highInclusive))
                    return i;
            }
            return -1;
        }

        public static int IndexOfAnyExceptInRange<T>(this Span<T> span, T lowInclusive, T highInclusive) where T : IComparable<T>
            => IndexOfAnyExceptInRange((ReadOnlySpan<T>)span, lowInclusive, highInclusive);

        public static int IndexOfAnyExceptInRange<T>(this ReadOnlySpan<T> span, T lowInclusive, T highInclusive) where T : IComparable<T>
        {
            if (lowInclusive is null || highInclusive is null)
                ThrowNullLowHighInclusive(lowInclusive, highInclusive);

            for (int i = 0; i < span.Length; i++)
            {
                if (!InRange(span[i], lowInclusive, highInclusive))
                    return i;
            }
            return -1;
        }

        public static int LastIndexOfAnyInRange<T>(this Span<T> span, T lowInclusive, T highInclusive) where T : IComparable<T>
            => LastIndexOfAnyInRange((ReadOnlySpan<T>)span, lowInclusive, highInclusive);

        public static int LastIndexOfAnyInRange<T>(this ReadOnlySpan<T> span, T lowInclusive, T highInclusive) where T : IComparable<T>
        {
            if (lowInclusive is null || highInclusive is null)
                ThrowNullLowHighInclusive(lowInclusive, highInclusive);

            for (int i = span.Length - 1; i >= 0; i--)
            {
                if (InRange(span[i], lowInclusive, highInclusive))
                    return i;
            }
            return -1;
        }

        public static int LastIndexOfAnyExceptInRange<T>(this Span<T> span, T lowInclusive, T highInclusive) where T : IComparable<T>
            => LastIndexOfAnyExceptInRange((ReadOnlySpan<T>)span, lowInclusive, highInclusive);

        public static int LastIndexOfAnyExceptInRange<T>(this ReadOnlySpan<T> span, T lowInclusive, T highInclusive) where T : IComparable<T>
        {
            if (lowInclusive is null || highInclusive is null)
                ThrowNullLowHighInclusive(lowInclusive, highInclusive);

            for (int i = span.Length - 1; i >= 0; i--)
            {
                if (!InRange(span[i], lowInclusive, highInclusive))
                    return i;
            }
            return -1;
        }

        public static bool ContainsAnyInRange<T>(this Span<T> span, T lowInclusive, T highInclusive) where T : IComparable<T>
            => IndexOfAnyInRange((ReadOnlySpan<T>)span, lowInclusive, highInclusive) >= 0;

        public static bool ContainsAnyInRange<T>(this ReadOnlySpan<T> span, T lowInclusive, T highInclusive) where T : IComparable<T>
            => IndexOfAnyInRange(span, lowInclusive, highInclusive) >= 0;

        public static bool ContainsAnyExceptInRange<T>(this Span<T> span, T lowInclusive, T highInclusive) where T : IComparable<T>
            => IndexOfAnyExceptInRange((ReadOnlySpan<T>)span, lowInclusive, highInclusive) >= 0;

        public static bool ContainsAnyExceptInRange<T>(this ReadOnlySpan<T> span, T lowInclusive, T highInclusive) where T : IComparable<T>
            => IndexOfAnyExceptInRange(span, lowInclusive, highInclusive) >= 0;

        private static bool InRange<T>(T value, T lowInclusive, T highInclusive) where T : IComparable<T>
            => lowInclusive.CompareTo(value) <= 0 && highInclusive.CompareTo(value) >= 0;

        private static void ThrowNullLowHighInclusive<T>(T? lowInclusive, T? highInclusive)
        {
            throw new ArgumentNullException(lowInclusive is null ? nameof(lowInclusive) : nameof(highInclusive));
        }
    }
}
