// Ported from dotnet/runtime 8.0, src/libraries/System.Formats.Cbor (MIT).
//
// Cuts, all of them dependencies we do not have rather than choices:
//   * ReadHalf / WriteHalf and HalfHelpers - no System.Half.
//   * Read/WriteBigInteger, Read/WriteDecimal, Read/WriteDateTimeOffset,
//     Read/WriteUnixTimeSeconds - no BigInteger, decimal or DateTimeOffset.
//     The tag machinery itself (ReadTag / WriteTag) is kept: it has no such
//     dependency, and CTAP2's canonical CBOR forbids tags anyway.
//   * SR is generated from the library's own Strings.resx, with a Format that
//     substitutes {0} positionally and understands no specifiers.
//   * BuildStringFromIndefiniteLengthTextString builds through char[] rather
//     than string.Create, which this std does not have.
//
// Everything else is the original, structure and names included, so a later
// port of Half or BigInteger restores the cut members by deleting a cut rather
// than by redesigning an API. See docs/nativeaot-nostd-kernel-limits.md.


namespace System.Formats.Cbor
{
    internal static class SR
    {
        public const string Argument_EncodeDestinationTooSmall = "The destination is too small to hold the encoded value.";
        public const string CborContentException_DefaultMessage = "The CBOR encoding is invalid.";
        public const string Cbor_ConformanceMode_ContainsDuplicateKeys = "CBOR Conformance mode '{0}' does not support duplicate keys.";
        public const string Cbor_ConformanceMode_IndefiniteLengthItemsNotSupported = "CBOR Conformance mode '{0}' does not support indefinite-length data items.";
        public const string Cbor_ConformanceMode_InvalidSimpleValueEncoding = "CBOR Conformance mode '{0}' does not support simple values in the range 24-31 and must be encoded as small as possible.";
        public const string Cbor_ConformanceMode_KeysNotInSortedOrder = "CBOR keys not sorted in accordance with conformance mode '{0}'.";
        public const string Cbor_ConformanceMode_NonCanonicalIntegerRepresentation = "CBOR Conformance mode '{0}' does not support non-canonical integer representations.";
        public const string Cbor_ConformanceMode_RequiresDefiniteLengthItems = "CBOR Conformance mode '{0}' does not support indefinite-length items.";
        public const string Cbor_ConformanceMode_TagsNotSupported = "CBOR Conformance mode '{0}' does not support tagged values.";
        public const string Cbor_NotAtEndOfDefiniteLengthDataItem = "Not at end of the definite-length data item.";
        public const string Cbor_NotAtEndOfIndefiniteLengthDataItem = "Not at end of the indefinite-length data item.";
        public const string Cbor_PopMajorTypeMismatch = "Cannot perform the requested operation, the current major type context is '{0}'.";
        public const string Cbor_Reader_DefiniteLengthExceedsBufferSize = "Declared definite length of CBOR data item exceeds available buffer size.";
        public const string Cbor_Reader_InvalidBigNumEncoding = "Not a valid tagged bignum encoding.";
        public const string Cbor_Reader_InvalidCbor_IndefiniteLengthStringContainsInvalidDataItem = "Indefinite-length CBOR string nests an invalid data item of major type {0}.";
        public const string Cbor_Reader_InvalidCbor_InvalidIntegerEncoding = "CBOR initial byte contains invalid integer encoding";
        public const string Cbor_Reader_InvalidCbor_InvalidUtf8StringEncoding = "CBOR text string payload is not valid a UTF-8 encoding.";
        public const string Cbor_Reader_InvalidCbor_KeyMissingValue = "The current CBOR map contains an incomplete key/value pair.";
        public const string Cbor_Reader_InvalidCbor_TagNotFollowedByValue = "A CBOR tag should always be followed by a data item.";
        public const string Cbor_Reader_InvalidCbor_UnexpectedBreakByte = "CBOR definite-length data items contains unexpected break byte.";
        public const string Cbor_Reader_InvalidCbor_UnexpectedEndOfBuffer = "Unexpected end of CBOR encoding data.";
        public const string Cbor_Reader_InvalidDateTimeEncoding = "Not a valid tagged RFC3339 DateTime encoding.";
        public const string Cbor_Reader_InvalidDecimalEncoding = "Not a valid tagged decimal encoding.";
        public const string Cbor_Reader_InvalidUnixTimeEncoding = "Not a valid tagged unix time encoding.";
        public const string Cbor_Reader_IsAtRootContext = "CBOR reader is already at the root data item context.";
        public const string Cbor_Reader_MajorTypeMismatch = "Cannot perform the requested operation, the next CBOR data item is of major type '{0}'.";
        public const string Cbor_Reader_NoMoreDataItemsToRead = "No more CBOR data items to read in the current context.";
        public const string Cbor_Reader_NotABooleanEncoding = "CBOR simple value does not encode a boolean value.";
        public const string Cbor_Reader_NotAFloatEncoding = "Data item does not encode a floating point number.";
        public const string Cbor_Reader_NotANullEncoding = "CBOR simple value does not encode null.";
        public const string Cbor_Reader_NotASimpleValueEncoding = "CBOR data item does not encode a simple value.";
        public const string Cbor_Reader_NotIndefiniteLengthString = "CBOR string is not of indefinite length.";
        public const string Cbor_Reader_ReadingAsLowerPrecision = "Attempting to read floating point encoding as a lower-precision value.";
        public const string Cbor_Reader_Skip_InvalidState = "Reader state '{0}' is not at the start of a data item.";
        public const string Cbor_Reader_TagMismatch = "CBOR tag does not match expected value.";
        public const string Cbor_Writer_CannotNestDataItemsInIndefiniteLengthStrings = "Cannot nest data items in indefinite-length CBOR string contexts.";
        public const string Cbor_Writer_DecimalOverflow = "Value was either too large or too small for a Decimal.";
        public const string Cbor_Writer_DefiniteLengthExceeded = "Adding a CBOR data item to the current context exceeds its definite length.";
        public const string Cbor_Writer_IncompleteCborDocument = "Writer contains an incomplete CBOR document.";
        public const string Cbor_Writer_InvalidUtf8String = "Not a valid UTF-8 encoding.";
        public const string Cbor_Writer_MapIncompleteKeyValuePair = "CBOR map incomplete; each key must be followed by a corresponding value.";
        public const string Cbor_Writer_PayloadIsNotValidCbor = "Not a valid CBOR value encoding.";
        public const string Cbor_Writer_ValueCannotBeInfiniteOrNaN = "Value cannot be infinite or NaN.";

        /// <summary>Positional substitution of {0}, {1}, ... and nothing else.</summary>
        /// <remarks>
        /// The library's messages use plain positional holes, so a formatter
        /// that handles those and refuses to pretend about the rest is the
        /// honest shape. A hole with no argument is left as written rather
        /// than dropped: a message missing a piece should look wrong.
        /// </remarks>
        public static string Format(string format, params object[] args)
        {
            if (format == null || args == null || args.Length == 0) return format;

            string result = format;
            for (int i = 0; i < args.Length; i++)
            {
                string hole = "{" + i.ToString() + "}";
                string value = args[i] == null ? "" : args[i].ToString();
                result = result.Replace(hole, value);
            }
            return result;
        }
    }
}
