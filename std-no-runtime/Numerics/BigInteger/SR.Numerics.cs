// The resource strings and throw helpers System.Numerics.BigInteger uses:
// texts verbatim from dotnet/runtime main (.NET 10, the fork
// dotnet-runtime-sharpos), src/libraries/System.Runtime.Numerics/src/
// Resources/Strings.resx and src/System/ThrowHelper.cs (MIT); constants
// rather than resource lookups, as the rest of std's SR.

using System.Diagnostics.CodeAnalysis;

namespace System
{
    internal static partial class SR
    {
        internal const string Argument_MustBeBigInt = "The parameter must be a BigInteger.";
        internal const string Overflow_Negative_Unsigned = "Negative values do not have an unsigned representation.";
        internal const string Overflow_BigIntInfinity = "BigInteger cannot represent infinity.";
        internal const string Overflow_NotANumber = "The value is not a number.";
        internal const string Overflow_ParseBigInteger = "The value could not be parsed.";
        internal const string Argument_InvalidHexStyle = "With the AllowHexSpecifier bit set in the enum bit field, the only other valid bits that can be combined into the enum value must be a subset of those in HexNumber.";
        internal const string Format_TooLarge = "The value is too large to be represented by this format specifier.";
    }

    internal static partial class ThrowHelper
    {
        [DoesNotReturn]
        internal static void ThrowOverflowException()
        {
            throw new OverflowException();
        }
    }
}
