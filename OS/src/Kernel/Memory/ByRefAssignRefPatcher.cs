using OS.Boot;

namespace OS.Kernel.Memory
{
    // Overwrites the first 13 bytes of ByRefAssignRefStub.RhpByRefAssignRef
    // with hand-coded shellcode that implements the non-standard calling
    // convention (rdi = dst, rsi = src, increments both, no-op for our
    // non-moving GC).
    //
    // Same pattern as InterfaceDispatchPatcher: runs under firmware CR3
    // where the kernel image is mapped RWX on OVMF. Real HW with W^X
    // would need the alias-map path (tracked in nativeaot-nostdlib-limits.md).
    internal static unsafe class ByRefAssignRefPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)ByRefAssignRefStub.GetMethodAddress();
            if (target == null) return false;

            // Inline the 15 bytes directly — we can't use a `static readonly
            // byte[]` initialiser here because it triggers the
            // ClassConstructorRunner lazy-init path we don't support (see
            // docs/nativeaot-nostdlib-limits.md §1).
            //
            // mov rcx, [rsi]    ; 48 8B 0E
            // mov [rdi], rcx    ; 48 89 0F
            // add rdi, 8        ; 48 83 C7 08
            // add rsi, 8        ; 48 83 C6 08
            // ret               ; C3
            target[0]  = 0x48; target[1]  = 0x8B; target[2]  = 0x0E;
            target[3]  = 0x48; target[4]  = 0x89; target[5]  = 0x0F;
            target[6]  = 0x48; target[7]  = 0x83; target[8]  = 0xC7; target[9]  = 0x08;
            target[10] = 0x48; target[11] = 0x83; target[12] = 0xC6; target[13] = 0x08;
            target[14] = 0xC3;

            // Readback sanity check — fails silently if .text was mapped RO.
            if (target[0] != 0x48 || target[1] != 0x8B || target[2] != 0x0E)
                return false;

            s_installed = true;
            return true;
        }
    }
}
