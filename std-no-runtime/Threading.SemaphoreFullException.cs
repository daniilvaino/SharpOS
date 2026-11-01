// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From dotnet/runtime v8.0.27 System.Private.CoreLib/src/System/Threading/SemaphoreFullException.cs.
// SharpOS cut: [Serializable], [TypeForwardedFrom], the serialization
// constructor; the base is Exception — std has no SystemException.

namespace System.Threading
{
    public class SemaphoreFullException : Exception
    {
        public SemaphoreFullException() : base(SR.Threading_SemaphoreFullException)
        {
        }

        public SemaphoreFullException(string? message) : base(message)
        {
        }

        public SemaphoreFullException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}
