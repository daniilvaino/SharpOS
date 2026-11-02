// System.HResults and System.NotImplemented, from dotnet/runtime v8.0.27
// System.Private.CoreLib (HResults.cs, NotImplemented.cs, MIT) — the members
// std's ports use; add more from the same file as callers need them.

namespace System
{
    internal static partial class HResults
    {
        internal const int COR_E_ARGUMENT = unchecked((int)0x80070057);
    }

    internal static partial class NotImplemented
    {
        internal static Exception ByDesign => new NotImplementedException();

        internal static Exception ByDesignWithMessage(string message) => new NotImplementedException(message);
    }
}
