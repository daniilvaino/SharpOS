using OS.Hal;
using OS.Kernel.Diagnostics;

#pragma warning disable CS0162 // Probes/Trace switches are compile-time constants: the branch a switch turns off is meant to be dead (docs/build-warnings.md)

namespace OS.Kernel.File
{
    // The boot volume mounted: the one place the file system becomes
    // Fs.Current (Vfs.Mount over BootDisk). Then a check that it reads —
    // an 8.3 path (EFI/BOOT/BOOTX64.EFI) and a long name (the hosted
    // runtime's CoreLib, exercising LFN), each by its "MZ" signature — and
    // its line in the log. POST-EBS only (PostEbsBoot): the AHCI bring-up
    // must not run while UEFI firmware still owns the disk. Until release
    // 0.1 this was Diagnostics.FatProbe.
    internal static unsafe class BootVolume
    {
        public static void Mount()
        {
            Disk disk = BootDisk.Get();
            Fs fs = disk != null ? Vfs.Mount(disk) : null;
            if (fs == null)
            {
                Console.Write("[fat] mount=N via=");
                Console.Write(BootDisk.SourceName);
                Console.Write(" jmp=0x");
                Console.WriteHex(Fat32.DiagJmp0);
                Console.Write(",0x");
                Console.WriteHex(Fat32.DiagJmp1);
                Console.Write(" bps@11=");
                Console.WriteUInt(Fat32.DiagBpsLba0);
                Console.Write(" p0type=0x");
                Console.WriteHex(Fat32.DiagPart0Type);
                Console.Write(" p0lba=");
                Console.WriteUInt(Fat32.DiagPart0Lba);
                Console.Write(" gptEnt=");
                Console.WriteUInt(Fat32.DiagGptEntries);
                Console.Write(" gptLba=");
                Console.WriteUInt((uint)Fat32.DiagGptFirstLba);
                Console.WriteLine(" FAIL");
                return;
            }

            bool mz83 = CheckMz(fs, "EFI/BOOT/BOOTX64.EFI", out uint sz83);
            // The long name is the hosted runtime's CoreLib: an image without
            // CoreCLR has none, and the check is skipped rather than failed.
            bool lfnPresent = fs.Exists("sharpos/System.Private.CoreLib.dll");
            uint szLfn = 0;
            bool mzLfn = lfnPresent && CheckMz(fs, "sharpos/System.Private.CoreLib.dll", out szLfn);

            Console.Write("[fat] mount=Y ");
            Console.Write(Fat32.IsFat32 ? "FAT32" : "FAT16");
            Console.Write(" bps=");
            Console.WriteUInt(Fat32.BytesPerSector);
            Console.Write(" spc=");
            Console.WriteUInt(Fat32.SectorsPerCluster);
            Console.Write(" bulk=");
            Console.WriteUInt(Fat32.BulkReadBytes / 1024);
            Console.Write("K");
            Console.Write(" 8.3:BOOTX64.EFI sz=");
            Console.WriteUInt(sz83);
            Console.Write(" mz=");
            Console.Write(mz83 ? "Y" : "N");
            if (lfnPresent)
            {
                Console.Write("  lfn:CoreLib.dll sz=");
                Console.WriteUInt(szLfn);
                Console.Write(" mz=");
                Console.Write(mzLfn ? "Y" : "N");
            }
            else
                Console.Write("  lfn:skip (no CoreLib)");
            Console.WriteLine(mz83 && (mzLfn || !lfnPresent) ? " PASS" : " FAIL");

            // EnumDir cheap-detector: what does \EFI\BOOT actually
            // enumerate via our FAT (diagnoses the launcher's empty
            // listing without guessing)?
            if (!Probes.Diagnostics) return;
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
