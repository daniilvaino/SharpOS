namespace OS.Hal.Usb
{
    // Discovery of USB host controllers on the PCI bus.
    //
    // Deliberately the first thing built: which controller a machine exposes
    // decides the whole rest of the stack. MOOS (our usual donor) implements
    // EHCI, i.e. USB 2.0 — but a machine from the last decade normally routes
    // its ports through xHCI and may not present EHCI at all. Guessing wrong
    // here means writing a driver for hardware that is not there.
    //
    // Class 0x0C subclass 0x03 is "USB controller"; ProgIF names the flavour.
    internal static unsafe class UsbHost
    {
        public enum Kind : byte
        {
            None = 0,
            Uhci = 0x00,   // USB 1.1, port I/O
            Ohci = 0x10,   // USB 1.1, MMIO
            Ehci = 0x20,   // USB 2.0
            Xhci = 0x30,   // USB 3.x
            Usb4 = 0x40,
        }

        public struct Controller
        {
            public Kind Kind;
            public ulong MmioBase;      // 0 for UHCI (port-I/O based)
            public byte Bus, Slot, Func;
            public ushort VendorId, DeviceId;
        }

        private const int MaxControllers = 8;
        private static readonly Controller[] s_found = new Controller[MaxControllers];
        private static int s_count;
        private static bool s_scanned;

        public static int Count => s_count;
        public static Controller Get(int i) => s_found[i];

        public static void Discover()
        {
            if (s_scanned) return;
            s_scanned = true;
            s_count = 0;

            Pci.Init();
            for (int i = 0; i < Pci.Count && s_count < MaxControllers; i++)
            {
                Pci.PciDev d = Pci.Get(i);
                if (d.ClassID != 0x0C || d.SubClassID != 0x03) continue;

                Controller c;
                c.Kind = (Kind)d.ProgIF;
                c.Bus = d.Bus; c.Slot = d.Slot; c.Func = d.Func;
                c.VendorId = d.VendorID; c.DeviceId = d.DeviceID;
                c.MmioBase = DecodeMmioBar(d.Bar0, d.Bar1);
                s_found[s_count++] = c;
            }
        }

        /// <summary>Finds the first controller of a kind, or false.</summary>
        public static bool TryFind(Kind kind, out Controller controller)
        {
            Discover();
            for (int i = 0; i < s_count; i++)
            {
                if (s_found[i].Kind != kind) continue;
                controller = s_found[i];
                return true;
            }
            controller = default;
            return false;
        }

        // A memory BAR carries flags in its low bits: bit 0 selects I/O vs
        // memory space, bits 1-2 the width. A 64-bit BAR takes the next BAR
        // as its high half, so reading only Bar0 would truncate the address
        // on any machine that maps the controller above 4 GiB.
        private static ulong DecodeMmioBar(uint bar0, uint bar1)
        {
            if ((bar0 & 1) != 0) return 0;              // I/O space, not MMIO
            ulong addr = bar0 & 0xFFFFFFF0u;
            bool is64 = ((bar0 >> 1) & 0x3) == 0x2;
            if (is64) addr |= (ulong)bar1 << 32;
            return addr;
        }

        public static string KindName(Kind kind)
        {
            switch (kind)
            {
                case Kind.Uhci: return "UHCI(1.1)";
                case Kind.Ohci: return "OHCI(1.1)";
                case Kind.Ehci: return "EHCI(2.0)";
                case Kind.Xhci: return "xHCI(3.x)";
                case Kind.Usb4: return "USB4";
                default: return "unknown";
            }
        }
    }
}
