// Ported verbatim from dotnet/runtime release/8.0
//   src/libraries/System.Private.CoreLib/src/System/ICustomFormatter.cs (MIT)

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System
{
    /// <summary>
    /// Defines a method that supports custom formatting of the value of an object.
    /// </summary>
    public interface ICustomFormatter
    {
        string Format(string? format, object? arg, IFormatProvider? formatProvider);
    }
}
