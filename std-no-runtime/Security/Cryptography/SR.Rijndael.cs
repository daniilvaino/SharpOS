// The .NET Framework texts (mscorlib.txt) for RijndaelManagedTransform's keys
// that .NET 8's Strings.resx no longer has.

namespace System
{
    internal static partial class SR
    {
        internal const string Cryptography_InsufficientBuffer = "Input buffer contains insufficient data.";
        internal const string Cryptography_PKCS7_InvalidPadding = "Padding is invalid and cannot be removed.";
        internal const string Cryptography_SSD_InvalidDataSize = "Length of the data to decrypt is invalid.";
        internal const string Cryptography_SSE_InvalidDataSize = "Length of the data to encrypt is invalid.";
    }
}
