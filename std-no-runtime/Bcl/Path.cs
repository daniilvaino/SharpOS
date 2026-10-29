// System.IO.Path — BCL-compat slice with the Unix behaviour (step215).
//
// Written after dotnet/runtime System.Private.CoreLib Path.cs + Path.Unix.cs
// (GetDirectoryNameOffset, GetFullPathInternal, RemoveRelativeSegments), not
// a file-for-file port. Cuts:
//   - No filesystem probing (GetTempPath, GetTempFileName, Exists).
//   - No span overloads, Join, GetRelativePath, GetRandomFileName.
//
// SharpOS path model: one rooted namespace, '/' separator, ';' between the
// entries of PATH — as the hosted runtime has it, not Unix's ':' (step215). Absolute = starts with '/'. Relative = taken from the
// process's working directory. The hosted runtime keeps its Windows model
// (OS/src/PAL/SharpOSHost/WindowsPath.cs).

namespace System.IO
{
    public static class Path
    {
        public const char DirectorySeparatorChar = '/';
        public const char AltDirectorySeparatorChar = '/';
        public const char VolumeSeparatorChar = '/';
        public const char PathSeparator = ';';

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
