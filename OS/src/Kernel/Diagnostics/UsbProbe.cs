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

        // Waits a bounded time for input on one device and prints whatever
        // arrives. An idle keyboard simply times out — that is the normal
        // outcome headless, and must not be read as a failure.
        private static void PollReports(uint slot, byte protocol)
        {
            Console.Write("[xhci] press keys / move mouse on slot ");
            Console.WriteUInt(slot);
            Console.WriteLine(" ...");

            byte* report = stackalloc byte[16];
            int seen = 0;
            for (int attempt = 0; attempt < 8 && seen < 4; attempt++)
            {
                if (!Xhci.TryReadReport(slot, report, 16, 500)) continue;

                seen++;
                Console.Write("[xhci] report ");
                for (int i = 0; i < 8; i++)
                {
                    Console.Write("0x");
                    Console.WriteHex(report[i]);
                    Console.Write(" ");
                }
                Console.WriteLine("");
            }

            if (seen == 0)
                Console.WriteLine("[xhci] no reports (idle device — not a failure)");
            _ = protocol;
        }

        // POST-EBS only, for the same reason as AHCI: the machines boot off a
        // USB stick, and resetting the controller while the firmware is still
        // reading our files from it would pull the disk out from under UEFI.
        // The scan above is read-only and safe at any time; this is not.
        public static void RunXhci()
        {
            if (!Probes.XhciInit) return;

            if (!Xhci.Init())
            {
                Console.Write("[xhci] init FAIL: ");
                Console.WriteLine(Xhci.Failure ?? "unknown");
                return;
            }

            Console.Write("[xhci] ver=0x");
            Console.WriteHex(Xhci.Version);
            Console.Write(" slots=");
            Console.WriteUInt(Xhci.MaxSlots);
            Console.Write(" ports=");
            Console.WriteUInt(Xhci.MaxPorts);
            Console.Write(" ctx=");
            Console.Write(Xhci.ContextSize64 ? "64" : "32");
            Console.Write(" pagesize=0x");
            Console.WriteHex(Xhci.PageSize);
            Console.Write(Xhci.TookOwnership ? " owned(from-bios)" : " owned(was-free)");
            Console.WriteLine("");

            Console.Write("[xhci] op=0x");
            Console.WriteHex(Xhci.OperationalBase);
            Console.Write(" rt=0x");
            Console.WriteHex(Xhci.RuntimeBase);
            Console.Write(" db=0x");
            Console.WriteHex(Xhci.DoorbellBase);
            Console.WriteLine(" reset OK");

            if (!Xhci.Start())
            {
                Console.Write("[xhci] start FAIL: ");
                Console.WriteLine(Xhci.Failure ?? "unknown");
                return;
            }

            Console.Write("[xhci] running scratchpad=");
            Console.WriteUInt(Xhci.ScratchpadCount);

            // A No-Op that completes is the real proof: our TRB reached the
            // controller and its event came back into our memory.
            Console.Write(" noop=");
            if (Xhci.TryNoOpCommand(out uint code))
            {
                Console.Write(code == 1 ? "OK" : "code=0x");
                if (code != 1) Console.WriteHex(code);
            }
            else
            {
                Console.Write("TIMEOUT");
            }
            Console.WriteLine("");

            for (uint p = 0; p < Xhci.MaxPorts; p++)
            {
                uint sc = Xhci.PortStatus(p);
                if ((sc & 1) == 0) continue;      // nothing connected
                Console.Write("[xhci] port ");
                Console.WriteUInt(p);
                Console.Write(" connected portsc=0x");
                Console.WriteHex(sc);
                Console.Write(" speed=");
                Console.WriteUInt((sc >> 10) & 0xF);

                bool enabled = Xhci.TryResetPort(p);
                Console.Write(enabled ? " reset=enabled" : " reset=FAIL");
                if (!enabled)
                {
                    Console.WriteLine("");
                    continue;
                }

                Console.Write(" slot=");
                if (!Xhci.TryEnableSlot(out uint slot, out uint slotCode) || slotCode != 1)
                {
                    Console.Write("FAIL code=0x");
                    Console.WriteHex(slotCode);
                    Console.WriteLine("");
                    continue;
                }
                Console.WriteUInt(slot);

                Console.Write(" addr=");
                if (!Xhci.TryAddressDevice(slot, p, out uint addrCode))
                {
                    Console.Write("FAIL code=0x");
                    Console.WriteHex(addrCode);
                    Console.WriteLine("");
                    continue;
                }
                Console.Write("OK");

                Console.Write(" desc=");
                if (Xhci.TryGetDeviceDescriptor(slot, out ushort vid, out ushort pid,
                                                out byte cls, out byte mps))
                {
                    Console.Write("vid=0x"); Console.WriteHex(vid);
                    Console.Write(" pid=0x"); Console.WriteHex(pid);
                    Console.Write(" class=0x"); Console.WriteHex(cls);
                    Console.Write(" mps="); Console.WriteUInt(mps);
                }
                else
                {
                    Console.Write("FAIL");
                }
                Console.WriteLine("");

                Console.Write("[xhci] slot ");
                Console.WriteUInt(slot);
                Console.Write(" hid=");
                if (!Xhci.TryConfigureHid(slot, out uint stage))
                {
                    // Not a HID device — the other thing we drive is storage.
                    Console.Write("no (stage=");
                    Console.WriteUInt(stage);
                    Console.Write(") msd=");
                    if (Xhci.TryConfigureMsd(slot, out uint msdStage))
                        Console.WriteLine("configured");
                    else
                    {
                        Console.Write("no (stage=");
                        Console.WriteUInt(msdStage);
                        Console.WriteLine(")");
                    }
                    continue;
                }

                byte proto = Xhci.HidProtocolOf(slot);
                Console.Write(proto == 1 ? "keyboard" : proto == 2 ? "mouse" : "other");
                Console.WriteLine(" configured");

                if (Probes.UsbHidPoll)
                    PollReports(slot, proto);
            }

            // Hand the keyboard to the rest of the system, so a machine with
            // no PS/2 still has a console and DOOM still has arrow keys.
            if (OS.Hal.ScancodeSource.TryAttachUsb())
            {
                Console.WriteLine("[xhci] usb keyboard attached as system input");
            }

            ReportStorage();
        }

        private static void ReportStorage()
        {
            if (!UsbMassStorage.TryAttach())
            {
                Console.WriteLine("[umsd] no mass storage device");
                return;
            }

            Console.Write("[umsd] blocks=");
            Console.WriteULong(UsbMassStorage.BlockCount);
            Console.Write(" bs=");
            Console.WriteUInt(UsbMassStorage.BlockSize);

            // Read sector 0 and look for the boot signature: proof the data
            // phase moved real bytes, not that a command was merely accepted.
            byte* sector = stackalloc byte[512];
            if (!UsbMassStorage.Read(0, 1, sector))
            {
                Console.WriteLine(" read=FAIL");
                return;
            }

            Console.Write(" lba0[510,511]=0x");
            Console.WriteHex(sector[510]);
            Console.Write(",0x");
            Console.WriteHex(sector[511]);
            Console.WriteLine(sector[510] == 0x55 && sector[511] == 0xAA ? " PASS" : " FAIL");

            if (Probes.UsbStorageWrite)
                VerifyWrite();
        }

        // Write a pattern and read it back from the last sector of the stick.
        //
        // The last sector, and only on the QEMU stick: it is a throwaway copy
        // rebuilt every run, so scribbling on it costs nothing. On real
        // hardware this would be someone's filesystem, which is why it sits
        // behind its own switch rather than running by default.
        private static void VerifyWrite()
        {
            ulong lba = UsbMassStorage.BlockCount - 1;

            byte* pattern = stackalloc byte[512];
            for (int i = 0; i < 512; i++) pattern[i] = (byte)(0xA5 ^ i);

            if (!UsbMassStorage.Write(lba, 1, pattern))
            {
                Console.WriteLine("[umsd] write FAIL");
                return;
            }

            byte* readback = stackalloc byte[512];
            for (int i = 0; i < 512; i++) readback[i] = 0;
            if (!UsbMassStorage.Read(lba, 1, readback))
            {
                Console.WriteLine("[umsd] write readback FAIL");
                return;
            }

            int mismatch = -1;
            for (int i = 0; i < 512; i++)
                if (readback[i] != pattern[i]) { mismatch = i; break; }

            Console.Write("[umsd] write lba=");
            Console.WriteULong(lba);
            if (mismatch < 0)
            {
                Console.WriteLine(" verify=PASS");
            }
            else
            {
                Console.Write(" verify=FAIL at ");
                Console.WriteUInt((uint)mismatch);
                Console.WriteLine("");
            }
        }
    }
}
