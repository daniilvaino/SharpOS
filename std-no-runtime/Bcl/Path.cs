// System.IO.Path — BCL-compat slice with the Unix behaviour (step215).
//
// Written after dotnet/runtime System.Private.CoreLib Path.cs + Path.Unix.cs
// (GetDirectoryNameOffset, GetFullPathInternal, RemoveRelativeSegments), not
// a file-for-file port. Cuts:
//   - No GetTempFileName, GetRelativePath, GetRandomFileName. Exists probes
//     the file system, so it is the app tier's (sdk/Path.AppHost.cs).
//   - Span overloads only for GetFileName and GetDirectoryName; Join,
//     TrimEndingDirectorySeparator, EndsInDirectorySeparator, GetTempPath —
//     release 0.1, for the archive ports.
//
// SharpOS path model: one rooted namespace, '/' separator, ';' between the
// entries of PATH — as the hosted runtime has it, not Unix's ':' (step215). Absolute = starts with '/'. Relative = taken from the
// process's working directory. The hosted runtime keeps its Windows model
// (OS/src/PAL/SharpOSHost/WindowsPath.cs).

namespace System.IO
{
    public static partial class Path
    {
        public const char DirectorySeparatorChar = '/';
        public const char AltDirectorySeparatorChar = '/';
        public const char VolumeSeparatorChar = '/';
        public const char PathSeparator = ';';

        // Unix's (BCL Path.Unix.cs): only NUL is invalid in a name, and '/'
        // as well in a file name.
        public static char[] GetInvalidFileNameChars() => new char[] { '\0', '/' };

        public static char[] GetInvalidPathChars() => new char[] { '\0' };

        public static string? GetPathRoot(string? path)
        {
            if (path == null || path.Length == 0) return null;
            return IsPathRooted(path) ? "/" : string.Empty;
        }

        public static ReadOnlySpan<char> GetPathRoot(ReadOnlySpan<char> path) =>
            path.Length > 0 && path[0] == DirectorySeparatorChar ? "/".AsSpan() : ReadOnlySpan<char>.Empty;

        public static bool IsPathRooted(string? path)
            => path != null && path.Length > 0 && path[0] == DirectorySeparatorChar;

        public static bool IsPathFullyQualified(string? path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            return IsPathRooted(path);
        }

        public static string GetFullPath(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (path.Length == 0) throw new ArgumentException("The value cannot be an empty string.", nameof(path));
            if (path.IndexOf('\0') >= 0) throw new ArgumentException("Illegal characters in path.", nameof(path));
            return GetFullPathInternal(path);
        }

        public static string GetFullPath(string path, string basePath)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (basePath == null) throw new ArgumentNullException(nameof(basePath));
            if (!IsPathFullyQualified(basePath)) throw new ArgumentException("Basepath argument is not fully qualified.", nameof(basePath));
            if (basePath.IndexOf('\0') >= 0 || path.IndexOf('\0') >= 0) throw new ArgumentException("Illegal characters in path.");

            if (IsPathFullyQualified(path)) return GetFullPathInternal(path);
            return GetFullPathInternal(Combine(basePath, path));
        }

        private static string GetFullPathInternal(string path)
        {
            if (!IsPathRooted(path))
                path = Combine(Environment.CurrentDirectory, path);

            string collapsed = RemoveRelativeSegments(path);
            return collapsed.Length == 0 ? "/" : collapsed;
        }

        // BCL PathInternal.RemoveRelativeSegments for a rooted Unix path:
        // "." dropped, ".." takes the segment before it (never the root),
        // repeated separators collapsed; a trailing separator stays.
        private static string RemoveRelativeSegments(string path)
        {
            var sb = new System.Text.StringBuilder(path.Length);
            sb.Append('/');
            int i = 1;
            while (i < path.Length)
            {
                char c = path[i];
                if (c == '/')
                {
                    if (sb[sb.Length - 1] != '/') sb.Append('/');
                    i++;
                    continue;
                }

                int end = i;
                while (end < path.Length && path[end] != '/') end++;
                int length = end - i;

                if (length == 1 && c == '.')
                {
                    i = end + 1;
                    continue;
                }
                if (length == 2 && c == '.' && path[i + 1] == '.')
                {
                    // Back over the separator ending the previous segment, then the segment.
                    int keep = sb.Length - 1;
                    while (keep > 0 && sb[keep - 1] != '/') keep--;
                    if (keep < 1) keep = 1;
                    sb.Length = keep;
                    i = end + 1;
                    continue;
                }

                sb.Append(path, i, length);
                if (end < path.Length) sb.Append('/');
                i = end + 1;
            }
            return sb.ToString();
        }

        // BCL HasExtension: a '.' in the file name that is not its last character.
        public static bool HasExtension(string? path)
        {
            if (path == null) return false;
            for (int i = path.Length - 1; i >= 0; i--)
            {
                char c = path[i];
                if (c == '.') return i != path.Length - 1;
                if (c == DirectorySeparatorChar) break;
            }
            return false;
        }

        public static string Combine(string path1, string path2)
        {
            if (path1 == null) throw new ArgumentNullException(nameof(path1));
            if (path2 == null) throw new ArgumentNullException(nameof(path2));
            if (path2.Length == 0) return path1;
            if (path1.Length == 0 || IsPathRooted(path2)) return path2;
            return path1[path1.Length - 1] == DirectorySeparatorChar
                ? path1 + path2
                : path1 + DirectorySeparatorChar + path2;
        }

        public static string Combine(string path1, string path2, string path3)
            => Combine(Combine(path1, path2), path3);

        public static string Combine(string path1, string path2, string path3, string path4)
            => Combine(Combine(Combine(path1, path2), path3), path4);

        public static string? GetFileName(string? path)
        {
            if (path == null) return null;
            int i = path.LastIndexOf(DirectorySeparatorChar);
            return i < 0 ? path : path.Substring(i + 1);
        }

        // BCL GetDirectoryName: null for a root or an empty path; separators
        // before the file name trimmed, but never the root's.
        public static string? GetDirectoryName(string? path)
        {
            if (path == null || path.Length == 0) return null;
            int rootLength = IsPathRooted(path) ? 1 : 0;
            int end = path.Length;
            if (end <= rootLength) return null;

            while (end > rootLength && path[--end] != DirectorySeparatorChar) ;
            while (end > rootLength && path[end - 1] == DirectorySeparatorChar) end--;
            return CollapseSeparators(path.Substring(0, end));
        }

        // BCL PathInternal.NormalizeDirectorySeparators on Unix: runs of '/' become one.
        private static string CollapseSeparators(string path)
        {
            if (path.IndexOf("//", StringComparison.Ordinal) < 0) return path;
            var sb = new System.Text.StringBuilder(path.Length);
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == DirectorySeparatorChar && i > 0 && path[i - 1] == DirectorySeparatorChar) continue;
                sb.Append(path[i]);
            }
            return sb.ToString();
        }

        public static ReadOnlySpan<char> GetFileName(ReadOnlySpan<char> path)
        {
            int i = path.LastIndexOf(DirectorySeparatorChar);
            return i < 0 ? path : path.Slice(i + 1);
        }

        // BCL's span overload: as the string one, without collapsing separators.
        public static ReadOnlySpan<char> GetDirectoryName(ReadOnlySpan<char> path)
        {
            if (path.IsEmpty) return ReadOnlySpan<char>.Empty;
            int rootLength = path[0] == DirectorySeparatorChar ? 1 : 0;
            int end = path.Length;
            if (end <= rootLength) return ReadOnlySpan<char>.Empty;
            while (end > rootLength && path[--end] != DirectorySeparatorChar) ;
            while (end > rootLength && path[end - 1] == DirectorySeparatorChar) end--;
            return path.Slice(0, end);
        }

        // Join: no rooting rules, unlike Combine — a separator between the
        // parts when neither has one at the seam (BCL Path.Join).
        public static string Join(ReadOnlySpan<char> path1, ReadOnlySpan<char> path2)
        {
            if (path1.Length == 0) return path2.ToString();
            if (path2.Length == 0) return path1.ToString();
            bool separated = path1[path1.Length - 1] == DirectorySeparatorChar || path2[0] == DirectorySeparatorChar;
            return separated ? string.Concat(path1, path2) : string.Concat(path1, DirectorySeparatorCharAsSpan, path2);
        }

        public static string Join(ReadOnlySpan<char> path1, ReadOnlySpan<char> path2, ReadOnlySpan<char> path3)
            => Join(Join(path1, path2).AsSpan(), path3);

        public static string Join(string? path1, string? path2) => Join(path1.AsSpan(), path2.AsSpan());

        public static string Join(string? path1, string? path2, string? path3) => Join(path1.AsSpan(), path2.AsSpan(), path3.AsSpan());

        private static ReadOnlySpan<char> DirectorySeparatorCharAsSpan => "/";

        public static string TrimEndingDirectorySeparator(string path) => PathInternal.TrimEndingDirectorySeparator(path);

        public static ReadOnlySpan<char> TrimEndingDirectorySeparator(ReadOnlySpan<char> path) => PathInternal.TrimEndingDirectorySeparator(path);

        public static bool EndsInDirectorySeparator(string? path) => PathInternal.EndsInDirectorySeparator(path);

        public static bool EndsInDirectorySeparator(ReadOnlySpan<char> path) => PathInternal.EndsInDirectorySeparator(path);

        /// <summary>BCL's on Unix: TMPDIR, or /tmp/, with a separator at the end. Nothing is created.</summary>
        public static string GetTempPath()
        {
            string? tmp = Environment.GetEnvironmentVariable("TMPDIR");
            if (string.IsNullOrEmpty(tmp)) return "/tmp/";
            return PathInternal.EnsureTrailingSeparator(tmp);
        }

        public static string? GetFileNameWithoutExtension(string? path)
        {
            if (path == null) return null;
            string fileName = GetFileName(path)!;
            int dot = fileName.LastIndexOf('.');
            return dot < 0 ? fileName : fileName.Substring(0, dot);
        }

        public static string? GetExtension(string? path)
        {
            if (path == null) return null;
            int n = path.Length;
            for (int i = n - 1; i >= 0; i--)
            {
                char c = path[i];
                if (c == '.')
                    return (i == n - 1) ? string.Empty : path.Substring(i);
                if (c == DirectorySeparatorChar)
                    return string.Empty;
            }
            return string.Empty;
        }
    }
}
