// SR keys System.Text.Encoding.CodePages shares with CoreLib's SR (dotnet/runtime Strings.resx).
// The library is its own assembly over CoreLib and its System.SR does not
// merge with CoreLib's; stock gives every library its own SR the same way.
// Copied from CoreLib's SR partials (std-no-runtime).

namespace System
{
    internal static partial class SR
    {
        internal static string Format(string resourceFormat, params object?[]? args) =>
            args is null || args.Length == 0 ? resourceFormat : string.Format(resourceFormat, args);

        internal static string Format(string resourceFormat, object? p1) => string.Format(resourceFormat, p1);

        internal static string Format(IFormatProvider? provider, string resourceFormat, params object?[]? args) =>
            string.Format(provider, resourceFormat, args ?? new object?[0]);

        internal static string Format(string resourceFormat, object? p1, object? p2) => string.Format(resourceFormat, p1, p2);

        internal static string Format(string resourceFormat, object? p1, object? p2, object? p3) => string.Format(resourceFormat, p1, p2, p3);

        // Upstream's (Common/src/System/SR.cs): true when the resource strings are
        // trimmed away. Kept here as constants.
        internal static bool UsingResourceKeys() => false;

        internal const string ArgumentOutOfRange_GetByteCountOverflow = @"Too many characters. The resulting number of bytes is larger than what can be returned as an int.";
        internal const string ArgumentOutOfRange_GetCharCountOverflow = @"Too many bytes. The resulting number of chars is larger than what can be returned as an int.";
        internal const string ArgumentOutOfRange_IndexCount = @"Index and count must refer to a location within the string.";
        internal const string ArgumentOutOfRange_IndexCountBuffer = @"Index and count must refer to a location within the buffer.";
        internal const string ArgumentOutOfRange_IndexMustBeLessOrEqual = "Index was out of range. Must be non-negative and less than or equal to the size of the collection.";
        internal const string ArgumentOutOfRange_NeedNonNegNum = "Non-negative number required.";
        internal const string ArgumentOutOfRange_Range = "Valid values are between {0} and {1}, inclusive.";
        internal const string Argument_CodepageNotSupported = @"{0} is not a supported code page.";
        internal const string Argument_EncoderFallbackNotEmpty = @"Must complete Convert() operation or call Encoder.Reset() before calling GetBytes() or GetByteCount(). Encoder '{0}' fallback '{1}'.";
        internal const string Argument_EncodingConversionOverflowBytes = @"The output byte buffer is too small to contain the encoded data, encoding codepage '{0}' and fallback '{1}'.";
        internal const string Argument_EncodingConversionOverflowChars = @"The output char buffer is too small to contain the decoded characters, encoding codepage '{0}' and fallback '{1}'.";
        internal const string Argument_InvalidCharSequenceNoIndex = @"String contains invalid Unicode code points.";
        internal const string Argument_RecursiveFallback = @"Recursive fallback not allowed for character \\u{0:X4}.";
        internal const string NotSupported_NoCodepageData = @"No data is available for encoding {0}. For information on defining a custom encoding, see the documentation for the Encoding.RegisterProvider method.";
    }
}
