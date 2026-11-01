// ThrowHelper members of System.Memory (dotnet/runtime v8.0.27,
// System.Memory/src/System/ThrowHelper.cs) that ReadOnlySequence<T> and
// SequenceReader<T> call (step224).

using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace System
{
    internal static partial class ThrowHelper
    {
        [DoesNotReturn]
        internal static void ThrowInvalidOperationException_EndPositionNotReached() =>
            throw new InvalidOperationException(SR.EndPositionNotReached);

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRangeException_PositionOutOfRange() =>
            throw new ArgumentOutOfRangeException("position");

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRangeException_OffsetOutOfRange() =>
            throw new ArgumentOutOfRangeException(nameof(ExceptionArgument.offset));

        [DoesNotReturn]
        public static void ThrowArgumentValidationException<T>(ReadOnlySequenceSegment<T>? startSegment, int startIndex, ReadOnlySequenceSegment<T>? endSegment)
        {
            if (startSegment == null)
                throw new ArgumentNullException(nameof(ExceptionArgument.startSegment));
            if (endSegment == null)
                throw new ArgumentNullException(nameof(ExceptionArgument.endSegment));
            if (startSegment != endSegment && startSegment.RunningIndex > endSegment.RunningIndex)
                throw new ArgumentOutOfRangeException(nameof(ExceptionArgument.endSegment));
            if ((uint)startSegment.Memory.Length < (uint)startIndex)
                throw new ArgumentOutOfRangeException(nameof(ExceptionArgument.startIndex));
            throw new ArgumentOutOfRangeException(nameof(ExceptionArgument.endIndex));
        }

        [DoesNotReturn]
        public static void ThrowArgumentValidationException(Array? array, int start)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(ExceptionArgument.array));
            if ((uint)start > (uint)array.Length)
                throw new ArgumentOutOfRangeException(nameof(ExceptionArgument.start));
            throw new ArgumentOutOfRangeException(nameof(ExceptionArgument.length));
        }

        [DoesNotReturn]
        public static void ThrowStartOrEndArgumentValidationException(long start)
        {
            if (start < 0)
                throw new ArgumentOutOfRangeException(nameof(ExceptionArgument.start));
            throw new ArgumentOutOfRangeException(nameof(ExceptionArgument.length));
        }
    }
}
