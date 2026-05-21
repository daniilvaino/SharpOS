using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step 99 pass 7 -- FS query exports. PAL Win32 FS getters
    // (GetFileAttributesW, GetFileAttributesExW, FindFirstFileW, ...)
    // forward through these so the "which paths exist on SharpOS" policy
    // stays kernel-side. The actual writable FS lands in a future phase;
    // for now we recognise the well-known \sharpos root directory only.
    internal static unsafe class FileSystemQuery
    {
        public const uint AttrInvalid   = 0xFFFFFFFFu;   // Win32 INVALID_FILE_ATTRIBUTES
        public const uint AttrDirectory = 0x00000010u;   // FILE_ATTRIBUTE_DIRECTORY

        // Compare a UTF-8 path tail (case-insensitive ASCII) against `t`.
        // Returns true iff path ends with `t` after lowercasing both
        // sides. Matches the same shape as the PAL's wstr-tail helper.
        private static bool TailMatches(byte* path, int pathLen, string t)
        {
            int tLen = t.Length;
            if (pathLen < tLen) return false;
            int off = pathLen - tLen;
            for (int i = 0; i < tLen; i++)
            {
                byte pc = path[off + i];
                char tc = t[i];
                if (pc >= (byte)'A' && pc <= (byte)'Z') pc = (byte)(pc - 'A' + 'a');
                if (tc >= 'A' && tc <= 'Z') tc = (char)(tc - 'A' + 'a');
                if (pc != (byte)tc) return false;
            }
            return true;
        }

        private static int Strlen(byte* p)
        {
            int n = 0;
            while (p[n] != 0) n++;
            return n;
        }

        // SharpOSHost_GetFileAttributes(utf8Path) -> Win32-style
        // attribute bitmask, or AttrInvalid (0xFFFFFFFF) on not-found.
        [RuntimeExport("SharpOSHost_GetFileAttributes")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetFileAttributes")]
        public static uint GetFileAttributes(byte* utf8Path)
        {
            if (utf8Path == null) return AttrInvalid;
            int len = Strlen(utf8Path);
            if (len == 0) return AttrInvalid;

            // Known FAT root: "\sharpos" and the "." current-directory
            // placeholder both resolve to the root dir on SharpOS.
            if (TailMatches(utf8Path, len, "sharpos")
             || TailMatches(utf8Path, len, "\\sharpos")
             || TailMatches(utf8Path, len, "/sharpos")
             || TailMatches(utf8Path, len, "."))
            {
                return AttrDirectory;
            }
            // Writable-FS extension TODO: probe actual FAT once readers
            // gain stat-style queries.
            return AttrInvalid;
        }
    }
}
