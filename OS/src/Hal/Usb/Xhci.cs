namespace OS.Hal.Usb
{
    // xHCI host controller — bring-up stage: take the controller away from the
    // firmware, reset it, and report what it is.
    //
    // No transfers yet. This stage is deliberately DMA-free so it can be
    // verified on hardware before a single ring exists: everything here is
    // register reads, one ownership handshake and a reset, all of which either
    // complete or time out visibly.
    //
    // Ownership first, and not as a formality: until the OS-owned bit is taken,
    // the firmware keeps servicing the ports from SMM. It answers port changes
    // behind our back and overwrites the registers we just wrote, which reads
    // as a controller that ignores commands rather than as a missing handoff.
    internal sealed unsafe partial class XhciController
    {
        // Capability registers (offsets from MmioBase).
        private const uint CAP_CAPLENGTH = 0x00;   // byte
        private const uint CAP_HCIVERSION = 0x02;  // ushort
        private const uint CAP_HCSPARAMS1 = 0x04;
        private const uint CAP_HCCPARAMS1 = 0x10;
        private const uint CAP_DBOFF = 0x14;
        private const uint CAP_RTSOFF = 0x18;

        // Operational registers (offsets from OperationalBase).
        private const uint OP_USBCMD = 0x00;
        private const uint OP_USBSTS = 0x04;
        private const uint OP_PAGESIZE = 0x08;

        private const uint USBCMD_RS = 1u << 0;    // run/stop
        private const uint USBCMD_HCRST = 1u << 1; // host controller reset

        private const uint USBSTS_HCH = 1u << 0;   // halted
        private const uint USBSTS_CNR = 1u << 11;  // controller not ready

        // Extended capability IDs.
        private const uint XECP_LEGACY = 1;
        private const uint XECP_SUPPORTED_PROTOCOL = 2;

        // Which ports belong to the USB 3 half of the controller.
        //
        // PORTSC reports a port's speed only once something is attached and
        // trained, and on some controllers it reads 0 ("undefined") even then.
        // Writing that 0 into a slot context asks the controller to schedule
        // at no speed at all, which comes back as a transaction error on the
        // first control transfer — seen on a desktop where the USB 3 stick
        // failed while the USB 2 keyboard and mouse worked.
        //
        // The Supported Protocol capability is the authoritative answer: it
        // names port ranges by USB major revision.
        private const int PortMapSize = 64;
        private readonly byte[] _portMajor = new byte[PortMapSize];

        /// <summary>USB major revision of a port (3, 2 …), or 0 if unknown.</summary>
        public byte PortMajor(uint port)
            => port < PortMapSize ? _portMajor[port] : (byte)0;
        private const uint LEGACY_BIOS_OWNED = 1u << 16;
        private const uint LEGACY_OS_OWNED = 1u << 24;

        private bool _initialized;
        private ulong _mmio;
        private ulong _opBase;
        private ulong _runtimeBase;
        private ulong _doorbellBase;
        private uint _maxSlots;
        private uint _maxPorts;
        private uint _pageSize;
        private ushort _version;
        private bool _contextSize64;
        private bool _tookOwnership;
        private string _failure;

        public bool IsInitialized => _initialized;
        public uint MaxSlots => _maxSlots;
        public uint MaxPorts => _maxPorts;
        public uint PageSize => _pageSize;
        public ushort Version => _version;
        public bool ContextSize64 => _contextSize64;
        public bool TookOwnership => _tookOwnership;
        public ulong MmioBase => _mmio;
        public ulong OperationalBase => _opBase;
        public ulong RuntimeBase => _runtimeBase;
        public ulong DoorbellBase => _doorbellBase;

        /// <summary>Why Init returned false, or null when it did not fail.</summary>
        public string Failure => _failure;

        private UsbHost.Controller _pci;

        public byte Bus => _pci.Bus;
        public byte Slot => _pci.Slot;
        public byte Func => _pci.Func;

        /// <summary>Bring up one specific controller. Which one to drive is
        /// the registry's decision, not this object's.</summary>
        public bool Init(UsbHost.Controller c)
        {
            if (_initialized) return true;
            _failure = null;
            _pci = c;

            if (c.MmioBase == 0)
                return Fail("xHCI BAR is not memory-mapped");

            // The register window is 64 KiB at most in practice; map generously
            // but as a device, or the reads below come back from a cache line.
            if (!OS.Kernel.Memory.VirtualMemory.MapFixed(
                    (void*)c.MmioBase, c.MmioBase, 0x10000, exec: false,
                    OS.Kernel.Memory.VirtualMemory.MemoryKind.Device))
                return Fail("xHCI MMIO map failed");

            _mmio = c.MmioBase;

            // CAPLENGTH and HCIVERSION share one 32-bit register. Reading the
            // version as a ushort at +2 returns 0 on QEMU: MMIO registers are
            // only guaranteed at their natural width, and sub-dword accesses
            // are not decoded. Read the dword and split it.
            uint capReg = *(uint*)(_mmio + CAP_CAPLENGTH);
            byte capLength = (byte)(capReg & 0xFF);
            _version = (ushort)(capReg >> 16);

            if (capLength < 0x20 || capLength > 0x80)
                return Fail("xHCI CAPLENGTH out of range (wrong BAR?)");
            uint hcs1 = *(uint*)(_mmio + CAP_HCSPARAMS1);
            uint hcc1 = *(uint*)(_mmio + CAP_HCCPARAMS1);

            _maxSlots = hcs1 & 0xFF;
            _maxPorts = (hcs1 >> 24) & 0xFF;
            _contextSize64 = (hcc1 & (1u << 2)) != 0;

            _opBase = _mmio + capLength;
            _runtimeBase = _mmio + (*(uint*)(_mmio + CAP_RTSOFF) & ~0x1Fu);
            _doorbellBase = _mmio + (*(uint*)(_mmio + CAP_DBOFF) & ~0x3u);

            if (!TryTakeOwnership(hcc1))
                return Fail("firmware would not release the controller");

            // Claim the bus before the reset, so the controller comes back up
            // already able to reach memory. Match on location, not on class:
            // the laptop carries three USB functions and the first one found
            // is not necessarily the one we just mapped.
            for (int i = 0; i < Pci.Count; i++)
            {
                Pci.PciDev dev = Pci.Get(i);
                if (dev.Bus != c.Bus || dev.Slot != c.Slot || dev.Func != c.Func) continue;
                Pci.EnableMemoryAndBusMaster(ref dev);
                break;
            }

            if (!TryReset())
                return Fail("xHCI reset timed out");

            _pageSize = *(uint*)(_opBase + OP_PAGESIZE);
            _initialized = true;
            return true;
        }

        private bool Fail(string why)
        {
            _failure = why;
            return false;
        }

        /// <summary>True when this is the controller the firmware booted from.</summary>
        public bool PickedByFirmware
            => OS.Boot.BootMedium.Valid && OS.Boot.BootMedium.IsUsb
               && _pci.Slot == OS.Boot.BootMedium.PciDevice
               && _pci.Func == OS.Boot.BootMedium.PciFunction;

        // Walk the extended capability list: take ownership from the firmware
        // if it claims any, and record which ports are USB 3 along the way.
        // The list must be walked to the end for the port map even when
        // ownership was settled early.
        private bool TryTakeOwnership(uint hcc1)
        {
            uint xecpDwords = (hcc1 >> 16) & 0xFFFF;
            if (xecpDwords == 0) return true;

            bool ownershipTaken = true;   // no legacy capability = nothing to take

            ulong p = _mmio + xecpDwords * 4UL;
            for (int guard = 0; guard < 64; guard++)
            {
                uint cap = *(uint*)p;
                if (cap == 0xFFFFFFFF || cap == 0) return true;

                uint id = cap & 0xFF;
                if (id == XECP_SUPPORTED_PROTOCOL)
                    RecordProtocolPorts(p, cap);
                else if (id == XECP_LEGACY)
                    ownershipTaken = TryLegacyHandoff(p);

                uint next = (cap >> 8) & 0xFF;
                if (next == 0) break;                // end of list
                p += next * 4UL;
            }
            return ownershipTaken;
        }

        // A Supported Protocol capability describes one contiguous run of
        // ports: dword 2 holds the first port number (1-based) and how many
        // follow, dword 0 the USB major revision they speak.
        private void RecordProtocolPorts(ulong cap, uint dword0)
        {
            uint major = (dword0 >> 24) & 0xFF;
            uint ports = *(uint*)(cap + 8);
            uint first = ports & 0xFF;
            uint count = (ports >> 8) & 0xFF;

            for (uint i = 0; i < count; i++)
            {
                uint index = first + i - 1;          // to our 0-based numbering
                if (index < PortMapSize) _portMajor[index] = (byte)major;
            }
        }

        private bool TryLegacyHandoff(ulong legacyReg)
        {
            uint* usblegsup = (uint*)legacyReg;
            if ((*usblegsup & LEGACY_BIOS_OWNED) == 0)
            {
                *usblegsup = *usblegsup | LEGACY_OS_OWNED;
                _tookOwnership = true;
                return true;
            }

            *usblegsup = *usblegsup | LEGACY_OS_OWNED;

            // The firmware finishes its own teardown asynchronously; the spec
            // suggests a second, and a machine that never clears the bit must
            // not hang the boot.
            if (!WaitUntil(1000, legacyReg, LEGACY_BIOS_OWNED, expectSet: false))
                return false;

            _tookOwnership = true;
            return true;
        }

        private bool TryReset()
        {
            uint* usbcmd = (uint*)(_opBase + OP_USBCMD);

            // Stop first: resetting a running controller is undefined.
            *usbcmd = *usbcmd & ~USBCMD_RS;
            if (!WaitUntil(200, _opBase + OP_USBSTS, USBSTS_HCH, expectSet: true))
                return false;

            *usbcmd = *usbcmd | USBCMD_HCRST;

            // Reset is done when HCRST self-clears AND the controller stops
            // reporting "not ready" — reading other registers before CNR
            // clears returns garbage.
            if (!WaitUntil(1000, _opBase + OP_USBCMD, USBCMD_HCRST, expectSet: false))
                return false;
            if (!WaitUntil(1000, _opBase + OP_USBSTS, USBSTS_CNR, expectSet: false))
                return false;

            return true;
        }

        // Poll a register bit with a real deadline. NoInlining on the read
        // keeps the compiler from hoisting it out of the loop, which is the
        // documented way this kind of wait turns into a hang.
        private bool WaitUntil(uint timeoutMs, ulong address, uint mask, bool expectSet)
        {
            ulong deadline = Deadline(timeoutMs);
            // The spin cap is not belt-and-braces: HPET may legitimately be
            // absent, and then the deadline cannot expire at all. Without this
            // the wait would be an unbreakable hang instead of a failure.
            for (int spins = 0; spins < 50_000_000; spins++)
            {
                bool isSet = (Read32(address) & mask) != 0;
                if (isSet == expectSet) return true;
                if (Expired(deadline)) return false;
            }
            return false;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private uint Read32(ulong address) => *(uint*)address;

        private ulong Deadline(uint ms)
        {
            if (!OS.Hal.Timer.Hpet.IsInitialized) return 0;
            ulong hz = OS.Hal.Timer.Hpet.FrequencyHz;
            return OS.Hal.Timer.Hpet.ReadCounter() + hz / 1000UL * ms;
        }

        private bool Expired(ulong deadline)
        {
            // No timer means no deadline; the bounded caller loops are the
            // only guard left, so never claim an expiry we cannot measure.
            if (deadline == 0) return false;
            return OS.Hal.Timer.Hpet.ReadCounter() >= deadline;
        }
    }
}
