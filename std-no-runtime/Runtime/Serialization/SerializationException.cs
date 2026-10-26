// Ported from dotnet/runtime (.NET 10 fork dotnet-runtime-sharpos, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Runtime/Serialization/SerializationException.cs
// Thrown by TimeZoneInfo.FromSerializedString (TimeZoneInfo.StringSerializer.cs).
//
// SharpOS cuts: [Serializable], [TypeForwardedFrom], the serialization constructor;
// the base is Exception (std has no SystemException).

namespace System.Runtime.Serialization
{
    public class SerializationException : Exception // SharpOS: SystemException in upstream
    {
        private const int COR_E_SERIALIZATION = unchecked((int)0x8013150C); // SharpOS: HResults.COR_E_SERIALIZATION

        /// <summary>
        /// Creates a new SerializationException with its message
        /// string set to a default message.
        /// </summary>
        public SerializationException()
            : base(SR.SerializationException)
        {
            HResult = COR_E_SERIALIZATION;
        }

        public SerializationException(string? message)
            : base(message ?? SR.SerializationException)
        {
            HResult = COR_E_SERIALIZATION;
        }

        public SerializationException(string? message, Exception? innerException)
            : base(message ?? SR.SerializationException, innerException)
        {
            HResult = COR_E_SERIALIZATION;
        }
    }
}
