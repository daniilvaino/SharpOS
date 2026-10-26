// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime src/libraries/System.Private.CoreLib/src/System/Collections/IHashCodeProvider.cs (.NET 10, MIT).

namespace System.Collections
{
    /// <summary>
    /// Provides a mechanism for a <see cref="Hashtable"/> user to override the default
    /// GetHashCode() function on Objects, providing their own hash function.
    /// </summary>
    [Obsolete("IHashCodeProvider has been deprecated. Use IEqualityComparer instead.")]
    // SharpOS cut: [TypeForwardedFrom(...)] - binary-serialization metadata, not in std.
    public interface IHashCodeProvider
    {
        /// <summary>Returns a hash code for the given object.</summary>
        int GetHashCode(object obj);
    }
}
