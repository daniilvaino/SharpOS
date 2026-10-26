// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime src/libraries/System.Private.Uri/src/System/UriFormatException.cs (.NET 10, MIT).
// Cuts: [Serializable], [TypeForwardedFrom], ISerializable, the serialization ctor and
// ISerializable.GetObjectData - std has no binary serialization.

namespace System
{
    /// <summary>
    /// An exception class used when an invalid Uniform Resource Identifier is detected.
    /// </summary>
    // SharpOS cut: [Serializable], [TypeForwardedFrom(...)], ISerializable.
    public class UriFormatException : FormatException
    {
        public UriFormatException() : base()
        {
        }

        public UriFormatException(string? textString) : base(textString)
        {
        }

        public UriFormatException(string? textString, Exception? e) : base(textString, e)
        {
        }

        // SharpOS cut: protected UriFormatException(SerializationInfo, StreamingContext) and
        // ISerializable.GetObjectData - no binary serialization in std.
    }
}
