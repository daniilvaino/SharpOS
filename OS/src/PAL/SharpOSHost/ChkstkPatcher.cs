namespace OS.PAL.SharpOSHost
{
    // Overwrites the first byte of ChkstkStub.Chkstk with `0xC3` (ret).
    // Must run BEFORE any CoreCLR code that has a stack frame >= 4096 bytes,
    // because the MSVC-emitted `mov eax, FRAMESIZE; call __chkstk` prologue
    // pattern fires for any sizable frame.
    //
    // Same pattern as ByRefAssignRefPatcher / RethrowPatcher: runs under
    // firmware CR3 where the kernel image is mapped RWX on OVMF.
    internal static unsafe class ChkstkPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)ChkstkStub.GetMethodAddress();
            if (target == null) return false;

            // ret — pops return address into RIP, leaves RSP +8. Caller's
            // subsequent `sub rsp, rax` performs the actual stack allocation.
            target[0] = 0xC3;

            if (target[0] != 0xC3) return false;

            s_installed = true;
            return true;
        }
    }
}
