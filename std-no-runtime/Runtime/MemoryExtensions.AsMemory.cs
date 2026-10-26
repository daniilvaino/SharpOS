// Ported from dotnet/runtime release/8.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/MemoryExtensions.cs
//   (AsMemory(this string) — all five overloads)
// Verbatim except ThrowHelper overloads taking ExceptionArgument, which our
// ThrowHelper lacks: direct throws (marked "SharpOS:"). Needs the BCL
// ReadOnlyMemory<T> (Bcl/ReadOnlyMemory.cs), whose owner may be a string.

namespace System
{
    public static partial class MemoryExtensions
    {
        /// <summary>Creates a new <see cref="ReadOnlyMemory{T}"/> over the portion of the target string.</summary>
        /// <param name="text">The target string.</param>
        /// <remarks>Returns default when <paramref name="text"/> is null.</remarks>
        public static ReadOnlyMemory<char> AsMemory(this string? text)
        {
            if (text == null)
                return default;

            return new ReadOnlyMemory<char>(text, 0, text.Length);
        }

        /// <summary>Creates a new <see cref="ReadOnlyMemory{T}"/> over the portion of the target string.</summary>
        /// <param name="text">The target string.</param>
        /// <param name="start">The index at which to begin this slice.</param>
        /// <remarks>Returns default when <paramref name="text"/> is null.</remarks>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// Thrown when the specified <paramref name="start"/> index is not in range (&lt;0 or &gt;text.Length).
        /// </exception>
        public static ReadOnlyMemory<char> AsMemory(this string? text, int start)
        {
            if (text == null)
            {
                if (start != 0)
                    throw new ArgumentOutOfRangeException("start"); // SharpOS: was ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.start)
                return default;
            }

            if ((uint)start > (uint)text.Length)
                throw new ArgumentOutOfRangeException("start"); // SharpOS: was ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.start)

            return new ReadOnlyMemory<char>(text, start, text.Length - start);
        }

        /// <summary>Creates a new <see cref="ReadOnlyMemory{T}"/> over the portion of the target string.</summary>
        /// <param name="text">The target string.</param>
        /// <param name="startIndex">The index at which to begin this slice.</param>
        public static ReadOnlyMemory<char> AsMemory(this string? text, Index startIndex)
        {
            if (text == null)
            {
                if (!startIndex.Equals(Index.Start))
                    throw new ArgumentNullException("text"); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.text)

                return default;
            }

            int actualIndex = startIndex.GetOffset(text.Length);
            if ((uint)actualIndex > (uint)text.Length)
                throw new ArgumentOutOfRangeException(); // SharpOS: was ThrowHelper.ThrowArgumentOutOfRangeException()

            return new ReadOnlyMemory<char>(text, actualIndex, text.Length - actualIndex);
        }

        /// <summary>Creates a new <see cref="ReadOnlyMemory{T}"/> over the portion of the target string.</summary>
        /// <param name="text">The target string.</param>
        /// <param name="start">The index at which to begin this slice.</param>
        /// <param name="length">The desired length for the slice (exclusive).</param>
        /// <remarks>Returns default when <paramref name="text"/> is null.</remarks>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// Thrown when the specified <paramref name="start"/> index or <paramref name="length"/> is not in range.
        /// </exception>
        public static ReadOnlyMemory<char> AsMemory(this string? text, int start, int length)
        {
            if (text == null)
            {
                if (start != 0 || length != 0)
                    throw new ArgumentOutOfRangeException("start"); // SharpOS: was ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.start)
                return default;
            }

            // See comment in Span<T>.Slice for how this works.
            if ((ulong)(uint)start + (ulong)(uint)length > (ulong)(uint)text.Length)
                throw new ArgumentOutOfRangeException("start"); // SharpOS: was ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.start); TARGET_64BIT branch kept

            return new ReadOnlyMemory<char>(text, start, length);
        }

        /// <summary>Creates a new <see cref="ReadOnlyMemory{T}"/> over the portion of the target string.</summary>
        /// <param name="text">The target string.</param>
        /// <param name="range">The range used to indicate the start and length of the sliced string.</param>
        public static ReadOnlyMemory<char> AsMemory(this string? text, Range range)
        {
            if (text == null)
            {
                Index startIndex = range.Start;
                Index endIndex = range.End;

                if (!startIndex.Equals(Index.Start) || !endIndex.Equals(Index.Start))
                    throw new ArgumentNullException("text"); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.text)

                return default;
            }

            (int start, int length) = range.GetOffsetAndLength(text.Length);
            return new ReadOnlyMemory<char>(text, start, length);
        }
    }
}
