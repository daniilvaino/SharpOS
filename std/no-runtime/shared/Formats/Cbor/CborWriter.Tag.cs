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

using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace System.Formats.Cbor
{
    public partial class CborWriter
    {
        /// <summary>Assign a semantic tag (major type 6) to the next data item.</summary>
        /// <param name="tag">The value to write.</param>
        /// <exception cref="InvalidOperationException"><para>Writing a new value exceeds the definite length of the parent data item.</para>
        /// <para>-or-</para>
        /// <para>The major type of the encoded value is not permitted in the parent data item.</para>
        /// <para>-or-</para>
        /// <para>The written data is not accepted under the current conformance mode.</para></exception>
        [CLSCompliant(false)]
        public void WriteTag(CborTag tag)
        {
            if (!CborConformanceModeHelpers.AllowsTags(ConformanceMode))
            {
                throw new InvalidOperationException(SR.Format(SR.Cbor_ConformanceMode_TagsNotSupported, ConformanceMode));
            }

            WriteUnsignedInteger(CborMajorType.Tag, (ulong)tag);
            _isTagContext = true;
        }

    }
}
