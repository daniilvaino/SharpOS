using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step126: kernel-side policy for Win32 GetEnvironmentVariableW/A.
    // Per CLAUDE.md "logic in the kernel" invariant, the decision about
    // which env vars are defined and what they resolve to lives here.
    // Fork side is a thin ABI marshaller: wide/ASCII name → UTF-8 bytes,
    // call us, marshal value back into caller buffer.
    //
    // Default policy = "minimum opt-outs needed for PS bare-metal UX":
    //   • POWERSHELL_TELEMETRY_OPTOUT — kills the ApplicationInsights
    //     InMemoryTransmitter background loop (which throws GZipStream/
    //     Compression.Native exceptions on every command).
    //   • POWERSHELL_UPDATECHECK_OPTOUT — no network, no checks.
    //   • DOTNET_CLI_TELEMETRY_OPTOUT / DOTNET_NOLOGO / DOTNET_SKIP_FIRST_TIME_EXPERIENCE
    //     — .NET host conveniences, also off.
    //   • POWERSHELL_DISTRIBUTION_CHANNEL — set to "SharpOS" so any PS
    //     telemetry that DOES queue can tag itself accurately.
    // Anything not in this list → ERROR_ENVVAR_NOT_FOUND (203). Fork's
    // existing fall-through behavior preserved.
    internal static unsafe class EnvironmentPolicy
    {
        public const int ERROR_ENVVAR_NOT_FOUND = 203;

        // Match a UTF-8 NUL-terminated name against an ASCII literal.
        // Names are case-sensitive on Windows; PS / .NET both query with
        // canonical upper-case spelling so a strict compare is correct.
        private static bool NameEquals(byte* name, int nameLen, string literal)
        {
            if (nameLen != literal.Length) return false;
            for (int i = 0; i < nameLen; i++)
                if (name[i] != (byte)literal[i]) return false;
            return true;
        }

        // Pick the value for `name`, writing UTF-8 bytes (no NUL) into outBuf
        // up to outBufSize. Returns:
        //   > 0 — number of UTF-8 bytes written (excl trailing NUL); for a
        //         valid name, this also equals the number of chars in the
        //         value (all ASCII).
        //   0 with errCode = 203 — name not defined.
        //   0 with errCode = 122 — buffer too small (returned size = needed,
        //         including trailing NUL, so caller can re-allocate).
        // outErr receives 0 on success, non-zero Win32 error code otherwise.
        [RuntimeExport("SharpOSHost_GetEnvVar")]
        public static uint GetEnvVar(byte* name, int nameLen,
                                      byte* outBuf, uint outBufSize,
                                      uint* outErr)
        {
            if (outErr != null) *outErr = ERROR_ENVVAR_NOT_FOUND;
            if (name == null || nameLen <= 0) return 0;

            string? value = ResolveName(name, nameLen);
            if (value == null) return 0;

            int vLen = value.Length;
            // Need vLen + 1 bytes (NUL). Caller probes with size=0/too-small
            // and gets the required length back; allocates and retries.
            if (outBuf == null || outBufSize < (uint)vLen + 1)
            {
                if (outErr != null) *outErr = 122;  // ERROR_INSUFFICIENT_BUFFER
                return (uint)vLen + 1;              // required size incl NUL
            }
            for (int i = 0; i < vLen; i++) outBuf[i] = (byte)value[i];
            outBuf[vLen] = 0;
            if (outErr != null) *outErr = 0;
            return (uint)vLen;
        }

        private static string? ResolveName(byte* name, int nameLen)
        {
            if (NameEquals(name, nameLen, "POWERSHELL_TELEMETRY_OPTOUT"))    return "1";
            if (NameEquals(name, nameLen, "POWERSHELL_UPDATECHECK_OPTOUT"))  return "1";
            if (NameEquals(name, nameLen, "DOTNET_CLI_TELEMETRY_OPTOUT"))    return "1";
            if (NameEquals(name, nameLen, "DOTNET_NOLOGO"))                  return "1";
            if (NameEquals(name, nameLen, "DOTNET_SKIP_FIRST_TIME_EXPERIENCE")) return "1";
            if (NameEquals(name, nameLen, "POWERSHELL_DISTRIBUTION_CHANNEL")) return "SharpOS";
            // SystemPolicy test-mode override — PS short-circuits all WLDP /
            // Safer / AppLocker probing when this is present. Value "0" =
            // no lockdown (FullLanguage), "1" = enforce. Any other parsed
            // uint also maps to FullLanguage. PS reads via Environment.
            // GetEnvironmentVariable("__PSLockdownPolicy") in SystemPolicy's
            // static ctor and caches the result for the lifetime of the
            // runspace.
            if (NameEquals(name, nameLen, "__PSLockdownPolicy")) return "0";
            return null;
        }
    }
}
