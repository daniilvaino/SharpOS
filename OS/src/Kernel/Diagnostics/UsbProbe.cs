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

            SurveyPorts();

            if (Probes.UsbScanHalt)
            {
                Console.WriteLine("[usb] halted on purpose — read the list above");
                OS.Hal.Platform.Halt();
            }
        }

        // Which ports of which controller have something plugged in.
        //
        // Read-only on purpose: no ownership handshake, no reset, nothing the
        // firmware would notice. A machine can carry several xHCI controllers
        // (this laptop has three) and we drive only one, so before assuming a
        // device is absent it is worth asking where it actually is. Safe to
        // run pre-EBS for the same reason the PCI scan is.
        private static void SurveyPorts()
        {
            for (int i = 0; i < UsbHost.Count; i++)
            {
                UsbHost.Controller c = UsbHost.Get(i);
                if (c.Kind != UsbHost.Kind.Xhci || c.MmioBase == 0) continue;

                if (!OS.Kernel.Memory.VirtualMemory.MapFixed(
                        (void*)c.MmioBase, c.MmioBase, 0x1000, exec: false,
                        OS.Kernel.Memory.VirtualMemory.MemoryKind.Device))
                    continue;

                uint capReg = *(uint*)c.MmioBase;
                byte capLength = (byte)(capReg & 0xFF);
                if (capLength < 0x20 || capLength > 0x80) continue;

                uint ports = (*(uint*)(c.MmioBase + 0x04) >> 24) & 0xFF;
                ulong op = c.MmioBase + capLength;

                Console.Write("[usbsurvey] ");
                Console.WriteUInt(c.Bus); Console.Write(":");
                Console.WriteUInt(c.Slot); Console.Write(".");
                Console.WriteUInt(c.Func);
                Console.Write(" ports=");
                Console.WriteUInt(ports);

                for (uint p = 0; p < ports; p++)
                {
                    uint sc = *(uint*)(op + 0x400 + p * 0x10);
                    if ((sc & 1) == 0) continue;
                    Console.Write(" [p");
                    Console.WriteUInt(p);
                    Console.Write(" spd=");
                    Console.WriteUInt((sc >> 10) & 0xF);
                    Console.Write("]");
                }
                Console.WriteLine("");
            }
        }

        // Waits a bounded time for input on one device and prints whatever
        // arrives. An idle keyboard simply times out — that is the normal
        // outcome headless, and must not be read as a failure.
        private static void PollReports(XhciController hc, uint slot, byte protocol)
        {
            Console.Write("[xhci] press keys / move mouse on slot ");
            Console.WriteUInt(slot);
            Console.WriteLine(" ...");

            byte* report = stackalloc byte[16];
            int seen = 0;
            for (int attempt = 0; attempt < 8 && seen < 4; attempt++)
            {
                if (!hc.TryReadReport(slot, report, 16, 500)) continue;

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

            // Every controller, not just one: on a desktop the boot stick and
            // the keyboards sit on different ones, and driving a single
            // controller means losing whichever is not on it.
            int brought = Xhci.StartAll();
            if (brought == 0)
            {
                Console.WriteLine("[xhci] no controller came up");
                Summarise();
                return;
            }

            for (int i = 0; i < brought; i++)
                Enumerate(Xhci.Get(i));

            // Hand the keyboard to the rest of the system, so a machine with
            // no PS/2 still has a console and DOOM still has arrow keys.
            if (OS.Hal.ScancodeSource.TryAttachUsb())
                Console.WriteLine("[xhci] usb keyboard attached as system input");

            ReportStorage();
            Summarise();
        }

        private static void Enumerate(XhciController hc)
        {
            Console.Write("[xhci] ");
            Console.WriteUInt(hc.Bus); Console.Write(":");
            Console.WriteUInt(hc.Slot); Console.Write(".");
            Console.WriteUInt(hc.Func);
            Console.Write(" ver=0x");
            Console.WriteHex(hc.Version);
            Console.Write(" slots=");
            Console.WriteUInt(hc.MaxSlots);
            Console.Write(" ports=");
            Console.WriteUInt(hc.MaxPorts);
            Console.Write(" ctx=");
            Console.Write(hc.ContextSize64 ? "64" : "32");
            Console.Write(" pagesize=0x");
            Console.WriteHex(hc.PageSize);
            Console.Write(hc.TookOwnership ? " owned(from-bios)" : " owned(was-free)");
            Console.Write(hc.PickedByFirmware ? " pick=bootpath" : " pick=first");
            Console.WriteLine("");

            Console.Write("[xhci] op=0x");
            Console.WriteHex(hc.OperationalBase);
            Console.Write(" rt=0x");
            Console.WriteHex(hc.RuntimeBase);
            Console.Write(" db=0x");
            Console.WriteHex(hc.DoorbellBase);
            Console.WriteLine(" reset OK");

            // Init and Start already ran in the registry; this only reports.
            Console.Write("[xhci] running scratchpad=");
            Console.WriteUInt(hc.ScratchpadCount);

            // A No-Op that completes is the real proof: our TRB reached the
            // controller and its event came back into our memory.
            Console.Write(" noop=");
            if (hc.TryNoOpCommand(out uint code))
            {
                Console.Write(code == 1 ? "OK" : "code=0x");
                if (code != 1) Console.WriteHex(code);
            }
            else
            {
                Console.Write("TIMEOUT");
            }
            Console.WriteLine("");

            for (uint p = 0; p < hc.MaxPorts; p++)
            {
                uint sc = hc.PortStatus(p);
                if ((sc & 1) == 0) continue;      // nothing connected
                Console.Write("[xhci] port ");
                Console.WriteUInt(p);
                Console.Write(" connected portsc=0x");
                Console.WriteHex(sc);
                bool enabled = hc.TryResetPort(p);
                Console.Write(enabled ? " reset=enabled" : " reset=FAIL");

                // After the reset, not before: the speed field is only
                // meaningful once the port has come up, and the pre-reset
                // value printed here previously was whatever the firmware
                // happened to leave behind — 0 on most machines.
                Console.Write(" speed=");
                Console.WriteUInt(hc.SpeedOfPort(p));
                Console.Write("(raw=");
                Console.WriteUInt((hc.PortStatus(p) >> 10) & 0xF);
                Console.Write(" usb");
                Console.WriteUInt(hc.PortMajor(p));
                Console.Write(")");
                if (!enabled)
                {
                    Console.WriteLine("");
                    continue;
                }

                Console.Write(" slot=");
                if (!hc.TryEnableSlot(out uint slot, out uint slotCode) || slotCode != 1)
                {
                    Console.Write("FAIL code=0x");
                    Console.WriteHex(slotCode);
                    Console.WriteLine("");
                    continue;
                }
                Console.WriteUInt(slot);

                Console.Write(" addr=");
                if (!hc.TryAddressDevice(slot, p, out uint addrCode))
                {
                    Console.Write("FAIL code=0x");
                    Console.WriteHex(addrCode);
                    Console.WriteLine("");
                    continue;
                }
                Console.Write("OK");

                Console.Write(" desc=");
                if (hc.TryGetDeviceDescriptor(slot, out ushort vid, out ushort pid,
                                                out byte cls, out byte mps))
                {
                    Console.Write("vid=0x"); Console.WriteHex(vid);
                    Console.Write(" pid=0x"); Console.WriteHex(pid);
                    Console.Write(" class=0x"); Console.WriteHex(cls);
                    Console.Write(" mps="); Console.WriteUInt(mps);
                }
                else
                {
                    Console.Write("FAIL stage=");
                    Console.WriteUInt(hc.LastFailedStage);
                    Console.Write(" code=0x");
                    Console.WriteHex(hc.LastCompletionCode);
                }
                Console.WriteLine("");

                Console.Write("[xhci] slot ");
                Console.WriteUInt(slot);
                Console.Write(" hid=");
                if (!hc.TryConfigureHid(slot, out uint stage))
                {
                    // Not a HID device — the other thing we drive is storage.
                    Console.Write("no (stage=");
                    Console.WriteUInt(stage);
                    Console.Write(") msd=");
                    if (hc.TryConfigureMsd(slot, out uint msdStage))
                        Console.WriteLine("configured");
                    else
                    {
                        Console.Write("no (stage=");
                        Console.WriteUInt(msdStage);
                        Console.WriteLine(")");
                    }
                    continue;
                }

                byte proto = hc.HidProtocolOf(slot);
                Console.Write(proto == 1 ? "keyboard" : proto == 2 ? "mouse" : "other");
                Console.WriteLine(" configured");

                if (Probes.UsbHidPoll)
                    PollReports(hc, slot, proto);
            }
        }

        // One line that says everything needed to diagnose a machine we
        // cannot log from: how many controllers exist, how many we drove,
        // what they yielded, and whether the firmware's hint was usable.
        private static void Summarise()
        {
            Console.Write("[usbsum] xhci-controllers=");
            uint controllers = 0;
            for (int i = 0; i < UsbHost.Count; i++)
                if (UsbHost.Get(i).Kind == UsbHost.Kind.Xhci) controllers++;
            Console.WriteUInt(controllers);
            Console.Write(" driven=");
            Console.WriteUInt((uint)Xhci.Count);
            Console.Write(" devices=");
            Console.WriteUInt((uint)Xhci.TotalDevices());
            Console.Write(" keyboard=");
            Console.Write(OS.Hal.ScancodeSource.UsbAttached ? "yes" : "NO");
            Console.Write(" storage=");
            Console.Write(UsbMassStorage.IsPresent ? "yes" : "NO");
            Console.Write(" hint=");
            if (!OS.Boot.BootMedium.Valid) Console.Write("none");
            else if (!OS.Boot.BootMedium.IsUsb) Console.Write("not-usb");
            else
            {
                Console.WriteUInt(OS.Boot.BootMedium.PciDevice);
                Console.Write(".");
                Console.WriteUInt(OS.Boot.BootMedium.PciFunction);
                Console.Write("/p");
                Console.WriteUInt(OS.Boot.BootMedium.UsbPort);
            }

            // Verdict, so the regression report has something to check rather
            // than a wall of facts. Until step156 this line ended here and the
            // analyser called the whole subsystem HALT: output started and
            // never concluded.
            //
            // What is asserted is deliberately narrow — every xHCI controller
            // the PCI scan found was brought up. NOT that devices are attached:
            // an empty port is a legitimate machine, and asserting a keyboard
            // would fail every headless run. No controllers at all is SKIP, not
            // failure: QEMU without -Usb has none by construction.
            if (controllers == 0)
                Console.WriteLine(" SKIP");
            else if (Xhci.Count < (int)controllers)
                Console.WriteLine(" FAIL");
            else
                Console.WriteLine(" PASS");
        }

        private static void ReportStorage()
        {
            if (!UsbMassStorage.TryAttach())
            {
                // "None found" and "found but never became ready" are
                // different problems and were reported identically before.
                Console.WriteLine(UsbMassStorage.FailStage == 1
                    ? "[umsd] no mass storage device"
                    : "[umsd] device present but not ready (capacity FAIL)");
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
