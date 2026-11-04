// SR keys SharpCompress (System.IO.Compression) shares with CoreLib's SR (dotnet/runtime Strings.resx).
// The library is its own assembly over CoreLib and its System.SR does not
// merge with CoreLib's; stock gives every library its own SR the same way.
// Copied from CoreLib's SR partials (std-no-runtime).

namespace System
{
    internal static partial class SR
    {
        internal static string Format(string resourceFormat, params object?[]? args) =>
            args is null || args.Length == 0 ? resourceFormat : string.Format(resourceFormat, args);

        internal static string Format(string resourceFormat, object? p1) => string.Format(resourceFormat, p1);

        internal static string Format(IFormatProvider? provider, string resourceFormat, params object?[]? args) =>
            string.Format(provider, resourceFormat, args ?? new object?[0]);

        internal static string Format(string resourceFormat, object? p1, object? p2) => string.Format(resourceFormat, p1, p2);

        internal static string Format(string resourceFormat, object? p1, object? p2, object? p3) => string.Format(resourceFormat, p1, p2, p3);

        internal const string ArgumentOutOfRange_Enum = @"Enum value was out of legal range.";
        internal const string NotSupported_UnreadableStream = @"Stream does not support reading.";
        internal const string NotSupported_UnwritableStream = @"Stream does not support writing.";
        internal const string SetLengthRequiresSeekingAndWriting = @"SetLength requires a stream that supports seeking and writing.";
    }
}
