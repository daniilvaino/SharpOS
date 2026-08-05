using OS.Hal;
using OS.Hal.Usb;

namespace OS.Kernel.Diagnostics
{
    // Reports every USB host controller the machine exposes.
    //
    // This runs before any USB driver exists on purpose. The test machines
    // boot from a USB stick and have no PS/2, so both input and the boot
    // medium depend on USB — but which controller to write a driver for is
    // not something to assume. QEMU's q35 gives EHCI plus UHCI companions by
    // default and xHCI only when asked (-device qemu-xhci), while recent
    // hardware is often xHCI-only. The answer differs per machine, so ask.
    internal static unsafe class UsbProbe
    {
        public static void Run()
        {
            UsbHost.Discover();

            if (UsbHost.Count == 0)
            {
                // Say why "none" might be a lie: a truncated scan never looked.
                Console.Write("[usb] no host controllers found (pci devs=");
                Console.WriteInt(OS.Hal.Pci.Count);
                Console.Write(" buses=");
                Console.WriteInt(OS.Hal.Pci.BusesScanned);
                Console.WriteLine(OS.Hal.Pci.Truncated ? " SCAN TRUNCATED)" : ")");
            }

            for (int i = 0; i < UsbHost.Count; i++)
            {
                UsbHost.Controller c = UsbHost.Get(i);
                Console.Write("[usb] ");
                Console.Write(UsbHost.KindName(c.Kind));
                Console.Write(" at ");
                Console.WriteUInt(c.Bus); Console.Write(":");
                Console.WriteUInt(c.Slot); Console.Write(".");
                Console.WriteUInt(c.Func);
                Console.Write(" vid=0x"); Console.WriteHex(c.VendorId);
                Console.Write(" did=0x"); Console.WriteHex(c.DeviceId);
                Console.Write(" mmio=0x"); Console.WriteHex(c.MmioBase);
                Console.WriteLine("");
            }

            if (Probes.UsbScanHalt)
            {
                Console.WriteLine("[usb] halted on purpose — read the list above");
                OS.Hal.Platform.Halt();
            }
        }
    }
}
