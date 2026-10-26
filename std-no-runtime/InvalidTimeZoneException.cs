// Ported from dotnet/runtime (.NET 10 fork dotnet-runtime-sharpos, MIT):
//   src/libraries/System.Private.CoreLib/src/System/InvalidTimeZoneException.cs
// SharpOS cut: [Serializable], [TypeForwardedFrom], the serialization constructor.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Runtime.CompilerServices;
// SharpOS cut: using System.Runtime.Serialization; (no binary serialization here)

namespace System
{
    // SharpOS cut: [Serializable], [TypeForwardedFrom(...)]
    public class InvalidTimeZoneException : Exception
    {
        public InvalidTimeZoneException()
        {
        }

        public InvalidTimeZoneException(string? message)
            : base(message)
        {
        }

        public InvalidTimeZoneException(string? message, Exception? innerException)
            : base(message, innerException)
        {
        }

#if false // SharpOS cut: serialization constructor
        [Obsolete(Obsoletions.LegacyFormatterImplMessage, DiagnosticId = Obsoletions.LegacyFormatterImplDiagId, UrlFormat = Obsoletions.SharedUrlFormat)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        protected InvalidTimeZoneException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
#endif

    }
}
