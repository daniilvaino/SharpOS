namespace OS.Hal
{
    // Patches PortIoStub.Inb / .Outb method bodies with hand-coded
    // x86 port I/O shellcode at boot time. Same pattern as
    // ByRefAssignRefPatcher: runs under firmware CR3 where the kernel
    // image is mapped RWX on OVMF. Real HW with W^X would need the
    // alias-map path (tracked in nativeaot-nostdlib-limits.md).
    internal static unsafe partial class PortIoPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* inb = (byte*)PortIoStub.GetInbAddress();
            byte* outb = (byte*)PortIoStub.GetOutbAddress();
            if (inb == null || outb == null) return false;

            // step 118 — compile-time codegen via BootAsm.Generator.
            EmitInb(inb);
            EmitOutb(outb);

            // Readback sanity check — fails silently if .text was mapped RO.
            if (inb[0] != 0x66 || inb[3] != 0xEC) return false;
            if (outb[0] != 0x88 || outb[5] != 0xEE) return false;

            s_installed = true;
            return true;
        }
    }
}
