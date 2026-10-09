// Ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/System.Private.CoreLib/src/System/ThrowHelper.cs
// Only the members the ported number-formatting files call; bodies verbatim.
// Internal, and partial so the next port can add its own members here.

using System.Diagnostics.CodeAnalysis;

namespace System
{
    internal static partial class ThrowHelper
    {
        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRangeException()
        {
            throw new ArgumentOutOfRangeException();
        }

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRange_Range<T>(string parameterName, T value, T minInclusive, T maxInclusive)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, SR.Format(SR.ArgumentOutOfRange_Range, minInclusive, maxInclusive));
        }

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRangeException_PrecisionTooLarge()
        {
            throw new ArgumentOutOfRangeException("precision", SR.Format(SR.Argument_PrecisionTooLarge, System.Buffers.StandardFormat.MaxPrecision));
        }

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRangeException_SymbolDoesNotFit()
        {
            throw new ArgumentOutOfRangeException("symbol", SR.Argument_BadFormatSpecifier);
        }

        [DoesNotReturn]
        internal static void ThrowArgumentException_DestinationTooShort()
        {
            throw new ArgumentException(SR.Argument_DestinationTooShort, "destination");
        }

        [DoesNotReturn]
        internal static void ThrowFormatException_BadFormatSpecifier()
        {
            throw new FormatException(SR.Argument_BadFormatSpecifier);
        }
    }
}
