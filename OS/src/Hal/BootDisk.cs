namespace OS.Hal
{
    // Which block device the system actually boots from.
    //
    // Until now that was always AHCI. It is not on the test machines: they
    // have no SATA controller in AHCI mode at all (NVMe, or SATA left in
    // RAID) and boot from a USB stick — so "no disk" there means "wrong
    // driver asked", not "no medium".
    //
    // Order is AHCI first because it is the faster path when present, and
    // because the QEMU default configuration still boots from it.
    //
    // POST-EBS only, like both drivers underneath: bringing either up while
    // the firmware still owns the medium would pull it out from under UEFI.
    internal static unsafe class BootDisk
    {
        private static Disk s_disk;
        private static bool s_probed;
        private static bool s_isUsb;

        /// <summary>True when the boot medium is a USB stick rather than SATA.</summary>
        public static bool IsUsb => s_isUsb;

        public static string SourceName => s_disk == null ? "none" : (s_isUsb ? "usb" : "ahci");

        /// <summary>
        /// The block device to mount, or null when neither driver found one.
        /// Probed once — a second attempt would re-initialise a controller
        /// that is already running.
        /// </summary>
        public static Disk Get()
        {
            if (s_probed) return s_disk;
            s_probed = true;

            if (Ahci.Device == null) Ahci.Initialize();

            // A driver object is not a medium. QEMU's q35 always has an AHCI
            // controller whether or not a disk hangs off it, and it reports a
            // port with nothing behind it — so ask the only question that
            // matters: does sector 0 actually read?
            if (Ahci.Device != null && CanRead(Ahci.Device))
            {
                s_disk = Ahci.Device;
                return s_disk;
            }

            if (Usb.UsbMassStorage.TryAttach())
            {
                Disk usb = new Usb.UsbDisk();
                if (CanRead(usb))
                {
                    s_disk = usb;
                    s_isUsb = true;
                }
            }
            return s_disk;
        }

        private static bool CanRead(Disk disk)
        {
            ulong scratch = DmaMemory.AllocPages(1);
            if (scratch == 0) return false;
            return disk.Read(0, 1, (byte*)scratch);
        }
    }
}
