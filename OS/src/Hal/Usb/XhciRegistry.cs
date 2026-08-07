namespace OS.Hal.Usb
{
    // Every xHCI controller on the machine, not just one.
    //
    // Driving a single controller was enough until a desktop turned up where
    // the boot stick and the keyboards live on different ones: picking the
    // boot controller got the disk and lost every keyboard, picking the first
    // got the keyboards and lost the disk. Both are needed at once, so both
    // are brought up.
    //
    // The boot controller goes first. It is the one the system cannot do
    // without, and if something later fails to come up, it should be a
    // keyboard that is missing rather than the filesystem.
    internal static unsafe class Xhci
    {
        private const int MaxControllers = 4;
        private static readonly XhciController[] s_controllers = new XhciController[MaxControllers];
        private static int s_count;
        private static bool s_started;

        public static int Count => s_count;
        public static XhciController Get(int index) => s_controllers[index];

        /// <summary>Bring up every xHCI controller. Idempotent.</summary>
        public static int StartAll()
        {
            if (s_started) return s_count;
            s_started = true;

            UsbHost.Discover();

            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < UsbHost.Count && s_count < MaxControllers; i++)
                {
                    UsbHost.Controller c = UsbHost.Get(i);
                    if (c.Kind != UsbHost.Kind.Xhci || c.MmioBase == 0) continue;

                    // Pass 0 takes the boot controller, pass 1 the rest.
                    bool isBoot = OS.Boot.BootMedium.Valid && OS.Boot.BootMedium.IsUsb
                                  && c.Slot == OS.Boot.BootMedium.PciDevice
                                  && c.Func == OS.Boot.BootMedium.PciFunction;
                    if (isBoot != (pass == 0)) continue;

                    XhciController hc = new XhciController();
                    if (!hc.Init(c)) continue;
                    if (!hc.Start()) continue;

                    s_controllers[s_count++] = hc;
                }
            }

            return s_count;
        }

        /// <summary>Total devices addressed across all controllers.</summary>
        public static int TotalDevices()
        {
            int total = 0;
            for (int i = 0; i < s_count; i++) total += s_controllers[i].DeviceCount;
            return total;
        }

        /// <summary>
        /// Find a device by what it is, wherever it lives. Callers hold the
        /// controller alongside the slot id — a slot number means nothing
        /// without knowing which controller issued it.
        /// </summary>
        public static bool TryFindHid(byte protocol, out XhciController controller, out uint slotId)
        {
            for (int i = 0; i < s_count; i++)
            {
                XhciController hc = s_controllers[i];
                for (int d = 0; d < hc.DeviceCount; d++)
                {
                    uint slot = hc.SlotIdAt(d);
                    if (hc.HidProtocolOf(slot) != protocol) continue;
                    if (!hc.IsConfigured(slot)) continue;

                    controller = hc;
                    slotId = slot;
                    return true;
                }
            }
            controller = null;
            slotId = 0;
            return false;
        }

        public static bool TryFindMassStorage(out XhciController controller, out uint slotId)
        {
            for (int i = 0; i < s_count; i++)
            {
                XhciController hc = s_controllers[i];
                for (int d = 0; d < hc.DeviceCount; d++)
                {
                    uint slot = hc.SlotIdAt(d);
                    if (!hc.IsMassStorage(slot)) continue;

                    controller = hc;
                    slotId = slot;
                    return true;
                }
            }
            controller = null;
            slotId = 0;
            return false;
        }
    }
}
