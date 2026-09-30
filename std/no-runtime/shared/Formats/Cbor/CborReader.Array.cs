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

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Formats.Cbor
{
    public partial class CborReader
    {
        /// <summary>Reads the next data item as the start of an array (major type 4).</summary>
        /// <returns>The length of the definite-length array, or <see langword="null" /> if the array is indefinite-length.</returns>
        /// <exception cref="InvalidOperationException">The next data item does not have the correct major type.</exception>
        /// <exception cref="CborContentException"><para>The next value has an invalid CBOR encoding.</para>
        /// <para>-or-</para>
        /// <para>There was an unexpected end of CBOR encoding data.</para>
        /// <para>-or-</para>
        /// <para>The next value uses a CBOR encoding that is not valid under the current conformance mode.</para></exception>
        public int? ReadStartArray()
        {
            CborInitialByte header = PeekInitialByte(expectedType: CborMajorType.Array);

            if (header.AdditionalInfo == CborAdditionalInfo.IndefiniteLength)
            {
                if (_isConformanceModeCheckEnabled && CborConformanceModeHelpers.RequiresDefiniteLengthItems(ConformanceMode))
                {
                    throw new CborContentException(SR.Format(SR.Cbor_ConformanceMode_IndefiniteLengthItemsNotSupported, ConformanceMode));
                }

                AdvanceBuffer(1);
                PushDataItem(CborMajorType.Array, null);
                return null;
            }
            else
            {
                ReadOnlySpan<byte> buffer = GetRemainingBytes();
                int arrayLength = DecodeDefiniteLength(header, buffer, out int bytesRead);

                AdvanceBuffer(bytesRead);
                PushDataItem(CborMajorType.Array, arrayLength);
                return arrayLength;
            }
        }

        /// <summary>Reads the end of an array (major type 4).</summary>
        /// <exception cref="InvalidOperationException"><para>The current context is not an array.</para>
        /// <para>-or-</para>
        /// <para>The reader is not at the end of the array.</para></exception>
        /// <exception cref="CborContentException"><para>The next value has an invalid CBOR encoding.</para>
        /// <para>-or-</para>
        /// <para>There was an unexpected end of CBOR encoding data.</para></exception>
        public void ReadEndArray()
        {
            if (_definiteLength is null)
            {
                ValidateNextByteIsBreakByte();
                PopDataItem(expectedType: CborMajorType.Array);
                AdvanceDataItemCounters();
                AdvanceBuffer(1);
            }
            else
            {
                PopDataItem(expectedType: CborMajorType.Array);
                AdvanceDataItemCounters();
            }
        }
    }
}
