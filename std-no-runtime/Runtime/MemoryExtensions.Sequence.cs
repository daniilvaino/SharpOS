// System.MemoryExtensions — searching for a sequence, ContainsAny, and
// copying an array into a span. Signatures and results are the BCL's
// (dotnet/runtime release/8.0 src/libraries/System.Private.CoreLib/src/System/MemoryExtensions.cs,
// MIT); the bodies are scalar loops instead of SpanHelpers' vectorized searches,
// as in MemoryExtensions.cs and MemoryExtensions.Search.cs.
//
//   IndexOf(ReadOnlySpan<T> value) / LastIndexOf(ReadOnlySpan<T> value)  Span + ReadOnlySpan
//   ContainsAny(value0, value1[, value2]) / ContainsAny(values)          Span + ReadOnlySpan
//   CopyTo<T>(T[], Span<T>) / CopyTo<T>(T[], Memory<T>)

namespace System
{
    public static partial class MemoryExtensions
    {
        // ---- IndexOf / LastIndexOf of a sequence ----

        public static int IndexOf<T>(this Span<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>
            => IndexOf((ReadOnlySpan<T>)span, value);

        public static int IndexOf<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>
        {
            // As SpanHelpers.IndexOf(ref T, int, ref T, int): empty value matches at 0.
            if (value.Length == 0)
                return 0;

            int last = span.Length - value.Length;
            T first = value[0];
            for (int i = 0; i <= last; i++)
            {
                if (span[i] == null ? first == null : span[i].Equals(first))
                {
                    if (span.Slice(i + 1, value.Length - 1).SequenceEqual(value.Slice(1)))
                        return i;
                }
            }
            return -1;
        }

        public static int LastIndexOf<T>(this Span<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>
            => LastIndexOf((ReadOnlySpan<T>)span, value);

        public static int LastIndexOf<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>
        {
            // As SpanHelpers.LastIndexOf(ref T, int, ref T, int): empty value matches at the end.
            if (value.Length == 0)
                return span.Length;

            for (int i = span.Length - value.Length; i >= 0; i--)
            {
                if (span.Slice(i, value.Length).SequenceEqual(value))
                    return i;
            }
            return -1;
        }

        // ---- ContainsAny ----

        public static bool ContainsAny<T>(this Span<T> span, T value0, T value1) where T : IEquatable<T>
            => ContainsAny((ReadOnlySpan<T>)span, value0, value1);

        public static bool ContainsAny<T>(this ReadOnlySpan<T> span, T value0, T value1) where T : IEquatable<T>
            => span.IndexOfAny(value0, value1) >= 0;

        public static bool ContainsAny<T>(this Span<T> span, T value0, T value1, T value2) where T : IEquatable<T>
            => ContainsAny((ReadOnlySpan<T>)span, value0, value1, value2);

        public static bool ContainsAny<T>(this ReadOnlySpan<T> span, T value0, T value1, T value2) where T : IEquatable<T>
            => span.IndexOfAny(value0, value1, value2) >= 0;

        public static bool ContainsAny<T>(this Span<T> span, ReadOnlySpan<T> values) where T : IEquatable<T>
            => ContainsAny((ReadOnlySpan<T>)span, values);

        public static bool ContainsAny<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> values) where T : IEquatable<T>
            => span.IndexOfAny(values) >= 0;

        // ---- CopyTo (array source) ----

        /// <summary>
        /// Copies the contents of the array into the span. If the source
        /// and destinations overlap, this method behaves as if the original values in
        /// a temporary location before the destination is overwritten.
        /// </summary>
        public static void CopyTo<T>(this T[]? source, Span<T> destination)
        {
            new ReadOnlySpan<T>(source).CopyTo(destination);
        }

        /// <summary>
        /// Copies the contents of the array into the memory. If the source
        /// and destinations overlap, this method behaves as if the original values are in
        /// a temporary location before the destination is overwritten.
        /// </summary>
        public static void CopyTo<T>(this T[]? source, Memory<T> destination)
        {
            source.CopyTo(destination.Span);
        }
    }
}
