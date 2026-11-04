namespace OS.Hal.Usb
{
    /// <summary>
    /// What one connected port came to (UsbBus.Enumerate): how far it got and
    /// what the device said on the way. Stage names the step that stopped it.
    /// </summary>
    internal struct UsbPortOutcome
    {
        public enum Stage { Reset, Slot, Address, Addressed }

        public uint Port;
        public uint PortStatus;          // PORTSC before the reset
        public Stage Reached;
        public uint Slot;
        public uint Code;                // completion code of the failing command
        public bool DescriptorRead;
        public ushort VendorId, ProductId;
        public byte Class, MaxPacket;
    }

    /// <summary>
    /// Watches USB bring-up (UsbProbe, when diagnostics are on). The bus
    /// decides and acts; an observer only reads what it is handed and the
    /// controller's own state, and may ask the device questions between
    /// addressing and configuring — never anything that changes it.
    /// </summary>
    internal interface IUsbBusObserver
    {
        void ControllerUp(XhciController hc, bool noopAnswered, uint noopCode);

        /// <summary>A connected port, after it was reset, addressed and its descriptor read — or as far as it got.</summary>
        void PortDone(XhciController hc, ref UsbPortOutcome outcome);

        /// <summary>The device's functions configured (or not: stage says where it stopped).</summary>
        void Configured(XhciController hc, uint slot, bool configured, uint stage);
    }

    /// <summary>
    /// The USB stack's bring-up (step244, out of UsbProbe): every xHCI
    /// controller started, every connected port reset, addressed and its
    /// functions configured, then the system's devices attached — the keyboard
    /// as input, a CDC-ACM port as a log sink, mass storage as a disk.
    /// </summary>
    /// <remarks>
    /// POST-EBS only, for the same reason as AHCI: the machines boot off a USB
    /// stick, and resetting the controller while the firmware still reads our
    /// files from it would pull the disk out from under UEFI.
    /// </remarks>
    internal static unsafe class UsbBus
    {
        /// <summary>Controllers brought up by the last Start.</summary>
        public static int ControllersUp { get; private set; }

        /// <summary>A CDC-ACM port took the first line and carries the boot log.</summary>
        public static bool SerialAttached { get; private set; }

        /// <summary>A CDC-ACM port was found but refused the first line.</summary>
        public static bool SerialMute { get; private set; }

        public static void Start(IUsbBusObserver observer, bool serialLog)
        {
            // Every controller, not just one: on a desktop the boot stick and
            // the keyboards sit on different ones, and driving a single
            // controller means losing whichever is not on it.
            ControllersUp = Xhci.StartAll();
            if (ControllersUp == 0) return;

            for (int i = 0; i < ControllersUp; i++)
                Enumerate(Xhci.Get(i), observer);

            // Hand the keyboard to the rest of the system, so a machine with
            // no PS/2 still has a console and DOOM still has arrow keys.
            ScancodeSource.TryAttachUsb();

            // Before the log binds to a file: the port is the only sink that
            // survives a machine whose filesystem never comes up.
            if (serialLog && UsbCdcAcm.TryAttach())
            {
                // One line down the port before handing it the log. The sink
                // switches itself off on its first failure, so without this
                // "attached" and "attached and mute" read the same.
                byte* hello = stackalloc byte[32];
                const string banner = "[cdc] port open\n";
                for (int i = 0; i < banner.Length; i++) hello[i] = (byte)banner[i];

                if (UsbCdcAcm.Write(hello, banner.Length))
                {
                    SerialAttached = true;
                    BootLog.AttachSerial();
                }
                else
                {
                    SerialMute = true;
                }
            }

            UsbMassStorage.TryAttach();
        }

        private static void Enumerate(XhciController hc, IUsbBusObserver observer)
        {
            // Init and Start already ran in the registry. A No-Op that
            // completes is the real proof: our TRB reached the controller and
            // its event came back into our memory.
            bool answered = hc.TryNoOpCommand(out uint noopCode);
            observer?.ControllerUp(hc, answered, noopCode);

            for (uint p = 0; p < hc.MaxPorts; p++)
            {
                uint sc = hc.PortStatus(p);
                if ((sc & 1) == 0) continue;      // nothing connected

                var outcome = new UsbPortOutcome { Port = p, PortStatus = sc, Reached = UsbPortOutcome.Stage.Reset };
                if (!hc.TryResetPort(p)) { observer?.PortDone(hc, ref outcome); continue; }

                outcome.Reached = UsbPortOutcome.Stage.Slot;
                if (!hc.TryEnableSlot(out uint slot, out uint slotCode) || slotCode != 1)
                {
                    outcome.Code = slotCode;
                    observer?.PortDone(hc, ref outcome);
                    continue;
                }
                outcome.Slot = slot;

                outcome.Reached = UsbPortOutcome.Stage.Address;
                if (!hc.TryAddressDevice(slot, p, out uint addrCode))
                {
                    outcome.Code = addrCode;
                    observer?.PortDone(hc, ref outcome);
                    continue;
                }

                outcome.Reached = UsbPortOutcome.Stage.Addressed;
                outcome.DescriptorRead = hc.TryGetDeviceDescriptor(slot, out outcome.VendorId, out outcome.ProductId,
                                                                   out outcome.Class, out outcome.MaxPacket);
                observer?.PortDone(hc, ref outcome);

                // One device can be several things at once: every function it
                // has is configured, not the first match.
                bool configured = hc.TryConfigureFunctions(slot, out uint stage);
                observer?.Configured(hc, slot, configured, stage);
            }
        }
    }
}
