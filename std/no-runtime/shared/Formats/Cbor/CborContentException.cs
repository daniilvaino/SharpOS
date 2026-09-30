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

using System.ComponentModel;

namespace System.Formats.Cbor
{
    /// <summary>The exception that's thrown when CBOR data is invalid.</summary>
    public class CborContentException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CborContentException" /> class using the provided message.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        public CborContentException(string? message)
            : base(message ?? SR.CborContentException_DefaultMessage)
        {

        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CborContentException" /> class,
        /// using the provided message and exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="inner">The exception that is the cause of the current exception.</param>
        public CborContentException(string? message, Exception? inner)
            : base(message ?? SR.CborContentException_DefaultMessage, inner)
        {

        }

        // SharpOS cut: the serialization constructor, together with
        // [Serializable] and the System.Runtime.Serialization using above.
        // Binary serialization does not exist here and is obsolete upstream;
        // an exception that can be thrown and caught loses nothing by not
        // being reconstructable from a stream.
    }
}
