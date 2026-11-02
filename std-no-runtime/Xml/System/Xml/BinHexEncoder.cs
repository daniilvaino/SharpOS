// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Xml
{
    internal static partial class BinHexEncoder
    {
        // SharpOS cut: CharsChunkSize and Encode(byte[], int, int, XmlWriter) — the
        // writer side of System.Xml is not ported.

        internal static string Encode(byte[] inArray, int offsetIn, int count)
        {
            return Convert.ToHexString(inArray, offsetIn, count);
        }
    } // class
} // namespace
