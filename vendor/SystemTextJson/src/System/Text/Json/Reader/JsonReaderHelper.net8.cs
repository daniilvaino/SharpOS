// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Runtime.CompilerServices;

namespace System.Text.Json
{
    internal static partial class JsonReaderHelper
    {
        // SharpOS: upstream is span.IndexOfAny(SearchValues<byte> s_controlQuoteBackslash) - a vectorized
        // search; std has no SearchValues. Same contract, scalar loop:
        // '"', '\',  or any control characters (i.e. 0 to 31). https://tools.ietf.org/html/rfc8259
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int IndexOfQuoteOrAnyControlOrBackSlash(this ReadOnlySpan<byte> span)
        {
            for (int i = 0; i < span.Length; i++)
            {
                byte b = span[i];
                if (b == JsonConstants.Quote || b == JsonConstants.BackSlash || b < JsonConstants.Space)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
