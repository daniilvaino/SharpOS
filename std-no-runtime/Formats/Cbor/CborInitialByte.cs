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

namespace System.Formats.Cbor
{
    /// <summary>
    ///   Represents CBOR Major Types, as defined in RFC7049 section 2.1.
    /// </summary>
    internal enum CborMajorType : byte
    {
        UnsignedInteger = 0,
        NegativeInteger = 1,
        ByteString = 2,
        TextString = 3,
        Array = 4,
        Map = 5,
        Tag = 6,
        Simple = 7,
    }

    /// <summary>
    ///   Represents the 5-bit additional information included in a CBOR initial byte.
    /// </summary>
    internal enum CborAdditionalInfo : byte
    {
        Additional8BitData = 24,
        Additional16BitData = 25,
        Additional32BitData = 26,
        Additional64BitData = 27,
        IndefiniteLength = 31,
    }

    /// <summary>
    ///   Represents a CBOR initial byte
    /// </summary>
    internal readonly struct CborInitialByte
    {
        public const byte IndefiniteLengthBreakByte = 0xff;
        public const byte AdditionalInformationMask = 0b000_11111;

        public byte InitialByte { get; }

        public CborInitialByte(CborMajorType majorType, CborAdditionalInfo additionalInfo)
        {
            Debug.Assert((byte)majorType < 8, "CBOR Major Type is out of range");
            Debug.Assert((byte)additionalInfo < 32, "CBOR initial byte additional info is out of range");

            InitialByte = (byte)(((byte)majorType << 5) | (byte)additionalInfo);
        }

        public CborInitialByte(byte initialByte)
        {
            InitialByte = initialByte;
        }

        public CborMajorType MajorType => (CborMajorType)(InitialByte >> 5);
        public CborAdditionalInfo AdditionalInfo => (CborAdditionalInfo)(InitialByte & AdditionalInformationMask);
    }
}
