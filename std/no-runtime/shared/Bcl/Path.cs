// System.IO.Path — minimal BCL-compat slice for SharpOS unikernel.
//
// Ported from dotnet/runtime System.Private.CoreLib Path.cs + Path.Windows.cs.
// Cuts:
//   - No filesystem probing (no GetTempPath, GetTempFileName, file APIs).
//   - No UNC / drive-letter handling (we don't have drives).
//   - No \\?\ extended path normalization.
//   - Locale-aware comparisons reduced к Ordinal.
//
// SharpOS path model: single rooted namespace, '\' separator, fake CWD
// `\sharpos\`. Absolute = starts with '\'. Relative = anything else, gets
// CWD prepended.
//
// What's implemented:
//   IsPathRooted   — starts with separator
//   GetFullPath    — root + normalize (resolves '.' и '..')
//   Combine        — join components с separator
//   GetFileName    — substring после last separator
//   GetDirectoryName — substring до last separator
//   GetExtension   — substring после last '.' in filename

namespace System.IO
{
    public static class Path
    {
        public const char DirectorySeparatorChar = '\\';
        public const char AltDirectorySeparatorChar = '/';
        public const char VolumeSeparatorChar = ':';

        private const string CurrentDirectory = "\\sharpos\\";

        public static bool IsPathRooted(string? path)
        {
            if (path == null || path.Length == 0) return false;
            char c = path[0];
            return c == DirectorySeparatorChar || c == AltDirectorySeparatorChar;
        }

        public static string GetFullPath(string path)
        {
            if (path == null || path.Length == 0)
                return CurrentDirectory;
            if (IsPathRooted(path))
                return Normalize(path);
            return Normalize(CurrentDirectory + path);
        }

        // Resolve '.' and '..' segments, collapse duplicate separators,
        // normalize all separators к '\'. Pure string manipulation.
        //
        // Allocates the output via String.FastAllocateString (our std's
        // raw allocator) and writes characters via fixed pointer; the
        // canonical `new string(char[])` ctor path triggers an ILC
        // StringAllocatorMethodNode lookup for a 'Ctor' helper our
        // NoStdLib build doesn't provide.
        public static unsafe string Normalize(string path)
        {
            int n = path.Length;
            // Stackalloc avoids `new string(char[])` ctor path (which our
            // NoStdLib config can't lower — ILC StringAllocatorMethodNode
            // looks for a special "Ctor" method we don't provide).
            char* outPtr = stackalloc char[n + 1];
            int* segStarts = stackalloc int[n + 1];
            int outPos = 0;
            int segDepth = 0;

            for (int i = 0; i < n; )
            {
                char c = path[i];
                if (c == AltDirectorySeparatorChar) c = DirectorySeparatorChar;

                if (c == DirectorySeparatorChar)
                {
                    if (outPos == 0 || outPtr[outPos - 1] != DirectorySeparatorChar)
                    {
                        outPtr[outPos++] = DirectorySeparatorChar;
                        segStarts[segDepth++] = outPos;
                    }
                    i++;
                    continue;
                }

                int segLen = 0;
                while (i + segLen < n)
                {
                    char cc = path[i + segLen];
                    if (cc == DirectorySeparatorChar || cc == AltDirectorySeparatorChar) break;
                    segLen++;
                }

                if (segLen == 1 && path[i] == '.')
                {
                    i += segLen;
                    continue;
                }
                if (segLen == 2 && path[i] == '.' && path[i + 1] == '.')
                {
                    if (segDepth > 1)
                    {
                        segDepth--;
                        outPos = segStarts[segDepth - 1];
                    }
                    else
                    {
                        outPos = (outPos > 0 && outPtr[0] == DirectorySeparatorChar) ? 1 : 0;
                        segDepth = (outPos == 1) ? 1 : 0;
                    }
                    i += segLen;
                    continue;
                }

                for (int k = 0; k < segLen; k++)
                    outPtr[outPos++] = path[i + k];
                i += segLen;
            }

            if (outPos > 1 && outPtr[outPos - 1] == DirectorySeparatorChar) outPos--;
            return String.FromUtf16(outPtr, outPos);
        }

        public static string Combine(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? string.Empty;
            if (string.IsNullOrEmpty(b)) return a;
            if (IsPathRooted(b)) return b;
            char last = a[a.Length - 1];
            if (last == DirectorySeparatorChar || last == AltDirectorySeparatorChar)
                return a + b;
            return a + DirectorySeparatorChar + b;
        }

        public static string? GetFileName(string? path)
        {
            if (path == null) return null;
            int n = path.Length;
            for (int i = n - 1; i >= 0; i--)
            {
                char c = path[i];
                if (c == DirectorySeparatorChar || c == AltDirectorySeparatorChar || c == VolumeSeparatorChar)
                    return path.Substring(i + 1);
            }
            return path;
        }

        public static string? GetDirectoryName(string? path)
        {
            if (path == null) return null;
            int n = path.Length;
            for (int i = n - 1; i >= 0; i--)
            {
                char c = path[i];
                if (c == DirectorySeparatorChar || c == AltDirectorySeparatorChar)
                    return path.Substring(0, i);
            }
            return string.Empty;
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
                if (c == DirectorySeparatorChar || c == AltDirectorySeparatorChar || c == VolumeSeparatorChar)
                    return string.Empty;
            }
            return string.Empty;
        }
    }
}
