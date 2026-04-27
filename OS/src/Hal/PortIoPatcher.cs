namespace OS.Hal
{
    // Patches PortIoStub.Inb / .Outb method bodies with hand-coded
    // x86 port I/O shellcode at boot time. Same pattern as
    // ByRefAssignRefPatcher: runs under firmware CR3 where the kernel
    // image is mapped RWX on OVMF. Real HW with W^X would need the
    // alias-map path (tracked in nativeaot-nostdlib-limits.md).
    internal static unsafe class PortIoPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* inb = (byte*)PortIoStub.GetInbAddress();
            byte* outb = (byte*)PortIoStub.GetOutbAddress();
            if (inb == null || outb == null) return false;

            WriteInbShellcode(inb);
            WriteOutbShellcode(outb);

            // Readback sanity check — fails silently if .text was mapped RO.
            if (inb[0] != 0x66 || inb[3] != 0xEC) return false;
            if (outb[0] != 0x88 || outb[5] != 0xEE) return false;

            s_installed = true;
            return true;
        }

        // byte Inb(ushort port)         — Win64 ABI, port in CX, result in AL.
        //   66 89 ca       mov dx, cx       ; load port number into DX
        //   ec             in  al, dx       ; read byte from port DX into AL
        //   0f b6 c0       movzx eax, al    ; clear upper bits of return reg
        //   c3             ret
        // Total: 8 bytes.
        private static void WriteInbShellcode(byte* p)
        {
            p[0] = 0x66; p[1] = 0x89; p[2] = 0xCA;
            p[3] = 0xEC;
            p[4] = 0x0F; p[5] = 0xB6; p[6] = 0xC0;
            p[7] = 0xC3;
        }

        // void Outb(ushort port, byte value) — Win64 ABI, port in CX, value in DL.
        //   88 d0          mov al, dl       ; rescue value (DL gets clobbered next)
        //   66 89 ca       mov dx, cx       ; load port number into DX
        //   ee             out dx, al       ; write AL to port DX
        //   c3             ret
        // Total: 7 bytes. Order matters: we must save DL before mov dx, cx.
        private static void WriteOutbShellcode(byte* p)
        {
            p[0] = 0x88; p[1] = 0xD0;
            p[2] = 0x66; p[3] = 0x89; p[4] = 0xCA;
            p[5] = 0xEE;
            p[6] = 0xC3;
        }
    }
}
