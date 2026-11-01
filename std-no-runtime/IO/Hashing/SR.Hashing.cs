// Resource strings for the System.IO.Hashing port, with the texts of
// dotnet/runtime's System.IO.Hashing Strings.resx (MIT). Only the keys those
// files use that no other SR partial in std already has.

namespace System
{
    internal static partial class SR
    {
        internal const string NotSupported_GetHashCode = @"The GetHashCode method is not supported on this object. Use GetCurrentHash or GetHashAndReset to retrieve the hash code computed by this object.";
    }
}
