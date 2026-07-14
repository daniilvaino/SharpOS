using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step126.8 — kernel-side policy for kernel32 directory ops + special
    // console file names (CONOUT$ / CONIN$ / CONERR$). Also the lookup
    // table for SetEnvironmentVariable / FormatMessage stubs.
    //
    // Our FS is read-only — CreateDirectoryW reports ERROR_ALREADY_EXISTS
    // so BCL FileSystem.CreateDirectory's default `allowExisting=true`
    // path silently succeeds; RemoveDirectoryW reports ERROR_FILE_NOT_FOUND.
    //
    // Console pseudo-name recognition: CreateFileW("CONOUT$") / "CONIN$" /
    // "CONERR$" must return our std-handle sentinels so subsequent
    // WriteConsoleW / GetConsoleScreenBufferInfo route through ConsoleWin32.
    internal static unsafe class FileSystemPolicy
    {
        public const int ERROR_SUCCESS         = 0;
        public const int ERROR_FILE_NOT_FOUND  = 2;
        public const int ERROR_ALREADY_EXISTS  = 183;

        // CreateDirectoryW result code.
        [RuntimeExport("SharpOSHost_CreateDirectory")]
        public static int CreateDirectory() => ERROR_ALREADY_EXISTS;

        // RemoveDirectoryW result code.
        [RuntimeExport("SharpOSHost_RemoveDirectory")]
        public static int RemoveDirectory() => ERROR_FILE_NOT_FOUND;

        // SetEnvironmentVariableW/A — no-op success (we don't persist env
        // writes; reads continue to return whatever the kernel reports).
        [RuntimeExport("SharpOSHost_SetEnvironmentVariable")]
        public static int SetEnvironmentVariable() => ERROR_SUCCESS;

        // Recognise CONOUT$ / CONERR$ / CONIN$ as console pseudo-files.
        // Returns: 0 = not a console name, 1 = STD_OUT (or STD_ERR), 2 = STD_IN.
        // Fork uses this to decide whether to return a console sentinel
        // handle from CreateFileW or fall through to filesystem path.
        // Caller passes UTF-8 bytes (zero-extended wchars) of the name.
        [RuntimeExport("SharpOSHost_ClassifyConsoleFileName")]
        public static int ClassifyConsoleFileName(byte* utf8Name, int len)
        {
            if (utf8Name == null) return 0;
            // Case-insensitive compare against "CONOUT$" / "CONERR$" / "CONIN$".
            // Names are short ASCII; do direct compare.
            if (MatchIgnoreCase(utf8Name, len, (byte)'C', (byte)'O', (byte)'N', (byte)'O', (byte)'U', (byte)'T', (byte)'$', 0, 0, 0))
                return 1;
            if (MatchIgnoreCase(utf8Name, len, (byte)'C', (byte)'O', (byte)'N', (byte)'E', (byte)'R', (byte)'R', (byte)'$', 0, 0, 0))
                return 1;
            if (MatchIgnoreCase(utf8Name, len, (byte)'C', (byte)'O', (byte)'N', (byte)'I', (byte)'N', (byte)'$', 0, 0, 0, 0))
                return 2;
            return 0;
        }

        // Compare bytes case-insensitively against a 10-byte pattern. Pattern
        // may include trailing NULs which match end-of-string. Used for
        // short fixed-length console names so we don't need a heap loop.
        private static bool MatchIgnoreCase(byte* s, int len,
                                            byte p0, byte p1, byte p2, byte p3, byte p4,
                                            byte p5, byte p6, byte p7, byte p8, byte p9)
        {
            byte* p = stackalloc byte[10];
            p[0] = p0; p[1] = p1; p[2] = p2; p[3] = p3; p[4] = p4;
            p[5] = p5; p[6] = p6; p[7] = p7; p[8] = p8; p[9] = p9;
            // Count actual pattern length (up to first NUL).
            int patLen = 0;
            while (patLen < 10 && p[patLen] != 0) patLen++;
            if (len != patLen) return false;
            for (int i = 0; i < patLen; i++)
            {
                byte sc = s[i];
                byte pc = p[i];
                if (sc >= (byte)'a' && sc <= (byte)'z') sc = (byte)(sc - (byte)'a' + (byte)'A');
                if (pc >= (byte)'a' && pc <= (byte)'z') pc = (byte)(pc - (byte)'a' + (byte)'A');
                if (sc != pc) return false;
            }
            return true;
        }

        // FormatMessage — kernel returns "no message available" indicator
        // (BCL Win32Exception treats this as fallback to error code text).
        [RuntimeExport("SharpOSHost_FormatMessage")]
        public static int FormatMessage() => 1815;  // ERROR_MR_MID_NOT_FOUND

        // GetLogicalDrives — bitmask of valid drive letters. SharpOS exposes
        // a single virtual drive C: backed by \sharpos\ FAT/UEFI tree.
        // Bit 2 (= 1u << ('C' - 'A')) → drive C: only.
        [RuntimeExport("SharpOSHost_GetLogicalDrives")]
        public static uint GetLogicalDrives() => 1u << 2;  // C: only

        // GetVolumeInformation — kernel decides volume identity. Fork copies
        // the policy strings into caller buffers.
        //   *outSerial     = volume serial number (cosmetic, fixed)
        //   *outMaxComp    = max filename component length (FAT LFN = 255)
        //   *outFsFlags    = filesystem capability flags (FILE_CASE_PRESERVED_NAMES)
        // Returns 1 on success (BOOL true), 0 for non-C: roots.
        // Volume label and FS type are short ASCII; fork writes them directly.
        public const uint FILE_CASE_PRESERVED_NAMES = 0x00000002;

        [RuntimeExport("SharpOSHost_GetVolumeInformation")]
        public static int GetVolumeInformation(uint* outSerial,
                                                uint* outMaxComp,
                                                uint* outFsFlags)
        {
            if (outSerial   != null) *outSerial   = 0x5A1C0DE5;
            if (outMaxComp  != null) *outMaxComp  = 255;
            if (outFsFlags  != null) *outFsFlags  = FILE_CASE_PRESERVED_NAMES;
            return 1;  // TRUE
        }

        // GetDriveTypeW — caller passes the drive letter as ASCII char
        // (fork shim extracts it from "C:\" / "C:" / null = current).
        // C: → DRIVE_FIXED. Any other → DRIVE_UNKNOWN.
        //   DRIVE_UNKNOWN = 0, DRIVE_NO_ROOT_DIR = 1, DRIVE_REMOVABLE = 2,
        //   DRIVE_FIXED = 3, DRIVE_REMOTE = 4, DRIVE_CDROM = 5, DRIVE_RAMDISK = 6
        [RuntimeExport("SharpOSHost_GetDriveType")]
        public static uint GetDriveType(int driveLetter)
        {
            if (driveLetter == (int)'C' || driveLetter == (int)'c') return 3;  // DRIVE_FIXED
            if (driveLetter == 0) return 3;  // current drive = C:
            return 0;  // DRIVE_UNKNOWN
        }
    }
}
