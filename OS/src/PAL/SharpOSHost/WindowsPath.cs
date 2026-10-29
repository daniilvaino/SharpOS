// The Windows path model the hosted runtime is given: '\' separator, a fake
// drive and working directory C:\sharpos\. Until step215 this was std's
// System.IO.Path; std's Path now has the Unix behaviour the rest of the
// system uses, and this copy keeps GetFullPathNameW answering CoreCLR
// exactly as before. Moved as it was, not changed.

using System;

namespace OS.PAL.SharpOSHost
{
    internal static class WindowsPath
    {
        private const char DirectorySeparatorChar = '\\';
        private const char AltDirectorySeparatorChar = '/';

        private const string CurrentDirectory = "C:\\sharpos\\";

        private static bool IsPathRooted(string path)
        {
            if (path == null || path.Length == 0) return false;
            char c = path[0];
            if (c == DirectorySeparatorChar || c == AltDirectorySeparatorChar) return true;
            if (path.Length >= 2 && path[1] == ':' &&
                ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) return true;
            return false;
        }

        private static bool IsPathFullyQualified(string path)
        {
            if (path == null || path.Length < 2) return false;
            char c0 = path[0];
            if ((c0 == DirectorySeparatorChar || c0 == AltDirectorySeparatorChar) &&
                (path[1] == DirectorySeparatorChar || path[1] == AltDirectorySeparatorChar))
                return true;
            if (path.Length >= 3 && path[1] == ':' &&
                (path[2] == DirectorySeparatorChar || path[2] == AltDirectorySeparatorChar) &&
                ((c0 >= 'A' && c0 <= 'Z') || (c0 >= 'a' && c0 <= 'z')))
                return true;
            return false;
        }

        public static string GetFullPath(string path)
        {
            if (path == null || path.Length == 0)
                return CurrentDirectory;
            if (IsPathFullyQualified(path))
                return Normalize(path);
            if (IsPathRooted(path))
                return Normalize(path);
            return Normalize(CurrentDirectory + path);
        }

        // Resolve '.' and '..' segments, collapse duplicate separators,
        // normalize all separators to '\'.
        private static unsafe string Normalize(string path)
        {
            int n = path.Length;
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
    }
}
