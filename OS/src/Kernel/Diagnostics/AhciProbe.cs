using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // C-FS2 oracle — adapted MOOS SATA/AHCI driver brings up the q35
    // ICH9 controller (located by C-FS1 PCI scan), reads LBA0 of the
    // boot disk via DMA, checks the MBR boot signature (QEMU VVFAT
    // sector0[510..511] == 55 AA, deterministic). Runs POST-EBS only
    // (called from ExitBootServicesProbe) — issuing AHCI commands
    // reprograms the HBA the live UEFI firmware owns pre-EBS.
    internal static unsafe class AhciProbe
    {
        public static void Run()
        {
            bool init = Ahci.Initialize();
            if (!init)
            {
                Console.WriteLine("[ahci] init=N FAIL");
                return;
            }

            ulong buf = Ahci.AllocDma(1);          // DMA, virt==phys
            bool readOk = buf != 0 && Ahci.Device.Read(0, 1, (byte*)buf);
            byte* b = (byte*)buf;
            byte s0 = readOk ? b[510] : (byte)0;
            byte s1 = readOk ? b[511] : (byte)0;
            bool sigOk = s0 == 0x55 && s1 == 0xAA;

            Console.Write("[ahci] init=Y type=");
            Console.WriteInt((int)Ahci.Device.PortType);
            Console.Write(" rd=");
            Console.WriteInt(readOk ? 1 : 0);
            Console.Write(" lba0[510,511]=0x");
            Console.WriteHex(s0);
            Console.Write(",0x");
            Console.WriteHex(s1);
            Console.WriteLine(init && readOk && sigOk ? " PASS" : " FAIL");
        }
    }
}
