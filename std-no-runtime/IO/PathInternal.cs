// System.IO.PathInternal — the members the ported archive code uses, from
// dotnet/runtime v8.0 src/libraries/Common/src/System/IO/PathInternal.cs,
// PathInternal.Unix.cs and PathInternal.CaseSensitivity.cs (MIT). SharpOS's
// paths are Unix's ('/', rooted at '/'); its one volume is FAT, whose names do
// not differ by case — so comparisons ignore case, as BCL's do on Windows and
// macOS.

using System.Diagnostics.CodeAnalysis;

namespace System.IO
{
    internal static partial class PathInternal
    {
        internal const char DirectorySeparatorChar = '/';
        internal const char AltDirectorySeparatorChar = '/';
        internal const string DirectorySeparatorCharAsString = "/";
        internal static ReadOnlySpan<byte> Utf8DirectorySeparators => "/"u8;

        internal static int GetRootLength(ReadOnlySpan<char> path)
            => path.Length > 0 && IsDirectorySeparator(path[0]) ? 1 : 0;

        internal static bool IsDirectorySeparator(char c) => c == DirectorySeparatorChar;

        internal static bool StartsWithDirectorySeparator(ReadOnlySpan<char> path) => path.Length > 0 && IsDirectorySeparator(path[0]);

        internal static string EnsureTrailingSeparator(string path)
            => EndsInDirectorySeparator(path.AsSpan()) ? path : path + DirectorySeparatorCharAsString;

        internal static bool IsRoot(ReadOnlySpan<char> path)
            => path.Length == GetRootLength(path);

        [return: NotNullIfNotNull(nameof(path))]
        internal static string? TrimEndingDirectorySeparator(string? path) =>
            EndsInDirectorySeparator(path) && !IsRoot(path.AsSpan()) ?
                path!.Substring(0, path.Length - 1) :
                path;

        internal static bool EndsInDirectorySeparator([NotNullWhen(true)] string? path) =>
              !string.IsNullOrEmpty(path) && IsDirectorySeparator(path[path.Length - 1]);

        internal static ReadOnlySpan<char> TrimEndingDirectorySeparator(ReadOnlySpan<char> path) =>
            EndsInDirectorySeparator(path) && !IsRoot(path) ?
                path.Slice(0, path.Length - 1) :
                path;

        internal static bool EndsInDirectorySeparator(ReadOnlySpan<char> path) =>
            path.Length > 0 && IsDirectorySeparator(path[path.Length - 1]);

        // SharpOS: FAT's names do not differ by case.
        internal static StringComparison StringComparison => StringComparison.OrdinalIgnoreCase;

        internal static bool IsCaseSensitive => false;
    }
}
