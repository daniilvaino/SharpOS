// Ported from dotnet/runtime (.NET 10 fork dotnet-runtime-sharpos, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Globalization/TimeSpanStyles.cs
// Verbatim.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Globalization
{
    [Flags]
    public enum TimeSpanStyles
    {
        None = 0x00000000,
        AssumeNegative = 0x00000001,
    }
}
