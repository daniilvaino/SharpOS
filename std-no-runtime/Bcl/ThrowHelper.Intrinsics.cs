// ThrowHelper members the System.Runtime.Intrinsics port uses (release 0.1),
// with the bodies of dotnet/runtime v8.0 System.Private.CoreLib ThrowHelper.cs
// (MIT). Only the ones no other ThrowHelper partial in std already has.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Intrinsics;

namespace System
{
    internal static partial class ThrowHelper
    {
        [DoesNotReturn]
        internal static void ThrowAccessViolationException()
        {
            throw new AccessViolationException();
        }

        [DoesNotReturn]
        internal static void ThrowStartIndexArgumentOutOfRange_ArgumentOutOfRange_IndexMustBeLess()
        {
            throw new ArgumentOutOfRangeException("startIndex", SR.ArgumentOutOfRange_IndexMustBeLess);
        }

        [DoesNotReturn]
        internal static void ThrowNotSupportedException()
        {
            throw new NotSupportedException();
        }

        // System.IO.Hashing's (VectorHelper), from its src/System/ThrowHelper.cs.
        [DoesNotReturn]
        internal static void ThrowUnreachableException() => throw new System.Diagnostics.UnreachableException();

        internal static void ThrowForUnsupportedIntrinsicsVector64BaseType<T>()
        {
            if (!Vector64<T>.IsSupported)
            {
                ThrowNotSupportedException(ExceptionResource.Arg_TypeNotSupported);
            }
        }

        internal static void ThrowForUnsupportedIntrinsicsVector128BaseType<T>()
        {
            if (!Vector128<T>.IsSupported)
            {
                ThrowNotSupportedException(ExceptionResource.Arg_TypeNotSupported);
            }
        }

        internal static void ThrowForUnsupportedIntrinsicsVector256BaseType<T>()
        {
            if (!Vector256<T>.IsSupported)
            {
                ThrowNotSupportedException(ExceptionResource.Arg_TypeNotSupported);
            }
        }

        internal static void ThrowForUnsupportedIntrinsicsVector512BaseType<T>()
        {
            if (!Vector512<T>.IsSupported)
            {
                ThrowNotSupportedException(ExceptionResource.Arg_TypeNotSupported);
            }
        }
    }

    internal static partial class SR
    {
        internal const string NotSupported_Type = "Type is not supported.";
        internal const string Arg_TypeNotSupported = "Specified type is not supported";
    }
}
