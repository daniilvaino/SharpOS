using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // C-FS3 oracle — own RO FAT16/32 via the Vfs detector over the
    // AHCI Disk. Resolves an 8.3 path (\EFI\BOOT\BOOTX64.EFI) AND a
    // long-name path (\sharpos\System.Private.CoreLib.dll — the file
    // the host shim loads, exercises LFN), checking the "MZ" PE
    // signature (rebuild-stable, no golden needed). Runs POST-EBS only
    // (ExitBootServicesProbe) — the AHCI bring-up must not run while
    // UEFI firmware still owns the disk.
    internal static unsafe class FatProbe
    {
        public static void Run()
        {
            if (Ahci.Device == null) Ahci.Initialize();

            Fs fs = Ahci.Device != null ? Vfs.Mount(Ahci.Device) : null;
            if (fs == null)
            {
                Console.Write("[fat] mount=N jmp=0x");
                Console.WriteHex(Fat32.DiagJmp0);
                Console.Write(",0x");
                Console.WriteHex(Fat32.DiagJmp1);
                Console.Write(" bps@11=");
                Console.WriteUInt(Fat32.DiagBpsLba0);
                Console.Write(" p0type=0x");
                Console.WriteHex(Fat32.DiagPart0Type);
                Console.Write(" p0lba=");
                Console.WriteUInt(Fat32.DiagPart0Lba);
                Console.WriteLine(" FAIL");
                return;
            }

            bool mz83 = CheckMz(fs, "EFI/BOOT/BOOTX64.EFI", out uint sz83);
            bool mzLfn = CheckMz(fs, "sharpos/System.Private.CoreLib.dll", out uint szLfn);

            Console.Write("[fat] mount=Y ");
            Console.Write(Fat32.IsFat32 ? "FAT32" : "FAT16");
            Console.Write(" 8.3:BOOTX64.EFI sz=");
            Console.WriteUInt(sz83);
            Console.Write(" mz=");
            Console.Write(mz83 ? "Y" : "N");
            Console.Write("  lfn:CoreLib.dll sz=");
            Console.WriteUInt(szLfn);
            Console.Write(" mz=");
            Console.Write(mzLfn ? "Y" : "N");
            Console.WriteLine(mz83 && mzLfn ? " PASS" : " FAIL");

            // The REAL host seam: SharpOSHost_FileOpen / ELF launcher
            // go through Platform.TryReadFile. With Fs.Current set it
            // must now serve the full file from our FAT (not UEFI) —
            // read all 23 MB of CoreLib via 16-sector chunks.
            bool plat = Platform.TryReadFile(
                "/sharpos/System.Private.CoreLib.dll", out void* hb, out uint hsz);
            bool platMz = plat && hb != null && hsz > 1
                          && ((byte*)hb)[0] == (byte)'M' && ((byte*)hb)[1] == (byte)'Z';
            Console.Write("[fatbridge] Platform.TryReadFile via FAT ok=");
            Console.Write(plat ? "Y" : "N");
            Console.Write(" sz=");
            Console.WriteUInt(hsz);
            Console.Write(" mz=");
            Console.Write(platMz ? "Y" : "N");
            Console.WriteLine(platMz ? " PASS" : " FAIL");

            // EnumDir cheap-detector: what does \EFI\BOOT actually
            // enumerate via our FAT (diagnoses the launcher's empty
            // listing without guessing)?
            char* nm = stackalloc char[64];
            Console.Write("[fatdir] EFI/BOOT:");
            int de = 0;
            for (uint i = 0; i < 16; i++)
            {
                if (!fs.EnumDir("EFI/BOOT", i, nm, 64, out uint nl, out ulong at))
                    break;
                Console.Write(" ");
                for (uint j = 0; j < nl && j < 64; j++) Console.WriteChar(nm[j]);
                if ((at & 0x10) != 0) Console.Write("/");
                de++;
            }
            Console.Write(" (n=");
            Console.WriteInt(de);
            Console.WriteLine(")");
        }

        private static bool CheckMz(Fs fs, string path, out uint size)
        {
            size = 0;
            byte* head = (byte*)Ahci.AllocDma(1);
            if (head == null) return false;
            int got = fs.ReadFile(path, head, 4096, out size);
            return got >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z';
        }
    }
}
