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
    internal static unsafe class BootStackSwitchPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)BootStackSwitchStub.GetMethodAddress();
            if (target == null) return false;

            // Inline the 14 bytes directly — `static readonly byte[]`
            // triggers ClassConstructorRunner trap (CLAUDE.md §1).
            //
            // mov rsp, rcx       ; 48 89 CC          (3 bytes)
            // mov rbp, 0         ; 48 C7 C5 00 00 00 00   (7 bytes — full 32-bit imm, zero-extended)
            // push 0             ; 6A 00             (2 bytes — sign-extended to 64-bit 0 on stack)
            // jmp rdx            ; FF E2             (2 bytes)
            target[0]  = 0x48; target[1]  = 0x89; target[2]  = 0xCC;
            target[3]  = 0x48; target[4]  = 0xC7; target[5]  = 0xC5;
            target[6]  = 0x00; target[7]  = 0x00; target[8]  = 0x00; target[9]  = 0x00;
            target[10] = 0x6A; target[11] = 0x00;
            target[12] = 0xFF; target[13] = 0xE2;

            // Readback sanity — fails silently if .text was mapped RO.
            if (target[0] != 0x48 || target[12] != 0xFF)
                return false;

            s_installed = true;
            return true;
        }
    }
}
