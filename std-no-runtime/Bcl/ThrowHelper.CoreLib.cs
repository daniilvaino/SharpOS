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

        private static string GetResourceString(ExceptionResource resource)
        {
            switch (resource)
            {
                case ExceptionResource.NotSupported_ReadOnlyCollection: return SR.NotSupported_ReadOnlyCollection;
                case ExceptionResource.Arg_RankMultiDimNotSupported: return SR.Arg_RankMultiDimNotSupported;
                case ExceptionResource.Arg_NonZeroLowerBound: return SR.Arg_NonZeroLowerBound;
                case ExceptionResource.Arg_ArrayPlusOffTooSmall: return SR.Arg_ArrayPlusOffTooSmall;
                default: return "";
            }
        }
    }

    internal enum ExceptionResource
    {
        NotSupported_ReadOnlyCollection,
        Arg_RankMultiDimNotSupported,
        Arg_NonZeroLowerBound,
        Arg_ArrayPlusOffTooSmall,
    }

    internal static partial class SR
    {
        internal const string NotSupported_ReadOnlyCollection = "Collection is read-only.";
        internal const string ArgumentOutOfRange_IndexMustBeLess = "Index was out of range. Must be non-negative and less than the size of the collection.";
        internal const string ArgumentOutOfRange_IndexMustBeLessOrEqual = "Index was out of range. Must be non-negative and less than or equal to the size of the collection.";
        internal const string Arg_EmptySpan = "Span may not be empty.";
    }
}
