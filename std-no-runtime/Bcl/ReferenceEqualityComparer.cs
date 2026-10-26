// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime main (.NET 10, the fork dotnet-runtime-sharpos):
//   src/libraries/System.Private.CoreLib/src/System/Collections/Generic/ReferenceEqualityComparer.cs
// Verbatim.

using System.Runtime.CompilerServices;

namespace System.Collections.Generic
{
    /// <summary>
    /// An <see cref="IEqualityComparer{Object}"/> that uses reference equality (<see cref="object.ReferenceEquals(object?, object?)"/>)
    /// instead of value equality (<see cref="object.Equals(object?)"/>) when comparing two object instances.
    /// </summary>
    public sealed class ReferenceEqualityComparer : IEqualityComparer<object?>, IEqualityComparer
    {
        private ReferenceEqualityComparer() { }

        /// <summary>
        /// Gets the singleton <see cref="ReferenceEqualityComparer"/> instance.
        /// </summary>
        public static ReferenceEqualityComparer Instance { get; } = new ReferenceEqualityComparer();

        /// <summary>
        /// Determines whether two object references refer to the same object instance.
        /// </summary>
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        /// <summary>
        /// Returns a hash code for the specified object. The returned hash code is based on the object
        /// identity, not on the contents of the object.
        /// </summary>
        public int GetHashCode(object? obj)
        {
            // Depending on target framework, RuntimeHelpers.GetHashCode might not be annotated
            // with the proper nullability attribute. We'll suppress any warning that might
            // result.
            return RuntimeHelpers.GetHashCode(obj!);
        }
    }
}
