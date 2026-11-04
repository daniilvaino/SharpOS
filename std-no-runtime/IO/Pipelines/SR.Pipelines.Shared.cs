// SR keys System.Text.Json (Pipelines, Encodings.Web) shares with CoreLib's SR (dotnet/runtime Strings.resx).
// The library is its own assembly over CoreLib and its System.SR does not
// merge with CoreLib's; stock gives every library its own SR the same way.
// Copied from CoreLib's SR partials (std-no-runtime).

namespace System
{
    internal static partial class SR
    {
        internal const string NotSupported_UnreadableStream = @"Stream does not support reading.";
        internal const string NotSupported_UnwritableStream = @"Stream does not support writing.";
        internal const string ObjectDisposed_StreamClosed = @"Cannot access a closed Stream.";
    }
}
