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
    internal static unsafe partial class Xhci
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
        private const uint LEGACY_BIOS_OWNED = 1u << 16;
        private const uint LEGACY_OS_OWNED = 1u << 24;

        private static bool s_initialized;
        private static ulong s_mmio;
        private static ulong s_opBase;
        private static ulong s_runtimeBase;
        private static ulong s_doorbellBase;
        private static uint s_maxSlots;
        private static uint s_maxPorts;
        private static uint s_pageSize;
        private static ushort s_version;
        private static bool s_contextSize64;
        private static bool s_tookOwnership;
        private static string s_failure;

        public static bool IsInitialized => s_initialized;
        public static uint MaxSlots => s_maxSlots;
        public static uint MaxPorts => s_maxPorts;
        public static uint PageSize => s_pageSize;
        public static ushort Version => s_version;
        public static bool ContextSize64 => s_contextSize64;
        public static bool TookOwnership => s_tookOwnership;
        public static ulong MmioBase => s_mmio;
        public static ulong OperationalBase => s_opBase;
        public static ulong RuntimeBase => s_runtimeBase;
        public static ulong DoorbellBase => s_doorbellBase;

        /// <summary>Why Init returned false, or null when it did not fail.</summary>
        public static string Failure => s_failure;

        public static bool Init()
        {
            if (s_initialized) return true;
            s_failure = null;

            if (!UsbHost.TryFind(UsbHost.Kind.Xhci, out UsbHost.Controller c))
                return Fail("no xHCI controller on the PCI bus");
            if (c.MmioBase == 0)
                return Fail("xHCI BAR is not memory-mapped");

            // The register window is 64 KiB at most in practice; map generously
            // but as a device, or the reads below come back from a cache line.
            if (!OS.Kernel.Memory.VirtualMemory.MapFixed(
                    (void*)c.MmioBase, c.MmioBase, 0x10000, exec: false,
                    OS.Kernel.Memory.VirtualMemory.MemoryKind.Device))
                return Fail("xHCI MMIO map failed");

            s_mmio = c.MmioBase;

            // CAPLENGTH and HCIVERSION share one 32-bit register. Reading the
            // version as a ushort at +2 returns 0 on QEMU: MMIO registers are
            // only guaranteed at their natural width, and sub-dword accesses
            // are not decoded. Read the dword and split it.
            uint capReg = *(uint*)(s_mmio + CAP_CAPLENGTH);
            byte capLength = (byte)(capReg & 0xFF);
            s_version = (ushort)(capReg >> 16);

            if (capLength < 0x20 || capLength > 0x80)
                return Fail("xHCI CAPLENGTH out of range (wrong BAR?)");
            uint hcs1 = *(uint*)(s_mmio + CAP_HCSPARAMS1);
            uint hcc1 = *(uint*)(s_mmio + CAP_HCCPARAMS1);

            s_maxSlots = hcs1 & 0xFF;
            s_maxPorts = (hcs1 >> 24) & 0xFF;
            s_contextSize64 = (hcc1 & (1u << 2)) != 0;

            s_opBase = s_mmio + capLength;
            s_runtimeBase = s_mmio + (*(uint*)(s_mmio + CAP_RTSOFF) & ~0x1Fu);
            s_doorbellBase = s_mmio + (*(uint*)(s_mmio + CAP_DBOFF) & ~0x3u);

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

            s_pageSize = *(uint*)(s_opBase + OP_PAGESIZE);
            s_initialized = true;
            return true;
        }

        private static bool Fail(string why)
        {
            s_failure = why;
            return false;
        }

        // Walk the extended capability list and perform the BIOS/OS handshake
        // if a legacy-support capability is present. Its absence is normal
        // (QEMU has none) and is not an error.
        private static bool TryTakeOwnership(uint hcc1)
        {
            uint xecpDwords = (hcc1 >> 16) & 0xFFFF;
            if (xecpDwords == 0) return true;

            ulong p = s_mmio + xecpDwords * 4UL;
            for (int guard = 0; guard < 64; guard++)
            {
                uint cap = *(uint*)p;
                if (cap == 0xFFFFFFFF || cap == 0) return true;

                uint id = cap & 0xFF;
                if (id == XECP_LEGACY)
                    return TryLegacyHandoff(p);

                uint next = (cap >> 8) & 0xFF;
                if (next == 0) return true;          // end of list
                p += next * 4UL;
            }
            return true;   // malformed list — nothing claimed it, treat as free
        }

        private static bool TryLegacyHandoff(ulong legacyReg)
        {
            uint* usblegsup = (uint*)legacyReg;
            if ((*usblegsup & LEGACY_BIOS_OWNED) == 0)
            {
                *usblegsup = *usblegsup | LEGACY_OS_OWNED;
                s_tookOwnership = true;
                return true;
            }

            *usblegsup = *usblegsup | LEGACY_OS_OWNED;

            // The firmware finishes its own teardown asynchronously; the spec
            // suggests a second, and a machine that never clears the bit must
            // not hang the boot.
            if (!WaitUntil(1000, legacyReg, LEGACY_BIOS_OWNED, expectSet: false))
                return false;

            s_tookOwnership = true;
            return true;
        }

        private static bool TryReset()
        {
            uint* usbcmd = (uint*)(s_opBase + OP_USBCMD);

            // Stop first: resetting a running controller is undefined.
            *usbcmd = *usbcmd & ~USBCMD_RS;
            if (!WaitUntil(200, s_opBase + OP_USBSTS, USBSTS_HCH, expectSet: true))
                return false;

            *usbcmd = *usbcmd | USBCMD_HCRST;

            // Reset is done when HCRST self-clears AND the controller stops
            // reporting "not ready" — reading other registers before CNR
            // clears returns garbage.
            if (!WaitUntil(1000, s_opBase + OP_USBCMD, USBCMD_HCRST, expectSet: false))
                return false;
            if (!WaitUntil(1000, s_opBase + OP_USBSTS, USBSTS_CNR, expectSet: false))
                return false;

            return true;
        }

        // Poll a register bit with a real deadline. NoInlining on the read
        // keeps the compiler from hoisting it out of the loop, which is the
        // documented way this kind of wait turns into a hang.
        private static bool WaitUntil(uint timeoutMs, ulong address, uint mask, bool expectSet)
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
        private static uint Read32(ulong address) => *(uint*)address;

        private static ulong Deadline(uint ms)
        {
            if (!OS.Hal.Timer.Hpet.IsInitialized) return 0;
            ulong hz = OS.Hal.Timer.Hpet.FrequencyHz;
            return OS.Hal.Timer.Hpet.ReadCounter() + hz / 1000UL * ms;
        }

        private static bool Expired(ulong deadline)
        {
            // No timer means no deadline; the bounded caller loops are the
            // only guard left, so never claim an expiry we cannot measure.
            if (deadline == 0) return false;
            return OS.Hal.Timer.Hpet.ReadCounter() >= deadline;
        }
    }
}
