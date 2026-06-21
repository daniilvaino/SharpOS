namespace OS.PAL.SharpOSHost
{
    // Overwrites the first byte of ChkstkStub.Chkstk with `0xC3` (ret).
    // Must run BEFORE any CoreCLR code that has a stack frame >= 4096 bytes,
    // because the MSVC-emitted `mov eax, FRAMESIZE; call __chkstk` prologue
    // pattern fires for any sizable frame.
    //
    // Same pattern as ByRefAssignRefPatcher / RethrowPatcher: runs under
    // firmware CR3 where the kernel image is mapped RWX on OVMF.
    internal static unsafe partial class ChkstkPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)ChkstkStub.GetMethodAddress();
            if (target == null) return false;

            // step 118 — compile-time codegen via BootAsm.Generator. Writes
            // the 1-byte template (`0xC3` = ret) into the first byte of the
            // managed __chkstk body.
            int compileLen = Emit(target);
            if (compileLen != 1 || target[0] != 0xC3) return false;

            s_installed = true;
            return true;
        }
    }
}
