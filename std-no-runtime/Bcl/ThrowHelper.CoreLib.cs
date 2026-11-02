// The CoreLib throw helpers that std's ports of Collection<T> and Random
// call. Ported from dotnet/runtime main (dotnet-runtime-sharpos/),
// System.Private.CoreLib/src/System/ThrowHelper.cs (MIT), bodies verbatim;
// ExceptionResource carries only the names those ports pass, and its text
// comes from a switch instead of a resource lookup (no enum names here).

using System.Diagnostics.CodeAnalysis;

namespace System
{
    internal static partial class ThrowHelper
    {
        [DoesNotReturn]
        internal static void ThrowNotSupportedException(ExceptionResource resource)
        {
            throw new NotSupportedException(GetResourceString(resource));
        }

        [DoesNotReturn]
        internal static void ThrowArgumentException(ExceptionResource resource)
        {
            throw new ArgumentException(GetResourceString(resource));
        }

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRange_IndexMustBeLessException()
        {
            throw new ArgumentOutOfRangeException("index", SR.ArgumentOutOfRange_IndexMustBeLess);
        }

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRange_IndexMustBeLessOrEqualException()
        {
            throw new ArgumentOutOfRangeException("index", SR.ArgumentOutOfRange_IndexMustBeLessOrEqual);
        }

        [DoesNotReturn]
        internal static void ThrowIndexArgumentOutOfRange_NeedNonNegNumException()
        {
            throw new ArgumentOutOfRangeException("index", SR.ArgumentOutOfRange_NeedNonNegNum);
        }

        [DoesNotReturn]
        internal static void ThrowArgumentException_Argument_IncompatibleArrayType()
        {
            throw new ArgumentException(SR.Argument_IncompatibleArrayType);
        }

        [DoesNotReturn]
        internal static void ThrowWrongValueTypeArgumentException<T>(T value, Type targetType)
        {
            throw new ArgumentException(SR.Format(SR.Arg_WrongType, (object?)value, targetType), nameof(value));
        }

        internal static void IfNullAndNullsAreIllegalThenThrow<T>(object? value, ExceptionArgument argName)
        {
            // Note that default(T) is not equal to null for value types except when T is Nullable<U>.
            if (!(default(T) == null) && value == null)
                ThrowArgumentNullException(argName);
        }

        [DoesNotReturn]
        internal static void ThrowArgumentNullException(ExceptionArgument argument, ExceptionResource resource) =>
            throw new ArgumentNullException(GetArgumentName(argument), GetResourceString(resource));

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRangeException(ExceptionArgument argument, ExceptionResource resource) =>
            throw new ArgumentOutOfRangeException(GetArgumentName(argument), GetResourceString(resource));

        [DoesNotReturn]
        internal static void ThrowNotSupportedException_UnreadableStream() =>
            throw new NotSupportedException(SR.NotSupported_UnreadableStream);

        [DoesNotReturn]
        internal static void ThrowNotSupportedException_UnseekableStream() =>
            throw new NotSupportedException(SR.NotSupported_UnseekableStream);

        [DoesNotReturn]
        internal static void ThrowInvalidOperationException(ExceptionResource resource) =>
            throw new InvalidOperationException(GetResourceString(resource));

        [DoesNotReturn]
        internal static void ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen() =>
            throw new InvalidOperationException(SR.InvalidOperation_EnumOpCantHappen);

        private static string GetResourceString(ExceptionResource resource)
        {
            switch (resource)
            {
                case ExceptionResource.NotSupported_ReadOnlyCollection: return SR.NotSupported_ReadOnlyCollection;
                case ExceptionResource.ConcurrentCollection_SyncRoot_NotSupported: return SR.ConcurrentCollection_SyncRoot_NotSupported;
                case ExceptionResource.Arg_RankMultiDimNotSupported: return SR.Arg_RankMultiDimNotSupported;
                case ExceptionResource.Arg_NonZeroLowerBound: return SR.Arg_NonZeroLowerBound;
                case ExceptionResource.Arg_ArrayPlusOffTooSmall: return SR.Arg_ArrayPlusOffTooSmall;
                case ExceptionResource.Arg_TypeNotSupported: return SR.Arg_TypeNotSupported;
                case ExceptionResource.InvalidOperation_SpanOverlappedOperation: return SR.InvalidOperation_SpanOverlappedOperation;
                case ExceptionResource.ArgumentNull_Array: return SR.ArgumentNull_Array;
                case ExceptionResource.ArgumentOutOfRange_IndexCount: return SR.ArgumentOutOfRange_IndexCount;
                case ExceptionResource.ArgumentOutOfRange_IndexCountBuffer: return SR.ArgumentOutOfRange_IndexCountBuffer;
                case ExceptionResource.ArgumentOutOfRange_IndexMustBeLessOrEqual: return SR.ArgumentOutOfRange_IndexMustBeLessOrEqual;
                case ExceptionResource.ArgumentOutOfRange_NeedNonNegNum: return SR.ArgumentOutOfRange_NeedNonNegNum;
                default: return "";
            }
        }
    }

    internal enum ExceptionResource
    {
        ConcurrentCollection_SyncRoot_NotSupported, // ConcurrentQueue (step223)
        NotSupported_ReadOnlyCollection,
        Arg_RankMultiDimNotSupported,
        Arg_NonZeroLowerBound,
        Arg_ArrayPlusOffTooSmall,
        Arg_TypeNotSupported,
        InvalidOperation_SpanOverlappedOperation, // Ordinal.ToUpperOrdinal
        ArgumentNull_Array, // encodings
        ArgumentOutOfRange_IndexCount, // encodings
        ArgumentOutOfRange_IndexCountBuffer, // encodings
        ArgumentOutOfRange_IndexMustBeLessOrEqual, // encodings
        ArgumentOutOfRange_NeedNonNegNum, // encodings
    }

    internal static partial class SR
    {
        internal const string NotSupported_ReadOnlyCollection = "Collection is read-only.";
        internal const string ArgumentOutOfRange_IndexMustBeLess = "Index was out of range. Must be non-negative and less than the size of the collection.";
        internal const string ArgumentOutOfRange_IndexMustBeLessOrEqual = "Index was out of range. Must be non-negative and less than or equal to the size of the collection.";
        internal const string Arg_EmptySpan = "Span may not be empty.";
    }
}
