// Ported from dotnet/runtime release/8.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Diagnostics/CodeAnalysis/SetsRequiredMembersAttribute.cs
// Verbatim.
// SYSTEM_PRIVATE_CORELIB is not defined in our builds, so the type is internal, as in
// any non-CoreLib assembly that compiles this file; std compiles into each image.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// Specifies that this constructor sets all required members for the current type, and callers
    /// do not need to set any required members themselves.
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
#if SYSTEM_PRIVATE_CORELIB
    public
#else
    internal
#endif
        sealed class SetsRequiredMembersAttribute : Attribute
    { }
}
