// SR keys of System.Private.CoreLib (dotnet/runtime v8.0.27 Strings.resx) for the
// encoding hierarchy (Text/Encoding.cs and the rest, BCL 8).

namespace System
{
    internal static partial class SR
    {
        // Upstream's (Common/src/System/SR.cs): true when the resource strings are
        // trimmed away. std keeps them, as constants.
        internal static bool UsingResourceKeys() => false;

        internal const string Arg_ArgumentException = @"Value does not fall within the expected range.";
        internal const string Arg_MustBeRune = @"Object must be of type Rune.";
        internal const string ArgumentNull_Array = @"Array cannot be null.";
        internal const string ArgumentOutOfRange_GetByteCountOverflow = @"Too many characters. The resulting number of bytes is larger than what can be returned as an int.";
        internal const string ArgumentOutOfRange_GetCharCountOverflow = @"Too many bytes. The resulting number of chars is larger than what can be returned as an int.";
        internal const string ArgumentOutOfRange_IndexCount = @"Index and count must refer to a location within the string.";
        internal const string Argument_CodepageNotSupported = @"{0} is not a supported code page.";
        internal const string Argument_ConversionOverflow = @"Conversion buffer overflow.";
        internal const string Argument_EncoderFallbackNotEmpty = @"Must complete Convert() operation or call Encoder.Reset() before calling GetBytes() or GetByteCount(). Encoder '{0}' fallback '{1}'.";
        internal const string Argument_EncodingConversionOverflowBytes = @"The output byte buffer is too small to contain the encoded data, encoding codepage '{0}' and fallback '{1}'.";
        internal const string Argument_EncodingConversionOverflowChars = @"The output char buffer is too small to contain the decoded characters, encoding codepage '{0}' and fallback '{1}'.";
        internal const string Argument_EncodingNotSupported = @"'{0}' is not a supported encoding name. For information on defining a custom encoding, see the documentation for the Encoding.RegisterProvider method.";
        internal const string Argument_FallbackBufferNotEmpty = @"Cannot change fallback when buffer is not empty. Previous Convert() call left data in the fallback buffer.";
        internal const string Argument_InvalidCharSequenceNoIndex = @"String contains invalid Unicode code points.";
        internal const string Argument_InvalidCodePageBytesIndex = @"Unable to translate bytes {0} at index {1} from specified code page to Unicode.";
        internal const string Argument_InvalidCodePageConversionIndex = @"Unable to translate Unicode character \\u{0:X4} at index {1} to specified code page.";
        internal const string Argument_RecursiveFallback = @"Recursive fallback not allowed for character \\u{0:X4}.";
        internal const string Argument_RecursiveFallbackBytes = @"Recursive fallback not allowed for bytes {0}.";
        internal const string Encoding_UTF7_Disabled = @"Support for UTF-7 is disabled. See {0} for more information.";
        internal const string Globalization_cp_1200 = @"Unicode";
        internal const string Globalization_cp_12000 = @"Unicode (UTF-32)";
        internal const string Globalization_cp_12001 = @"Unicode (UTF-32 Big-Endian)";
        internal const string Globalization_cp_1201 = @"Unicode (Big-Endian)";
        internal const string Globalization_cp_20127 = @"US-ASCII";
        internal const string Globalization_cp_28591 = @"Western European (ISO)";
        internal const string Globalization_cp_65000 = @"Unicode (UTF-7)";
        internal const string Globalization_cp_65001 = @"Unicode (UTF-8)";
        internal const string NotSupported_NoCodepageData = @"No data is available for encoding {0}. For information on defining a custom encoding, see the documentation for the Encoding.RegisterProvider method.";
    }
}
