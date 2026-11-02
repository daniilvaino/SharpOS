// The HResult of System.Private.Xml's own exceptions (its Misc/HResults.cs,
// dotnet-runtime-sharpos, .NET 10): one constant onto std's HResults rather
// than the library's whole copy, which repeats CoreLib's.

namespace System
{
    internal static partial class HResults
    {
        internal const int Xml = unchecked((int)0x80131940);
    }
}
