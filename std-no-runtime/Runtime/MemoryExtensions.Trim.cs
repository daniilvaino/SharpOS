// The trimming of a span by an element value, and AsSpan over an array by
// Index/Range. API of dotnet/runtime main (.NET 10, the fork
// dotnet-runtime-sharpos), src/libraries/System.Private.CoreLib/src/System/
// MemoryExtensions.Trim.cs and MemoryExtensions.cs (MIT); bodies are the
// BCL's ClampStart/ClampEnd loops over EqualityComparer<T>.Default, which is
// what the BCL does for a T that is not a bitwise-equatable primitive.
// BigInteger's calculator trims its uint limbs with them.

using System.Collections.Generic;

namespace System
{
    public static partial class MemoryExtensions
    {
        public static Span<T> Trim<T>(this Span<T> span, T trimElement) where T : IEquatable<T>?
        {
            int start = ClampStart(span, trimElement);
            int length = ClampEnd(span, start, trimElement);
            return span.Slice(start, length);
        }

        public static Span<T> TrimStart<T>(this Span<T> span, T trimElement) where T : IEquatable<T>?
            => span.Slice(ClampStart(span, trimElement));

        public static Span<T> TrimEnd<T>(this Span<T> span, T trimElement) where T : IEquatable<T>?
            => span.Slice(0, ClampEnd(span, 0, trimElement));

        public static ReadOnlySpan<T> Trim<T>(this ReadOnlySpan<T> span, T trimElement) where T : IEquatable<T>?
        {
            int start = ClampStart(span, trimElement);
            int length = ClampEnd(span, start, trimElement);
            return span.Slice(start, length);
        }

        public static ReadOnlySpan<T> TrimStart<T>(this ReadOnlySpan<T> span, T trimElement) where T : IEquatable<T>?
            => span.Slice(ClampStart(span, trimElement));

        public static ReadOnlySpan<T> TrimEnd<T>(this ReadOnlySpan<T> span, T trimElement) where T : IEquatable<T>?
            => span.Slice(0, ClampEnd(span, 0, trimElement));

        private static int ClampStart<T>(ReadOnlySpan<T> span, T trimElement) where T : IEquatable<T>?
        {
            int start = 0;
            for (; start < span.Length; start++)
            {
                if (!EqualityComparer<T>.Default.Equals(span[start], trimElement))
                {
                    break;
                }
            }
            return start;
        }

        private static int ClampEnd<T>(ReadOnlySpan<T> span, int start, T trimElement) where T : IEquatable<T>?
        {
            int end = span.Length - 1;
            for (; end >= start; end--)
            {
                if (!EqualityComparer<T>.Default.Equals(span[end], trimElement))
                {
                    break;
                }
            }
            return end - start + 1;
        }

        public static Span<T> AsSpan<T>(this T[]? array, Index startIndex)
        {
            if (array == null)
            {
                if (!startIndex.Equals(Index.Start))
                    throw new ArgumentOutOfRangeException(nameof(startIndex));
                return default;
            }

            int actualIndex = startIndex.GetOffset(array.Length);
            return new Span<T>(array, actualIndex, array.Length - actualIndex);
        }

        public static Span<T> AsSpan<T>(this T[]? array, Range range)
        {
            if (array == null)
            {
                Index startIndex = range.Start;
                Index endIndex = range.End;

                if (!startIndex.Equals(Index.Start) || !endIndex.Equals(Index.Start))
                    throw new ArgumentNullException(nameof(array));

                return default;
            }

            (int start, int length) = range.GetOffsetAndLength(array.Length);
            return new Span<T>(array, start, length);
        }
    }
}
