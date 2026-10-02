// Resource strings for the ported CoreLib number-formatting files, with the
// texts of dotnet/runtime's System.Private.CoreLib Strings.resx (MIT). Only
// the keys those files use (release/8.0 texts). Format mirrors CoreLib's SR.Format: a plain
// string.Format, with no resource lookup in between.
//
// Internal to std. Code in System.Formats.Cbor keeps resolving to its own SR
// (the nearer namespace wins).

namespace System
{
    internal static partial class SR
    {
        internal const string Arg_HexBinaryStylesNotSupported = "The number styles AllowHexSpecifier and AllowBinarySpecifier are not supported on floating point data types.";
        internal const string Arg_InvalidHexBinaryStyle = "With the AllowHexSpecifier or AllowBinarySpecifier bit set in the enum bit field, the only other valid bits that can be combined into the enum value must be AllowLeadingWhite and AllowTrailingWhite.";
        internal const string Argument_BadFormatSpecifier = "Format specifier was invalid.";
        internal const string Argument_EmptyString = "The value cannot be an empty string.";
        internal const string Argument_InvalidDigitSubstitution = "The DigitSubstitution property must be of a valid member of the DigitShapes enumeration. Valid entries include Context, NativeNational or None.";
        internal const string Argument_InvalidGroupSize = "Every element in the value array should be between one and nine, except for the last element, which can be zero.";
        internal const string Argument_InvalidNativeDigitCount = "The NativeDigits array must contain exactly ten members.";
        internal const string Argument_InvalidNativeDigitValue = "Each member of the NativeDigits array must be a single text element (one or more UTF-16 code points) with a Unicode Nd (Number, Decimal Digit) property indicating it is a digit.";
        internal const string Argument_InvalidNumberStyles = "An undefined NumberStyles value is being used.";
        internal const string ArgumentOutOfRange_NeedNonNegNum = "Non-negative number required.";
        internal const string ArgumentNull_ArrayValue = "Found a null value within an array.";
        internal const string ArgumentOutOfRange_Range = "Valid values are between {0} and {1}, inclusive.";
        internal const string InvalidOperation_ReadOnly = "Instance is read-only.";

        internal static string Format(string resourceFormat, object? p1) => string.Format(resourceFormat, p1);

        internal static string Format(string resourceFormat, object? p1, object? p2) => string.Format(resourceFormat, p1, p2);

        internal static string Format(string resourceFormat, object? p1, object? p2, object? p3) => string.Format(resourceFormat, p1, p2, p3);
    }
}
