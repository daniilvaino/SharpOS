// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime src/libraries/System.Private.Uri/src/System/UriHostNameType.cs (.NET 10, MIT). Unchanged.

namespace System
{
    public enum UriHostNameType
    {
        Unknown,
        Basic,
        Dns,
        IPv4,
        IPv6
    }
}
