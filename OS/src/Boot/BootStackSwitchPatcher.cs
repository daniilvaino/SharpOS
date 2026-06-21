namespace OS.Boot
{
    // Overwrites the first 14 bytes of BootStackSwitchStub.BootStackSwitchRaw
    // with hand-coded shellcode that switches RSP to the new stack and
    // jumps to the continuation. See BootStackSwitchStub.cs for the
    // shellcode contract and Win64 ABI alignment reasoning.
    //
    // Same pattern as ByRefAssignRefPatcher: runs under firmware CR3
    // where the kernel image is mapped RWX on OVMF. Real HW with W^X
    // would need the alias-map path (tracked in nativeaot-nostd-kernel-
    // limits.md).
    internal static unsafe partial class BootStackSwitchPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)BootStackSwitchStub.GetMethodAddress();
            if (target == null) return false;

            // step 118 — compile-time codegen via BootAsm.Generator.
            int compileLen = Emit(target);

            // Readback sanity — fails silently if .text was mapped RO.
            // jmp rdx is the last 2 bytes regardless of which form was emitted.
            if (target[0] != 0x48 || target[compileLen - 2] != 0xFF)
                return false;

            s_installed = true;
            return true;
        }
    }
}
