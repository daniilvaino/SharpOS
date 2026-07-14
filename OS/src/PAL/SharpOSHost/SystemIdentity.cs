using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step 99 pass 7 -- system identity / version / timezone exports.
    // Forked CoreCLR's PAL forwards Win32 string-getters (GetComputerNameW,
    // GetUserNameExW, GetCurrentDirectoryW, ...) and RtlGetVersion through
    // these kernel-side functions so policy ("what string SharpOS reports"
    // / "what version SharpOS claims") stays in C# per the SharpOS
    // invariant.
    //
    // All strings are ASCII UTF-8. PAL wide-string callers zero-extend
    // each byte to wchar_t -- safe because every value is a 7-bit ASCII
    // identifier (machine name "SHARPOS", paths starting with backslash,
    // hostname "sharpos", user "local").
    //
    // Constants are written inline as fixed strings in each kind branch
    // -- not stored in static fields because `static readonly byte[]`
    // would trigger the ClassConstructorRunner trap in the NoStdLib
    // environment (CLAUDE.md §"ClassConstructorRunner trap"). Cost is a
    // few extra byte stores per call; negligible vs SharpOSHost_*
    // dispatch overhead.
    internal static unsafe class SystemIdentity
    {
        public const int KindCurrentDir   = 0;
        public const int KindTempPath     = 1;
        public const int KindSystemDir    = 2;
        public const int KindWindowsDir   = 3;
        public const int KindMachineName  = 4;
        public const int KindUserName     = 5;
        public const int KindHostName     = 6;
        public const int KindOsName       = 7;
        public const int KindTimeZoneName = 8;

        // Returns the source length in bytes (excl NUL). Caller can pass
        // outBuf=null / outBufSize=0 first to discover the required size,
        // then alloc and call again. If outBuf != null and outBufSize >
        // srcLen, copies src + NUL and returns srcLen. If outBufSize <=
        // srcLen, returns srcLen + 1 (required incl NUL) and writes
        // nothing. Returns -1 for unknown kind.
        [RuntimeExport("SharpOSHost_GetSystemString")]
        public static int GetSystemString(int kind, byte* outBuf, int outBufSize)
        {
            // Use a stack buffer keyed by the kind. Each branch writes
            // its string bytes directly. Kept short -- longest is
            // \sharpos\system32 = 17 chars.
            byte* tmp = stackalloc byte[64];
            int len;

            switch (kind)
            {
                case KindCurrentDir:
                    // "C:\sharpos" — must be drive-rooted absolute so BCL
                    // Path.GetFullPath doesn't prepend it to already-absolute
                    // paths (e.g. C:\sharpos\pwsh\X.dll → \sharpos\C:\... bug).
                    len = 10;
                    tmp[0]=(byte)'C'; tmp[1]=(byte)':'; tmp[2]=(byte)'\\';
                    tmp[3]=(byte)'s'; tmp[4]=(byte)'h'; tmp[5]=(byte)'a';
                    tmp[6]=(byte)'r'; tmp[7]=(byte)'p'; tmp[8]=(byte)'o'; tmp[9]=(byte)'s';
                    break;
                case KindTempPath:
                    len = 15;
                    tmp[0]=(byte)'C'; tmp[1]=(byte)':'; tmp[2]=(byte)'\\';
                    tmp[3]=(byte)'s'; tmp[4]=(byte)'h'; tmp[5]=(byte)'a';
                    tmp[6]=(byte)'r'; tmp[7]=(byte)'p'; tmp[8]=(byte)'o'; tmp[9]=(byte)'s';
                    tmp[10]=(byte)'\\'; tmp[11]=(byte)'t'; tmp[12]=(byte)'m'; tmp[13]=(byte)'p';
                    tmp[14]=(byte)'\\';
                    break;
                case KindSystemDir:
                    len = 19;
                    tmp[0]=(byte)'C'; tmp[1]=(byte)':'; tmp[2]=(byte)'\\';
                    tmp[3]=(byte)'s'; tmp[4]=(byte)'h'; tmp[5]=(byte)'a';
                    tmp[6]=(byte)'r'; tmp[7]=(byte)'p'; tmp[8]=(byte)'o'; tmp[9]=(byte)'s';
                    tmp[10]=(byte)'\\'; tmp[11]=(byte)'s'; tmp[12]=(byte)'y'; tmp[13]=(byte)'s';
                    tmp[14]=(byte)'t'; tmp[15]=(byte)'e'; tmp[16]=(byte)'m';
                    tmp[17]=(byte)'3'; tmp[18]=(byte)'2';
                    break;
                case KindWindowsDir:
                    len = 10;
                    tmp[0]=(byte)'C'; tmp[1]=(byte)':'; tmp[2]=(byte)'\\';
                    tmp[3]=(byte)'s'; tmp[4]=(byte)'h'; tmp[5]=(byte)'a';
                    tmp[6]=(byte)'r'; tmp[7]=(byte)'p'; tmp[8]=(byte)'o'; tmp[9]=(byte)'s';
                    break;
                case KindMachineName:
                    len = 7;
                    tmp[0]=(byte)'S'; tmp[1]=(byte)'H'; tmp[2]=(byte)'A'; tmp[3]=(byte)'R';
                    tmp[4]=(byte)'P'; tmp[5]=(byte)'O'; tmp[6]=(byte)'S';
                    break;
                case KindUserName:
                    len = 5;
                    tmp[0]=(byte)'l'; tmp[1]=(byte)'o'; tmp[2]=(byte)'c'; tmp[3]=(byte)'a'; tmp[4]=(byte)'l';
                    break;
                case KindHostName:
                    len = 7;
                    tmp[0]=(byte)'s'; tmp[1]=(byte)'h'; tmp[2]=(byte)'a'; tmp[3]=(byte)'r';
                    tmp[4]=(byte)'p'; tmp[5]=(byte)'o'; tmp[6]=(byte)'s';
                    break;
                case KindOsName:
                    len = 7;
                    tmp[0]=(byte)'S'; tmp[1]=(byte)'h'; tmp[2]=(byte)'a'; tmp[3]=(byte)'r';
                    tmp[4]=(byte)'p'; tmp[5]=(byte)'O'; tmp[6]=(byte)'S';
                    break;
                case KindTimeZoneName:
                    len = 3;
                    tmp[0]=(byte)'U'; tmp[1]=(byte)'T'; tmp[2]=(byte)'C';
                    break;
                default:
                    return -1;
            }

            if (outBuf == null || outBufSize <= len) return len + 1;
            for (int i = 0; i < len; i++) outBuf[i] = tmp[i];
            outBuf[len] = 0;
            return len;
        }

        // OS version reported by RtlGetVersion / GetVersionExW.
        // Mirrors the host build target (Windows 11 build 26100) until
        // SharpOS diverges enough to claim its own version.
        [RuntimeExport("SharpOSHost_GetOSVersion")]
        public static void GetOSVersion(uint* outMajor, uint* outMinor, uint* outBuild)
        {
            if (outMajor != null) *outMajor = 10;
            if (outMinor != null) *outMinor = 0;
            if (outBuild != null) *outBuild = 26100;
        }

        // Time zone bias in MINUTES, west of UTC positive (matches Win32
        // TIME_ZONE_INFORMATION.Bias semantics). SharpOS reports UTC: 0.
        [RuntimeExport("SharpOSHost_GetTimeZoneBiasMinutes")]
        public static int GetTimeZoneBiasMinutes() => 0;
    }
}
