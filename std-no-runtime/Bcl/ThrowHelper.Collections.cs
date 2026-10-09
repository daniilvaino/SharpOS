// The throw helpers and resource strings of System.Collections (dotnet/runtime
// main, src/libraries/System.Collections/src/System/Collections/ThrowHelper.cs
// and its Strings.resx, MIT) that std's ports of its collections use
// (OrderedDictionary, step198). Bodies verbatim; SR.Format is std's.

using System.Collections.Generic;

namespace System
{
    internal static partial class ThrowHelper
    {
        internal static void ThrowKeyNotFound<TKey>(TKey key) =>
            throw new KeyNotFoundException(SR.Format(SR.Arg_KeyNotFoundWithKey, key));

        internal static void ThrowDuplicateKey<TKey>(TKey key) =>
            throw new ArgumentException(SR.Format(SR.Argument_AddingDuplicate, key), nameof(key));

        internal static void ThrowConcurrentOperation() =>
            throw new InvalidOperationException(SR.InvalidOperation_ConcurrentOperationsNotSupported);

        internal static void ThrowIndexArgumentOutOfRange() =>
            throw new ArgumentOutOfRangeException("index");

        internal static void ThrowVersionCheckFailed() =>
            throw new InvalidOperationException(SR.InvalidOperation_EnumFailedVersion);
    }

    internal static partial class SR
    {
        internal const string Arg_ArrayPlusOffTooSmall = "Destination array is not long enough to copy all the items in the collection. Check array index and length.";
        internal const string Arg_NonZeroLowerBound = "The lower bound of target array must be zero.";
        internal const string Arg_RankMultiDimNotSupported = "Only single dimensional arrays are supported for the requested action.";
        internal const string Arg_WrongType = "The value '{0}' is not of type '{1}' and cannot be used in this generic collection.";
        internal const string Argument_IncompatibleArrayType = "Target array type is not compatible with the type of items in the collection.";
        internal const string InvalidOperation_ConcurrentOperationsNotSupported = "Operations that change non-concurrent collections must have exclusive access. A concurrent update was performed on this collection and corrupted its state. The collection's state is no longer correct.";
        internal const string Argument_AddingDuplicate = "An item with the same key has already been added. Key: {0}";
        internal const string InvalidOperation_EnumFailedVersion = "Collection was modified after the enumerator was instantiated.";
    }
}
