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

using System.Globalization;
using System.Numerics;

namespace System.Formats.Cbor
{
    public partial class CborReader
    {
        /// <summary>Reads the next data item as a semantic tag (major type 6).</summary>
        /// <returns>The decoded value.</returns>
        /// <exception cref="InvalidOperationException">The next data item does not have the correct major type.</exception>
        /// <exception cref="CborContentException"><para>The next value has an invalid CBOR encoding.</para>
        /// <para>-or-</para>
        /// <para>There was an unexpected end of CBOR encoding data.</para>
        /// <para>-or-</para>
        /// <para>The next value uses a CBOR encoding that is not valid under the current conformance mode.</para></exception>
        [CLSCompliant(false)]
        public CborTag ReadTag()
        {
            CborTag tag = PeekTagCore(out int bytesRead);

            AdvanceBuffer(bytesRead);
            _isTagContext = true;
            return tag;
        }

        /// <summary>Reads the next data item as a semantic tag (major type 6), without advancing the reader.</summary>
        /// <returns>The decoded value.</returns>
        /// <exception cref="InvalidOperationException">The next data item does not have the correct major type.</exception>
        /// <exception cref="CborContentException"><para>The next value has an invalid CBOR encoding.</para>
        /// <para>-or-</para>
        /// <para>There was an unexpected end of CBOR encoding data.</para>
        /// <para>-or-</para>
        /// <para>The next value uses a CBOR encoding that is not valid under the current conformance mode.</para></exception>
        /// <remarks>Useful in scenarios where the semantic value decoder needs to be determined at run time.</remarks>
        [CLSCompliant(false)]
        public CborTag PeekTag() => PeekTagCore(out int _);

        private void ReadExpectedTag(CborTag expectedTag)
        {
            CborTag tag = PeekTagCore(out int bytesRead);

            if (expectedTag != tag)
            {
                throw new InvalidOperationException(SR.Cbor_Reader_TagMismatch);
            }

            AdvanceBuffer(bytesRead);
            _isTagContext = true;
        }

        private CborTag PeekTagCore(out int bytesRead)
        {
            CborInitialByte header = PeekInitialByte(expectedType: CborMajorType.Tag);
            CborTag result = (CborTag)DecodeUnsignedInteger(header, GetRemainingBytes(), out bytesRead);

            if (_isConformanceModeCheckEnabled && !CborConformanceModeHelpers.AllowsTags(ConformanceMode))
            {
                throw new CborContentException(SR.Format(SR.Cbor_ConformanceMode_TagsNotSupported, ConformanceMode));
            }

            return result;
        }
    }
}
