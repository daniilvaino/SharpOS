using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // C-FS1 oracle — adapted MOOS ECAM scan finds the q35 AHCI
    // controller (class 0x01 / subclass 0x06, ICH9 @ 00:1f.2 in QEMU,
    // ABAR in BAR5). Deterministic headless (fixed QEMU topology).
    // Foundation for C-FS2 (SATA/AHCI Disk over BAR5).
    internal static class PciProbe
    {
        public static void Run()
        {
            Pci.Init();
            int n = Pci.Count;
            bool ahci = Pci.TryFind(0x01, 0x06, out Pci.PciDev a);

            Console.Write("[pci] devs=");
            Console.WriteInt(n);
            if (ahci)
            {
                Console.Write(" ahci=");
                Console.WriteInt(a.Bus);
                Console.Write(":");
                Console.WriteInt(a.Slot);
                Console.Write(".");
                Console.WriteInt(a.Func);
                Console.Write(" id=0x");
                Console.WriteHex(a.VendorID);
                Console.Write(":0x");
                Console.WriteHex(a.DeviceID);
                Console.Write(" abar=0x");
                Console.WriteHex(a.Bar5);
            }
            else
            {
                Console.Write(" ahci=none");
            }
            // PASS = enumeration produced devices AND the AHCI
            // controller with a non-zero ABAR was located.
            bool pass = n > 0 && ahci && a.Bar5 != 0;
            Console.WriteLine(pass ? " PASS" : " FAIL");
        }
    }
}
