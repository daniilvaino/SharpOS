// The CoreLib throw helpers that std's ports of CharUnicodeInfo and Rune
// call. Ported from dotnet/runtime main (dotnet-runtime-sharpos/),
// System.Private.CoreLib/src/System/ThrowHelper.cs (MIT), bodies verbatim.

using System.Diagnostics.CodeAnalysis;

namespace System
{
    internal static partial class ThrowHelper
    {
        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRangeException(ExceptionArgument argument)
        {
            throw new ArgumentOutOfRangeException(GetArgumentName(argument));
        }

        [DoesNotReturn]
        internal static void ThrowArgumentException_CannotExtractScalar(ExceptionArgument argument)
        {
            throw new ArgumentException(SR.Argument_CannotExtractScalar, GetArgumentName(argument));
        }
    }

    internal static partial class SR
    {
        internal const string Argument_CannotExtractScalar = "Cannot extract a Unicode scalar value from the specified index in the input.";
    }
}
