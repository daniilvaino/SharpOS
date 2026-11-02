// System.SystemException. Ported from dotnet/runtime (.NET 10, the fork
// dotnet-runtime-sharpos, MIT):
//   src/libraries/System.Private.CoreLib/src/System/SystemException.cs
// Cuts: the serialization constructor and [Serializable]/[TypeForwardedFrom].
//
// SharpOS: std's own exceptions still derive from Exception directly
// (SerializationException says so); this is the BCL base for code that names
// it — System.Xml's XmlException (step230).

namespace System
{
    public class SystemException : Exception
    {
        private const int COR_E_SYSTEM = unchecked((int)0x80131501);

        public SystemException()
            : base("System error.")
        {
            HResult = COR_E_SYSTEM;
        }

        public SystemException(string? message)
            : base(message)
        {
            HResult = COR_E_SYSTEM;
        }

        public SystemException(string? message, Exception? innerException)
            : base(message, innerException)
        {
            HResult = COR_E_SYSTEM;
        }
    }
}
