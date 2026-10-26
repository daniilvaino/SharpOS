// Resource strings for the IdnMapping port, with the texts of dotnet/runtime's System.Private.CoreLib
// Strings.resx (.NET 10, MIT). Only the keys IdnMapping.cs uses that no other SR partial in std has.

namespace System
{
    internal static partial class SR
    {
        internal const string Argument_IdnBadBidi = @"Left to right characters may not be mixed with right to left characters in IDN labels.";
        internal const string Argument_IdnBadLabelSize = @"IDN labels must be between 1 and 63 characters long.";
        internal const string Argument_IdnBadNameSize = @"IDN names must be between 1 and {0} characters long.";
        internal const string Argument_IdnBadPunycode = @"Invalid IDN encoded string.";
        internal const string Argument_IdnBadStd3 = @"Label contains character '{0}' not allowed with UseStd3AsciiRules";
        internal const string Argument_IdnIllegalName = @"Decoded string is not a valid IDN name.";
        internal const string Argument_InvalidCharSequence = @"Invalid Unicode code point found at index {0}.";
        internal const string ArgumentOutOfRange_IndexCountBuffer = @"Index and count must refer to a location within the buffer.";

        // Char.ConvertToUtf32(string, int) (Char.Statics.cs), which IdnMapping calls.
        internal const string Argument_InvalidHighSurrogate = @"Found a high surrogate char without a following low surrogate at index: {0}. The input may not be in this encoding, or may not contain valid Unicode (UTF-16) characters.";
        internal const string Argument_InvalidLowSurrogate = @"Found a low surrogate char without a preceding high surrogate at index: {0}. The input may not be in this encoding, or may not contain valid Unicode (UTF-16) characters.";
    }
}
