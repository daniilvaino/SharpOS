// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/System.Private.CoreLib/src/System/MidpointRounding.cs
// Verbatim. Used by decimal.Round / Math.Round(decimal, ...) and Decimal.DecCalc.

namespace System
{
    public enum MidpointRounding
    {
        ToEven = 0,
        AwayFromZero = 1,
        ToZero = 2,
        ToNegativeInfinity = 3,
        ToPositiveInfinity = 4
    }
}
