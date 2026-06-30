using System.Runtime;
using System.Runtime.InteropServices;
using OS.Hal;

namespace OS.PAL.SharpOSHost
{
    // step 99 pass 7 / step126.20 — FS query exports. PAL Win32 FS getters
    // (GetFileAttributesW, GetFileAttributesExW, FindFirstFileW, ...)
    // forward through these. Probe-the-FAT logic stays kernel-side per
    // CLAUDE.md invariant (logic-in-kernel).
    //
    // Policy:
    //   • "/" or "\" or "" (drive root after C:\ strip)  → directory
    //   • "\sharpos" (canonical root)                    → directory
    //   • path ending in known file extension            → probe TryReadFile
    //   • any other path starting with "\sharpos\…"      → assume directory
    //     (PS module discovery walks deeply; FindFirstFile returns empty
    //     enumeration for non-existent ones, that's recoverable)
    internal static unsafe class FileSystemQuery
    {
        public const uint AttrInvalid   = 0xFFFFFFFFu;   // Win32 INVALID_FILE_ATTRIBUTES
        public const uint AttrNormal    = 0x00000080u;   // FILE_ATTRIBUTE_NORMAL
        public const uint AttrDirectory = 0x00000010u;   // FILE_ATTRIBUTE_DIRECTORY

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

        private static bool HasFileExtension(byte* path, int len)
        {
            // Look back from end for the last '.' before any separator.
            for (int i = len - 1; i >= 0 && i > len - 16; i--)
            {
                byte b = path[i];
                if (b == (byte)'\\' || b == (byte)'/') return false;
                if (b == (byte)'.')
                {
                    // Must have at least one char after '.'
                    return (i < len - 1);
                }
            }
            return false;
        }

        [RuntimeExport("SharpOSHost_GetFileAttributes")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_GetFileAttributes")]
        public static uint GetFileAttributes(byte* utf8Path)
        {
            if (utf8Path == null) return AttrInvalid;
            int len = Strlen(utf8Path);

            // Strip virtual drive-letter prefix "C:\..." → "\..." (mirrors
            // SharpOSHost_FileOpen normalization). The fork's wpath_to_ascii
            // passes paths through unchanged, so PS calls arrive as
            // "C:\sharpos\..." here.
            if (len >= 3 && (utf8Path[0] == (byte)'C' || utf8Path[0] == (byte)'c')
                         && utf8Path[1] == (byte)':' && utf8Path[2] == (byte)'\\')
            {
                utf8Path += 2;
                len -= 2;
            }
            // Bare "C:" / "c:" — drive identifier without path. PS asks for
            // this during PSDrive auto-mount to verify the C: drive itself
            // exists. Treat as the drive root (directory).
            else if (len == 2 && (utf8Path[0] == (byte)'C' || utf8Path[0] == (byte)'c')
                              && utf8Path[1] == (byte)':')
            {
                return AttrDirectory;
            }

            // Drive root "" / "\" / "/" — drive root after our C:\ strip.
            if (len == 0) return AttrDirectory;
            if (len == 1 && (utf8Path[0] == (byte)'\\' || utf8Path[0] == (byte)'/'))
                return AttrDirectory;
            // "." — current-directory placeholder.
            if (len == 1 && utf8Path[0] == (byte)'.') return AttrDirectory;

            // Canonical "\sharpos" or "/sharpos" prefix?
            bool underSharpOS = false;
            if (len >= 8)
            {
                bool sepStart = (utf8Path[0] == (byte)'\\' || utf8Path[0] == (byte)'/');
                if (sepStart)
                {
                    byte* p = utf8Path + 1;
                    if ((p[0]=='s'||p[0]=='S') && (p[1]=='h'||p[1]=='H')
                     && (p[2]=='a'||p[2]=='A') && (p[3]=='r'||p[3]=='R')
                     && (p[4]=='p'||p[4]=='P') && (p[5]=='o'||p[5]=='O')
                     && (p[6]=='s'||p[6]=='S'))
                    {
                        underSharpOS = (len == 8) || p[7] == (byte)'\\' || p[7] == (byte)'/';
                    }
                }
            }

            if (!underSharpOS) return AttrInvalid;

            // "\sharpos" itself.
            if (len == 8) return AttrDirectory;

            // Path with file extension → probe FS to verify existence.
            // No extension → assume directory (FindFirstFile will enumerate).
            if (HasFileExtension(utf8Path, len))
            {
                // Up-cast UTF-8 bytes (ASCII paths) into a wchar stackbuf so
                // we can build a managed string via String.FromUtf16. The
                // probe slurps file content via Platform.TryReadFile —
                // wasteful for large files but acceptable for PS's psd1 /
                // ps1xml attribute probes (kilobytes each).
                char* wbuf = stackalloc char[260];
                int wlen = len < 259 ? len : 259;
                for (int i = 0; i < wlen; i++) wbuf[i] = (char)utf8Path[i];
                wbuf[wlen] = '\0';
                string path = System.String.FromUtf16(wbuf, wlen);
                if (Platform.TryReadFile(path, out void* _, out uint _))
                    return AttrNormal;
                return AttrInvalid;
            }

            return AttrDirectory;
        }

        // SharpOSHost_FindDirEntry — single-entry directory enumeration.
        // Caller (fork's FindFirstFileW/FindNextFileW shim) tracks the index;
        // we hand back one entry at a time. Returns name length (0 = no more
        // entries / not a dir), writes name into outName (max outNameChars,
        // sans NUL), and Win32-style attribute bits via outAttrs.
        //
        // Path normalization mirrors GetFileAttributes — strip leading C:\ /
        // c:\ so the FAT-side EnumDir sees the canonical \sharpos\... form.
        [RuntimeExport("SharpOSHost_FindDirEntry")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_FindDirEntry")]
        public static uint FindDirEntry(byte* utf8Path, uint index,
                                         char* outName, uint outNameChars,
                                         uint* outAttrs, uint* outSize)
        {
            if (outAttrs != null) *outAttrs = 0;
            if (outSize != null) *outSize = 0;
            if (utf8Path == null || outName == null || outNameChars == 0) return 0;

            int len = Strlen(utf8Path);
            if (len >= 3 && (utf8Path[0] == (byte)'C' || utf8Path[0] == (byte)'c')
                         && utf8Path[1] == (byte)':' && utf8Path[2] == (byte)'\\')
            {
                utf8Path += 2;
                len -= 2;
            }
            // Drive root "\" / "" — enumerate "\sharpos" as the sole entry.
            if (len == 0 || (len == 1 && (utf8Path[0] == (byte)'\\' || utf8Path[0] == (byte)'/')))
            {
                if (index != 0) return 0;
                if (outNameChars < 7) return 0;
                outName[0] = 's'; outName[1] = 'h'; outName[2] = 'a';
                outName[3] = 'r'; outName[4] = 'p'; outName[5] = 'o'; outName[6] = 's';
                if (outAttrs != null) *outAttrs = AttrDirectory;
                return 7;
            }

            // Up-cast ASCII path to wchar for the managed FAT API.
            char* wbuf = stackalloc char[260];
            int wlen = len < 259 ? len : 259;
            for (int i = 0; i < wlen; i++) wbuf[i] = (char)utf8Path[i];
            wbuf[wlen] = '\0';
            string path = System.String.FromUtf16(wbuf, wlen);

            if (!Platform.TryReadDirectoryEntry(path, index, outName, outNameChars,
                                                out uint nameLen, out ulong attrs))
                return 0;

            // Map FAT attrs (low bit 0x10 = dir) to Win32; default to normal
            // file if no dir bit set. Size is packed in upper 32 bits of attrs
            // (Fat32.EnumDir convention) — extract for outSize.
            uint w32 = (uint)(attrs & 0x10UL);
            if (outAttrs != null) *outAttrs = w32 != 0 ? AttrDirectory : AttrNormal;
            if (outSize != null) *outSize = (uint)(attrs >> 32);
            return nameLen;
        }

        // Drive-root synthetic "sharpos" entry already covered earlier in
        // the function — that returns AttrDirectory + size=0, which is
        // correct (directories have no size in NtQueryDirectoryFile output).
    }
}
