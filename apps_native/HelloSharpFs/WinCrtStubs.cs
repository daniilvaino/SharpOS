using System.Runtime;

namespace HelloSharpFs
{
    // Freestanding win-x64 PE CRT stubs. Only compiled for the win-x64 build
    // (HelloSharpFs.csproj gates this + the CoffStub.Generator import on the
    // RID); the ELF (linux-x64) build supplies __security_cookie via its own
    // security_cookie.o in build_launcher_wsl.ps1.
    //
    // Mirrors the kernel's CrtGlobals (OS/src/PAL/SharpOSHost/CrtAndEhStubs.cs):
    //   - __security_cookie as a native DATA symbol via CoffStub.Generator --
    //     ILC's [RuntimeExport] on a static field does NOT emit a native data
    //     symbol (historical ILC gap), so the [CoffDataSymbol] attribute drives
    //     a tiny COFF .obj instead.
    //   - __security_check_cookie as a no-op function -- no real stack-canary
    //     semantics are enforced on a trusted unikernel, and this avoids the
    //     libcmt __report_gsfailure path on a (spurious) mismatch.
    internal static class WinCrtStubs
    {
        [BootAsm.CoffDataSymbol("__security_cookie", Section = ".data", Alignment = 8)]
        public static ulong SecurityCookie = 0x2B992DDFA232UL;

        [RuntimeExport("__security_check_cookie")]
        public static void SecurityCheckCookie(ulong cookie)
        {
            _ = cookie;
        }

        // NativeAOT's module startup references __managed__Startup (the default
        // Main-wrapper entry), but EntryPointSymbol=SharpAppEntry renames the
        // real entry, so ILC never defines that symbol. The ELF (ld -e
        // SharpAppBootstrap) build dead-strips the reference; the win-x64 SDK
        // link keeps the whole ILC obj and needs the symbol resolved. This
        // no-op satisfies it -- it is never called: we enter via
        // SharpAppBootstrap -> SharpAppEntry, which does its own init.
        [RuntimeExport("__managed__Startup")]
        public static void ManagedStartup()
        {
        }
    }
}
