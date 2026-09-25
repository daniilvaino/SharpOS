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

            // Before the log binds to a file: the port is the only sink that
            // survives a machine whose filesystem never comes up.
            if (Probes.UsbSerialLog && OS.Hal.Usb.UsbCdcAcm.TryAttach())
            {
                // One line down the port before handing it the log. The sink
                // switches itself off on its first failure, so without this
                // "attached" and "attached and mute" read the same.
                byte* hello = stackalloc byte[32];
                const string banner = "[cdc] port open\n";
                for (int i = 0; i < banner.Length; i++) hello[i] = (byte)banner[i];

                Console.Write("[xhci] usb serial ");
                if (OS.Hal.Usb.UsbCdcAcm.Write(hello, banner.Length))
                {
                    Console.WriteLine("attached as log sink");
                    OS.Hal.BootLog.AttachSerial();
                    if (Probes.UsbSerialEcho) EchoSerial();
                }
                else
                {
                    Console.Write("write FAILED code=0x");
                    if (Xhci.TryFindCdcAcm(out XhciController chc, out _))
                        Console.WriteHex(chc.LastCompletionCode);
                    Console.WriteLine(" (0 = no event before the timeout)");
                }
            }

            ReportStorage();
            Summarise();
        }

        // Read the serial port back for a few seconds and show what arrives.
        //
        // The first thing that proves the input half works at all: the length
        // of a short read comes from the transfer event's residue, which was
        // discarded until now, so a read could only ever guess. Typing from
        // the build machine (rig/kb.ps1 writes to the phone's /dev/ttyGS*)
        // should appear here byte for byte.
        //
        // Bounded on purpose. This runs during boot, and a probe that waits
        // for input nobody sends would hold the machine at the same spot every
        // time — the failure would look like a hang, which is the one thing a
        // diagnostic must never imitate.
        private static void EchoSerial()
        {
            const int Windows = 20;          // 20 x 100 ms
            byte* buffer = stackalloc byte[128];
            int total = 0;

            for (int i = 0; i < Windows; i++)
            {
                int n = OS.Hal.Usb.UsbCdcAcm.Read(buffer, 128, 100);
                if (n <= 0) continue;

                Console.Write("[cdc] in ");
                Console.WriteUInt((uint)n);
                Console.Write(" bytes: ");
                for (int j = 0; j < n; j++)
                {
                    byte b = buffer[j];
                    // Printable as itself, everything else as its number: a
                    // terminal that swallowed a control byte would hide the
                    // very thing being measured.
                    if (b >= 0x20 && b < 0x7F) Console.WriteChar((char)b);
                    else { Console.Write("<"); Console.WriteUInt(b); Console.Write(">"); }
                }
                Console.WriteLine("");
                total += n;
            }

            Console.Write("[cdc] echo window closed, bytes=");
            Console.WriteUInt((uint)total);
            Console.WriteLine(total == 0 ? " (nothing was sent)" : "");
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

                if (Probes.UsbDescriptorDump) DumpDescriptor(hc, slot);

                // Taken twice, on purpose: once here and once after the
                // endpoints are configured. A single reading says nothing —
                // the question is what the Configure Endpoint command changes
                // about the endpoint the census then cannot talk to.
                if (Probes.UsbCensusDeep) DumpControlEndpoint(hc, slot, "before configure");

                // One device can be several things at once, so this lists what
                // it turned out to be rather than picking the first match.
                Console.Write("[xhci] slot ");
                Console.WriteUInt(slot);
                Console.Write(" functions=");
                if (!hc.TryConfigureFunctions(slot, out uint stage))
                {
                    Console.Write("none (stage=");
                    Console.WriteUInt(stage);
                    Console.WriteLine(")");
                    continue;
                }

                byte proto = hc.HidProtocolOf(slot);
                // A HID with protocol 0 is still a HID — it just has no boot
                // protocol. Keying this line on the protocol alone printed
                // such a device as having no HID function at all, which is how
                // the rig phone looked for as long as anyone had been reading
                // this line.
                if (hc.IsHid(slot))
                    Console.Write(proto == 1 ? "keyboard " : proto == 2 ? "mouse " : "hid(no-boot) ");
                if (hc.IsMassStorage(slot)) Console.Write("storage ");
                if (hc.IsCdcAcm(slot)) Console.Write("serial ");
                Console.WriteLine("configured");

                // Only now: see DeepCensus for why nothing that costs a
                // transfer may run before the device is configured.
                if (Probes.UsbCensusDeep)
                {
                    // The two class requests whose answers used to be dropped.
                    // One of them halts the control endpoint on a device that
                    // does not implement it, which is allowed — and used to
                    // end every later transfer without saying so.
                    Console.Write("[xhci] class requests: setProtocol=");
                    Console.Write(hc.HidSetProtocolOk(slot) ? "ok" : (hc.IsHid(slot) ? "REFUSED" : "n/a"));
                    Console.Write(" lineState=");
                    Console.Write(hc.CdcLineStateOk(slot) ? "ok" : (hc.IsCdcAcm(slot) ? "REFUSED" : "n/a"));
                    Console.Write(" ep0 halts=");
                    Console.WriteUInt(hc.ControlHaltsSeen);
                    Console.Write(" recovered=");
                    Console.WriteUInt(hc.ControlHaltsRecovered);
                    Console.WriteLine("");

                    DumpControlEndpoint(hc, slot, "after configure");

                    // Every HID interface the slot declared, with what its
                    // report descriptor says it is for. The line the previous
                    // enumerator could not print: it kept one HID per slot and
                    // dropped the rest, so a device with two looked like a
                    // device with one.
                    int hidCount = hc.HidCountOf(slot);
                    for (int h = 0; h < hidCount; h++)
                    {
                        int fn = hc.HidIndexOf(slot, h);
                        if (fn < 0) continue;

                        Console.Write("[xhci]   hid function ");
                        Console.WriteUInt((uint)h);
                        Console.Write(" interface=");
                        Console.WriteUInt(hc.HidInterfaceAt(fn));
                        Console.Write(" proto=");
                        Console.WriteUInt(hc.HidProtocolAt(fn));
                        Console.Write(" reportLen=");
                        Console.WriteUInt(hc.HidReportDescLengthAt(fn));
                        Console.Write(" usage-page=0x");
                        Console.WriteHex(hc.HidUsagePageAt(fn), 4);
                        Console.Write(" usage=0x");
                        Console.WriteHex(hc.HidUsageAt(fn), 4);
                        Console.Write(" (");
                        Console.Write(UsageName(hc.HidUsagePageAt(fn), hc.HidUsageAt(fn)));
                        Console.WriteLine(")");
                    }

                    DeepCensus(hc, slot);
                }

                if (Probes.UsbHidPoll && proto != 0)
                    PollReports(hc, slot, proto);
            }
        }

        // What the device actually said about itself. Printed as the record
        // list rather than as hex: the question these answer is always "which
        // interfaces are there and which endpoints hang off them", and
        // counting bytes by hand in a log photographed off a screen is how
        // that question stayed open for a day.
        //
        // Read-only from end to end. Nothing here claims an interface or
        // changes a configuration, so a device that appears in this list and
        // then goes undriven is a decision we made rather than a transfer that
        // failed — which is the distinction the one-driver-per-slot enumerator
        // could not report.
        private static void DumpDescriptor(XhciController hc, uint slot)
        {
            byte* d = stackalloc byte[512];
            if (!hc.TryFetchConfigDescriptor(slot, d, 512, out int len))
            {
                Console.WriteLine("[xhci] descriptor unreadable");
                return;
            }

            Console.Write("[xhci] descriptor bytes=");
            Console.WriteUInt((uint)len);
            Console.WriteLine("");

            int offset = 0;
            while (offset + 2 <= len)
            {
                byte recordLength = d[offset];
                byte type = d[offset + 1];
                if (recordLength == 0) { Console.WriteLine("[xhci]   zero-length record, stopping"); break; }

                if (type == 11 && recordLength >= 8)
                {
                    // Interface Association: the descriptor that says "these N
                    // interfaces are one function". Printed because on a
                    // composite device it is the only statement of intent —
                    // without it, two HID interfaces are just two interfaces.
                    Console.Write("[xhci]   assoc first=");
                    Console.WriteUInt(d[offset + 2]);
                    Console.Write(" count=");
                    Console.WriteUInt(d[offset + 3]);
                    Console.Write(" class=0x");
                    Console.WriteHex(d[offset + 4], 2);
                    Console.Write(" sub=0x");
                    Console.WriteHex(d[offset + 5], 2);
                    Console.Write(" proto=0x");
                    Console.WriteHex(d[offset + 6], 2);
                    Console.WriteLine("");
                }
                else if (type == 4 && recordLength >= 9)
                {
                    Console.Write("[xhci]   interface ");
                    Console.WriteUInt(d[offset + 2]);
                    Console.Write(" alt=");
                    Console.WriteUInt(d[offset + 3]);
                    Console.Write(" eps=");
                    Console.WriteUInt(d[offset + 4]);
                    Console.Write(" class=0x");
                    Console.WriteHex(d[offset + 5], 2);
                    Console.Write(" sub=0x");
                    Console.WriteHex(d[offset + 6], 2);
                    Console.Write(" proto=0x");
                    Console.WriteHex(d[offset + 7], 2);
                    // Subclass 1 is the Boot Interface Subclass, and its
                    // absence is not cosmetic: without it there is no fixed
                    // report layout, and the report descriptor stops being an
                    // improvement and becomes the only source.
                    if (d[offset + 5] == 3)
                        Console.Write(d[offset + 6] == 1 ? " (hid boot)" : " (hid, no boot protocol)");
                    Console.WriteLine("");
                }
                else if (type == 0x21 && recordLength >= 9)
                {
                    // HID descriptor: sits between the interface and its
                    // endpoints and carries the length of the report
                    // descriptor, which has to be known before it can be asked
                    // for — devices stall an over-long request.
                    Console.Write("[xhci]     hid ver=0x");
                    Console.WriteHex((ulong)(d[offset + 2] | (d[offset + 3] << 8)), 4);
                    Console.Write(" country=");
                    Console.WriteUInt(d[offset + 4]);
                    Console.Write(" descriptors=");
                    Console.WriteUInt(d[offset + 5]);
                    Console.Write(" reportType=0x");
                    Console.WriteHex(d[offset + 6], 2);
                    Console.Write(" reportLen=");
                    Console.WriteUInt((uint)(d[offset + 7] | (d[offset + 8] << 8)));
                    Console.WriteLine("");
                }
                else if (type == 5 && recordLength >= 7)
                {
                    byte address = d[offset + 2];
                    byte attributes = d[offset + 3];

                    Console.Write("[xhci]     endpoint 0x");
                    Console.WriteHex(address, 2);
                    Console.Write(" ");
                    Console.Write(TransferTypeName(attributes));
                    Console.Write((address & 0x80) != 0 ? " in" : " out");
                    Console.Write(" max=");
                    Console.WriteUInt((uint)(d[offset + 4] | (d[offset + 5] << 8)));
                    Console.Write(" interval=");
                    Console.WriteUInt(d[offset + 6]);
                    Console.WriteLine("");
                }
                else
                {
                    Console.Write("[xhci]   record type=0x");
                    Console.WriteHex(type, 2);
                    Console.Write(" len=");
                    Console.WriteUInt(recordLength);
                    Console.WriteLine("");
                }

                offset += recordLength;
            }
        }

        // Where the default control endpoint stands, in the controller's words
        // and in ours. Costs nothing: it reads the device context and two of
        // our own fields.
        private static void DumpControlEndpoint(XhciController hc, uint slot, string when)
        {
            Console.Write("[xhci] ep0 ");
            Console.Write(when);
            Console.Write(": ");

            if (!hc.TryReadControlEndpointState(slot, out uint slotState, out uint epState,
                                                out ulong dequeue, out bool epCycle,
                                                out ulong ringBase, out uint enqueue,
                                                out uint ourCycle))
            {
                Console.WriteLine("unreadable");
                return;
            }

            Console.Write("slotState=");
            Console.WriteUInt(slotState);
            Console.Write(" epState=");
            Console.WriteUInt(epState);
            Console.Write(EndpointStateName(epState));
            Console.Write(" hwDequeue=0x");
            Console.WriteHex(dequeue);
            Console.Write(" dcs=");
            Console.WriteUInt(epCycle ? 1u : 0u);
            Console.Write(" ring=0x");
            Console.WriteHex(ringBase);
            Console.Write(" ourEnqueue=");
            Console.WriteUInt(enqueue);
            Console.Write(" ourCycle=");
            Console.WriteUInt(ourCycle);
            // On both readings, so the difference between them is readable.
            // Printed once at the end it was cumulative for the whole
            // controller and attributable to nothing: the first run said
            // "halts=1" beside "lineState=ok" and left no way to tell which
            // request had caused it.
            Console.Write(" halts=");
            Console.WriteUInt(hc.ControlHaltsSeen);
            // The controller's dequeue index on our ring, so the two numbers
            // are comparable without arithmetic in the reader's head.
            if (ringBase != 0 && dequeue >= ringBase && dequeue - ringBase < 4096)
            {
                Console.Write(" hwIndex=");
                Console.WriteUInt((uint)((dequeue - ringBase) / 16));
            }
            Console.WriteLine("");
        }

        private static string EndpointStateName(uint state)
            => state switch
            {
                0 => "(disabled)",
                1 => "(running)",
                2 => "(HALTED)",
                3 => "(STOPPED)",
                4 => "(ERROR)",
                _ => "(?)",
            };

        // The half of the census that asks the device more questions, rather
        // than re-reading bytes already in hand.
        //
        // Runs AFTER the device is configured, and that ordering is the whole
        // point. It first ran before, alongside the descriptor dump, and cost
        // the test rig its boot disk: a report descriptor belongs to an
        // INTERFACE, and an unconfigured device has no interfaces, so a
        // correct device stalls the request. The stall halts the control
        // endpoint, nothing here resets it, and every later transfer on that
        // slot fails — including the SET_CONFIGURATION that brings up mass
        // storage. On the rig, storage, keyboard and serial are one composite
        // device on one slot, so asking the keyboard an early question took
        // the disk down with it, and the machine came up saying it had booted
        // from USB and could find no USB disk.
        //
        // Diagnostics do not get to break what they observe. Everything that
        // needs only the bytes already fetched stays in DumpDescriptor, where
        // it costs nothing; everything that needs a transfer waits until the
        // device is in a state that owes an answer.
        private static void DeepCensus(XhciController hc, uint slot)
        {
            DumpDeviceAndStrings(hc, slot);

            // The configuration is re-fetched rather than carried over from
            // DumpDescriptor: one control transfer against holding parsed
            // state in statics, which in this environment means either a
            // class constructor we cannot run or a fixed-size table in .bss.
            byte* d = stackalloc byte[512];
            if (!hc.TryFetchConfigDescriptor(slot, d, 512, out int len))
            {
                // Silence here would be indistinguishable from a device with
                // no HID interfaces at all.
                Console.Write("[xhci] deep census: configuration unreadable code=0x");
                Console.WriteHex(hc.LastCompletionCode);
                Console.WriteLine("");
                return;
            }

            byte iface = 0;
            bool isHid = false;

            int offset = 0;
            while (offset + 2 <= len)
            {
                byte recordLength = d[offset];
                if (recordLength == 0) break;
                byte type = d[offset + 1];

                if (type == 4 && recordLength >= 9)
                {
                    iface = d[offset + 2];
                    isHid = d[offset + 5] == 3 && d[offset + 3] == 0;   // alt 0 only
                }
                else if (type == 0x21 && recordLength >= 9 && isHid)
                {
                    ushort reportLength = (ushort)(d[offset + 7] | (d[offset + 8] << 8));
                    if (reportLength != 0) DumpReportDescriptor(hc, slot, iface, reportLength);
                    isHid = false;
                }

                offset += recordLength;
            }
        }

        private static string TransferTypeName(byte attributes)
            => (attributes & 0x3) switch
            {
                0 => "control",
                1 => "iso",
                2 => "bulk",
                _ => "interrupt",
            };

        // The device descriptor in full, plus the three strings a human reads
        // a device by. The enumeration line above prints vid/pid because that
        // is what enumeration needs; this prints what a device manager shows.
        private static void DumpDeviceAndStrings(XhciController hc, uint slot)
        {
            byte* d = stackalloc byte[18];
            if (!hc.TryFetchDeviceDescriptor(slot, d, 18, out int len) || len < 18)
            {
                // Named, not just reported. "unreadable" covered a device that
                // refused, a transfer that timed out and a page we could not
                // allocate, and the first run of this census hit one of them
                // without saying which.
                Console.Write("[xhci] device descriptor unreadable: ");
                Console.Write(hc.LastDescriptorFailureName);
                Console.Write(" code=0x");
                Console.WriteHex(hc.LastCompletionCode);
                Console.Write(" len=");
                Console.WriteUInt((uint)len);
                Console.WriteLine("");
                return;
            }

            Console.Write("[xhci] device usb=0x");
            Console.WriteHex((ulong)(d[2] | (d[3] << 8)), 4);
            Console.Write(" class=0x"); Console.WriteHex(d[4], 2);
            Console.Write(" sub=0x"); Console.WriteHex(d[5], 2);
            Console.Write(" proto=0x"); Console.WriteHex(d[6], 2);
            Console.Write(" rev=0x"); Console.WriteHex((ulong)(d[12] | (d[13] << 8)), 4);
            Console.Write(" configs="); Console.WriteUInt(d[17]);
            // Class 0 at the device level means "the interfaces decide" —
            // the standard way of saying composite.
            if (d[4] == 0) Console.Write(" (composite)");
            Console.WriteLine("");

            ushort lang = hc.FirstLanguageId(slot);
            if (lang == 0)
            {
                Console.WriteLine("[xhci]   no string descriptors");
                return;
            }

            WriteStringDescriptor(hc, slot, "manufacturer", d[14], lang);
            WriteStringDescriptor(hc, slot, "product", d[15], lang);
            WriteStringDescriptor(hc, slot, "serial", d[16], lang);
        }

        private static void WriteStringDescriptor(XhciController hc, uint slot,
                                                  string label, byte index, ushort lang)
        {
            if (index == 0) return;          // the device declares none

            byte* s = stackalloc byte[256];
            Console.Write("[xhci]   ");
            Console.Write(label);
            Console.Write("=");
            if (!hc.TryFetchStringDescriptor(slot, index, lang, s, 256, out int len) || len < 4)
            {
                Console.WriteLine("<unreadable>");
                return;
            }

            // UTF-16LE after the two-byte header. Anything outside printable
            // ASCII becomes a question mark: this goes to a serial log and to
            // a framebuffer with no font for the rest, and a mangled byte here
            // would read as a transfer fault rather than as a character we
            // cannot draw.
            Console.WriteChar('"');
            for (int i = 2; i + 1 < len; i += 2)
            {
                ushort ch = (ushort)(s[i] | (s[i + 1] << 8));
                Console.WriteChar(ch >= 0x20 && ch < 0x7F ? (char)ch : '?');
            }
            Console.WriteChar('"');
            Console.WriteLine("");
        }

        // A HID interface report descriptor: how long it is, what it says it
        // is, and the bytes themselves.
        //
        // The top-level usage page and usage are pulled out because they are
        // the discriminator. On a device with no boot protocol two HID
        // interfaces can be identical down to the byte, and only this tells a
        // keyboard (page 0x01, usage 0x06) from a FIDO transport (page
        // 0xF1D0). The raw bytes are printed too: the parser that will read
        // them properly does not exist yet, and until it does the log is where
        // the answer lives.
        private static void DumpReportDescriptor(XhciController hc, uint slot,
                                                 byte interfaceNumber, ushort reportLength)
        {
            byte* r = stackalloc byte[512];
            Console.Write("[xhci]     report interface=");
            Console.WriteUInt(interfaceNumber);
            Console.Write(" ");

            if (!hc.TryFetchReportDescriptor(slot, interfaceNumber, reportLength, r, 512, out int len)
                || len <= 0)
            {
                Console.Write("UNREADABLE: ");
                Console.Write(hc.LastDescriptorFailureName);
                Console.Write(" code=0x");
                Console.WriteHex(hc.LastCompletionCode);
                Console.WriteLine("");
                return;
            }

            Console.Write("bytes=");
            Console.WriteUInt((uint)len);
            Console.WriteLine("");

            Console.Write("[xhci]     report");
            for (int i = 0; i < len; i++)
            {
                Console.Write(" ");
                Console.WriteHex(r[i], 2);
            }
            Console.WriteLine("");
        }

        private static string UsageName(uint page, uint usage)
        {
            if (page == 0xF1D0) return "fido/ctap";
            if (page == 0x01 && usage == 0x06) return "keyboard";
            if (page == 0x01 && usage == 0x02) return "mouse";
            if (page == 0x0C) return "consumer";
            return "unknown";
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
