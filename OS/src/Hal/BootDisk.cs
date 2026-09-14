namespace OS.Hal
{
    // Which block device the system actually boots from.
    //
    // Until now that was always AHCI. It is not on the test machines: they
    // have no SATA controller in AHCI mode at all (NVMe, or SATA left in
    // RAID) and boot from a USB stick — so "no disk" there means "wrong
    // driver asked", not "no medium".
    //
    // The firmware says which one (BootMedium, captured before
    // ExitBootServices), and only that answer decides. A desktop with SATA
    // disks in AHCI mode, booted from a USB stick, mounted the first SATA
    // disk that read — the firmware partition of the Windows disk, no
    // \sharpos on it, and CoreCLR died looking for its CoreLib. Without an
    // answer nothing is mounted.
    //
    // POST-EBS only, like both drivers underneath: bringing either up while
    // the firmware still owns the medium would pull it out from under UEFI.
    internal static unsafe class BootDisk
    {
        private static Disk s_disk;
        private static bool s_probed;
        private static bool s_isUsb;
        private static string s_missing;

        /// <summary>
        /// Why there is no boot disk to read from, for the message that stops
        /// what needs one; empty when there is.
        /// </summary>
        public static string MissingReason
        {
            get
            {
                if (s_disk == null)
                    return s_missing ?? "no boot disk was looked for";
                if (Fs.Current == null)
                    return "the boot disk has no FAT volume that mounts";
                return "";
            }
        }

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

            if (OS.Boot.BootMedium.Valid && OS.Boot.BootMedium.IsUsb)
            {
                TryUsb();
                return s_disk;
            }

            if (OS.Boot.BootMedium.Valid && OS.Boot.BootMedium.IsNvme)
            {
                // No NVMe driver. Any other disk that reads would be a wrong
                // one, which is worse than none: it fails later and elsewhere.
                s_missing = "booted from NVMe, which has no driver here";
                Log.Write(LogLevel.Warn, "boot disk: " + s_missing);
                return null;
            }

            if (OS.Boot.BootMedium.Valid && OS.Boot.BootMedium.IsSata)
            {
                // That controller and that port, or nothing: with several
                // SATA disks the first one that reads is someone else's.
                int slot = OS.Boot.BootMedium.PciDevice;
                int function = OS.Boot.BootMedium.PciFunction;
                int port = OS.Boot.BootMedium.SataPort;
                Ahci.Initialize(slot, function, port);

                // Only the disk the firmware named; a probe may have brought
                // up another one first.
                bool ours = Ahci.Device != null && Ahci.DeviceSlot == slot
                            && Ahci.DeviceFunction == function && Ahci.DevicePort == port;
                if (ours && CanRead(Ahci.Device))
                    s_disk = Ahci.Device;
                else
                {
                    s_missing = "the SATA disk the firmware booted from did not come up";
                    Log.Write(LogLevel.Warn, "boot disk: " + s_missing);
                }
                return s_disk;
            }

            // Anything else is a guess, and a guess picks whichever disk reads
            // first — on a desktop that was the firmware partition of the
            // Windows disk. The file system writes (the boot log, the write
            // probes) go to the disk chosen here, so no disk is safer than the
            // wrong one. [bootmedium] path: in the log shows the raw nodes of
            // a medium this does not know yet.
            s_missing = OS.Boot.BootMedium.Valid
                ? "booted from a medium with no driver here (see [bootmedium] path)"
                : "the firmware did not say what it booted from";
            Log.Write(LogLevel.Warn, "boot disk: " + s_missing + ", no disk mounted");
            return null;
        }

        private static void TryUsb()
        {
            if (!Usb.UsbMassStorage.TryAttach())
            {
                // The usual reason on a desktop: the stick sits behind a USB
                // hub (front panel, some back-panel ports), and hubs are not
                // enumerated yet, so nothing past them is seen.
                s_missing = "booted from USB, but no USB storage device was found"
                          + " (one behind a USB hub is not seen: hubs are not supported yet)";
                return;
            }

            Disk usb = new Usb.UsbDisk();
            if (CanRead(usb))
            {
                s_disk = usb;
                s_isUsb = true;
            }
            else
            {
                s_missing = "the USB storage device did not read its first sector";
            }
        }

        private static bool CanRead(Disk disk)
        {
            ulong scratch = DmaMemory.AllocPages(1);
            if (scratch == 0) return false;
            return disk.Read(0, 1, (byte*)scratch);
        }
    }
}
