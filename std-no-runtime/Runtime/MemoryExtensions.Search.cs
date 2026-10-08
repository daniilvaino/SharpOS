// System.MemoryExtensions — more of the BCL surface, for the vendored
// System.Text.Json reader/writer (vendor/SystemTextJson) and the floating-point
// parser (Number/Number.Parsing.cs). Signatures and results are the BCL's
// (dotnet/runtime src/libraries/System.Private.CoreLib/src/System/MemoryExtensions.cs,
// MemoryExtensions.Trim.cs, MIT); the bodies are scalar loops instead of the BCL's
// SpanHelpers/Vector128 searches, like the rest of this file's first half
// (MemoryExtensions.cs).
//
//   IndexOfAny(value0, value1[, value2]) / IndexOfAny(values)   Span + ReadOnlySpan
//   IndexOfAnyExcept(value) / LastIndexOfAnyExcept(value)       Span + ReadOnlySpan
//   Count(value)                                                Span + ReadOnlySpan
//   Trim / TrimStart / TrimEnd (ReadOnlySpan<char>, whitespace)
//   EqualsOrdinalIgnoreCase (internal, as in the BCL)
//   AsMemory<T>(T[] [, start [, length]])

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    public static partial class MemoryExtensions
    {
        // ---- IndexOfAny ----

        public static int IndexOfAny<T>(this Span<T> span, T value0, T value1) where T : IEquatable<T>
            => IndexOfAny((ReadOnlySpan<T>)span, value0, value1);

        public static int IndexOfAny<T>(this ReadOnlySpan<T> span, T value0, T value1) where T : IEquatable<T>
        {
            for (int i = 0; i < span.Length; i++)
            {
                T item = span[i];
                if (value0.Equals(item) || value1.Equals(item)) return i;
            }
            return -1;
        }

        public static int IndexOfAny<T>(this Span<T> span, T value0, T value1, T value2) where T : IEquatable<T>
            => IndexOfAny((ReadOnlySpan<T>)span, value0, value1, value2);

        public static int IndexOfAny<T>(this ReadOnlySpan<T> span, T value0, T value1, T value2) where T : IEquatable<T>
        {
            for (int i = 0; i < span.Length; i++)
            {
                T item = span[i];
                if (value0.Equals(item) || value1.Equals(item) || value2.Equals(item)) return i;
            }
            return -1;
        }

        public static int IndexOfAny<T>(this Span<T> span, ReadOnlySpan<T> values) where T : IEquatable<T>
            => IndexOfAny((ReadOnlySpan<T>)span, values);

        public static int IndexOfAny<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> values) where T : IEquatable<T>
        {
            for (int i = 0; i < span.Length; i++)
            {
                T item = span[i];
                for (int j = 0; j < values.Length; j++)
                {
                    if (values[j].Equals(item)) return i;
                }
            }
            return -1;
        }

        // ---- IndexOfAnyExcept / LastIndexOfAnyExcept ----

        public static int IndexOfAnyExcept<T>(this Span<T> span, T value) where T : IEquatable<T>
            => IndexOfAnyExcept((ReadOnlySpan<T>)span, value);

        public static int IndexOfAnyExcept<T>(this ReadOnlySpan<T> span, T value) where T : IEquatable<T>
        {
            for (int i = 0; i < span.Length; i++)
            {
                if (!value.Equals(span[i])) return i;
            }
            return -1;
        }

        public static int LastIndexOfAnyExcept<T>(this Span<T> span, T value) where T : IEquatable<T>
            => LastIndexOfAnyExcept((ReadOnlySpan<T>)span, value);

        public static int LastIndexOfAnyExcept<T>(this ReadOnlySpan<T> span, T value) where T : IEquatable<T>
        {
            for (int i = span.Length - 1; i >= 0; i--)
            {
                if (!value.Equals(span[i])) return i;
            }
            return -1;
        }

        // ---- Count ----

        public static int Count<T>(this Span<T> span, T value) where T : IEquatable<T>
            => Count((ReadOnlySpan<T>)span, value);

        public static int Count<T>(this ReadOnlySpan<T> span, T value) where T : IEquatable<T>
        {
            int count = 0;
            for (int i = 0; i < span.Length; i++)
            {
                if (value.Equals(span[i])) count++;
            }
            return count;
        }

        // ---- Trim (whitespace, char.IsWhiteSpace) ----

        public static ReadOnlySpan<char> Trim(this ReadOnlySpan<char> span)
        {
            int start = 0;
            for (; start < span.Length; start++)
            {
                if (!char.IsWhiteSpace(span[start])) break;
            }

            int end = span.Length - 1;
            for (; end > start; end--)
            {
                if (!char.IsWhiteSpace(span[end])) break;
            }

            return span.Slice(start, end - start + 1);
        }

        public static ReadOnlySpan<char> TrimStart(this ReadOnlySpan<char> span)
        {
            int start = 0;
            for (; start < span.Length; start++)
            {
                if (!char.IsWhiteSpace(span[start])) break;
            }
            return span.Slice(start);
        }

        public static ReadOnlySpan<char> TrimEnd(this ReadOnlySpan<char> span)
        {
            int end = span.Length - 1;
            for (; end >= 0; end--)
            {
                if (!char.IsWhiteSpace(span[end])) break;
            }
            return span.Slice(0, end + 1);
        }

        // ---- EqualsOrdinalIgnoreCase (internal in the BCL too) ----

        internal static bool EqualsOrdinalIgnoreCase(this ReadOnlySpan<char> span, ReadOnlySpan<char> value)
            => EqualsCore(span, value, ignoreCase: true);

        // ---- AsMemory ----

        public static Memory<T> AsMemory<T>(this T[]? array)
            => array == null ? default : new Memory<T>(array);

        public static Memory<T> AsMemory<T>(this T[]? array, int start)
        {
            if (array == null)
            {
                if (start != 0) throw new ArgumentOutOfRangeException(nameof(start));
                return default;
            }
            if ((uint)start > (uint)array.Length) throw new ArgumentOutOfRangeException(nameof(start));
            return new Memory<T>(array, start, array.Length - start);
        }

        public static Memory<T> AsMemory<T>(this T[]? array, int start, int length)
        {
            if (array == null)
            {
                if (start != 0 || length != 0) throw new ArgumentOutOfRangeException(nameof(start));
                return default;
            }
            return new Memory<T>(array, start, length);
        }
    }
}
