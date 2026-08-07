// Driver implementations adapted from MOOS by nifanfa
// (https://github.com/nifanfa/MOOS), released under the Unlicense
// (public domain). Standing on shoulders of fellow public domain contributors.
//
// Ported from MOOS Kernel/Driver/PCIExpress.cs. Cuts: dropped MOOS
// PCIDevice registry / legacy-PCI bridge recursion / Console/name
// tables; ACPI.MCFG -> OS.Hal.Acpi.Mcfg; added the ECAM MMIO identity
// map (the segment window lives above RAM, unmapped in our pager —
// same situation the framebuffer needed). Pure MMIO pointer config
// access — no Native/port I/O, so it is Invariant-1 clean as-is.
//
// Scope: QEMU q35 puts integrated devices (ICH9 AHCI = 00:1f.2) on
// bus 0; we map+scan buses 0..MaxBus only (bounded — full 256-bus
// ECAM would be a 256 MiB identity map).

using OS.Kernel.Memory;

namespace OS.Hal
{
    internal static unsafe class Pci
    {
        // A desktop board fills bus 0 well past 32 functions once its root
        // ports, xHCI, audio, SATA and management engine are counted, and the
        // old limits (2 buses, 32 devices) truncated the scan silently — a
        // device simply was not there, with nothing said. The window costs one
        // MiB of identity mapping per bus, so 8 is cheap.
        // Buses are followed through bridges rather than swept as a range.
        // A flat 0..7 sweep missed an AMD desktop's xHCI entirely — those sit
        // at device 0 function 3 of a bus that a bridge assigns well above the
        // sweep — so the machine's boot controller simply did not exist as far
        // as we were concerned, and with it the stick we booted from.
        //
        // The window is still mapped a bus at a time (1 MiB each): mapping all
        // 256 up front would be 256 MiB of identity mapping for a handful of
        // live buses.
        private const int MaxBusIndex = 256;
        // A desktop fills this faster than it looks: every root port, bridge,
        // audio, storage and management function counts. Overflowing used to
        // abandon the scan outright, which hid whatever had not been reached
        // yet — including, on one machine, the controller we boot from.
        private const int MaxDevs = 256;

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
        private struct Header
        {
            public ushort VendorID;
            public ushort DeviceID;
            public ushort Command;
            public ushort Status;
            public byte RevisionID;
            public byte ProgIF;
            public byte SubClass;
            public byte ClassID;
            public byte CachelineSize;
            public byte LatencyTimer;
            public byte HeaderType;
            public byte BIST;
        }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
        private struct DeviceHeader
        {
            public Header Header;
            public uint Bar0, Bar1, Bar2, Bar3, Bar4, Bar5;
            public uint CardbusCisPtr;
            public ushort SubSystemVendorID;
            public ushort SubSystemID;
            public uint ExpRomBaseAddr;
            public byte CapabPtr;
            public byte Reserved0;
            public ushort Reserved1;
            public uint Reserved2;
            public byte InterruptLine;
            public byte InterruptPin;
            public byte MinGrid;
            public byte MaxLatency;
        }

        // Flat record of a discovered function.
        public struct PciDev
        {
            public byte Bus, Slot, Func;
            public ushort VendorID, DeviceID;
            public byte ClassID, SubClassID, ProgIF, CapabPtr;
            public uint Bar0, Bar1, Bar2, Bar3, Bar4, Bar5;
            public ulong EcamAddress;        // config-space MMIO base for this fn
        }

        private static readonly PciDev[] s_devs = new PciDev[MaxDevs];
        private static int s_count;
        private static bool s_scanned;
        private static bool s_truncated;

        public static int Count => s_count;

        /// <summary>True when the scan stopped early: some devices were never
        /// looked at, so "not found" means nothing until this is false.</summary>
        public static bool Truncated => s_truncated;
        public static int BusesScanned => s_busesScanned;
        private static int s_busesScanned;
        public static PciDev Get(int i) => s_devs[i];

        // ECAM: cfg space of (bus,slot,func) at
        //   Base + (bus<<20) + (slot<<15) + (func<<12).
        public static void Init()
        {
            if (s_scanned) return;
            s_scanned = true;
            s_count = 0;

            if (!OS.Hal.Acpi.Mcfg.IsAvailable) return;

            // Every segment, not just the first: a machine that splits its
            // buses across segments keeps whole controllers in the ones we
            // would otherwise never look at.
            int entries = OS.Hal.Acpi.Mcfg.EntryCount;
            for (int e = 0; e < entries; e++)
                ScanSegment(e);
        }

        private static void ScanSegment(int entryIndex)
        {
            if (!OS.Hal.Acpi.Mcfg.TryGetEntry(entryIndex, out ulong baseAddr,
                    out ushort segment, out byte startBus, out byte endBus))
                return;

            _ = segment;

            // Work list of buses still to visit, seeded with the segment's
            // first. Bridges append the buses behind them as they are found.
            byte* pending = stackalloc byte[MaxBusIndex];
            bool* seen = stackalloc bool[MaxBusIndex];
            for (int i = 0; i < MaxBusIndex; i++) seen[i] = false;

            int pendingCount = 0;
            pending[pendingCount++] = startBus;
            seen[startBus] = true;

            while (pendingCount > 0)
            {
                byte bus = pending[--pendingCount];
                if (!TryMapBus(baseAddr, startBus, endBus, bus, out ulong busAddr)) continue;
                s_busesScanned++;

                for (byte slot = 0; slot < 32; slot++)
                {
                    ulong slotAddr = busAddr + ((ulong)slot << 15);
                    Header* h0 = (Header*)slotAddr;
                    if (h0->VendorID == 0 || h0->VendorID == 0xFFFF) continue;

                    int funcs = (h0->HeaderType & 0x80) != 0 ? 8 : 1;
                    for (byte func = 0; func < funcs; func++)
                    {
                        ulong fnAddr = slotAddr + ((ulong)func << 12);
                        DeviceHeader* d = (DeviceHeader*)fnAddr;
                        if (d->Header.VendorID == 0 || d->Header.VendorID == 0xFFFF)
                            continue;

                        // A bridge (header type 1) names the bus behind it in
                        // its secondary-bus byte. Following those is the only
                        // way to reach devices that no fixed range covers.
                        if ((d->Header.HeaderType & 0x7F) == 1)
                        {
                            byte secondary = ((byte*)fnAddr)[0x19];
                            if (secondary != 0 && !seen[secondary]
                                && pendingCount < MaxBusIndex)
                            {
                                seen[secondary] = true;
                                pending[pendingCount++] = secondary;
                            }
                        }

                        // Out of room: keep walking so bridges are still
                        // followed, but say so — "not found" is meaningless
                        // once anything was skipped.
                        if (s_count >= MaxDevs) { s_truncated = true; continue; }

                        ref PciDev r = ref s_devs[s_count++];
                        r.Bus = bus; r.Slot = slot; r.Func = func;
                        r.VendorID = d->Header.VendorID;
                        r.DeviceID = d->Header.DeviceID;
                        r.ClassID = d->Header.ClassID;
                        r.SubClassID = d->Header.SubClass;
                        r.ProgIF = d->Header.ProgIF;
                        r.CapabPtr = d->CapabPtr;
                        r.Bar0 = d->Bar0; r.Bar1 = d->Bar1; r.Bar2 = d->Bar2;
                        r.Bar3 = d->Bar3; r.Bar4 = d->Bar4; r.Bar5 = d->Bar5;
                        r.EcamAddress = fnAddr;
                    }
                }
            }
        }

        // Map one bus's 1 MiB slice of the ECAM window, on demand.
        private static bool TryMapBus(ulong baseAddr, byte startBus, byte endBus,
                                      byte bus, out ulong busAddr)
        {
            busAddr = 0;
            // Outside the segment's declared range there is no window: the
            // computed address would land on someone else's memory and read
            // as plausible nonsense rather than failing.
            if (bus < startBus || bus > endBus) return false;

            ulong offset = (ulong)(bus - startBus) << 20;
            busAddr = baseAddr + offset;
            return VirtualMemory.MapFixed((void*)busAddr, busAddr, 1UL << 20, exec: false,
                                          VirtualMemory.MemoryKind.Device);
        }

        // First function matching class/subclass (e.g. 0x01/0x06 = AHCI).
        public static bool TryFind(byte classId, byte subClass, out PciDev dev)
        {
            for (int i = 0; i < s_count; i++)
                if (s_devs[i].ClassID == classId && s_devs[i].SubClassID == subClass)
                { dev = s_devs[i]; return true; }
            dev = default;
            return false;
        }

        // Enable MMIO decoding and bus mastering for a function. The firmware
        // usually leaves both on, but a controller we are about to reset and
        // drive with DMA must not depend on that: without bus mastering the
        // device cannot touch our rings at all, and the failure is silent —
        // commands are posted and simply never complete.
        public static void EnableMemoryAndBusMaster(ref PciDev dev)
        {
            if (dev.EcamAddress == 0) return;
            ushort* command = (ushort*)(dev.EcamAddress + 4);
            *command = (ushort)(*command | 0x0006);   // bit1 memory space, bit2 bus master
        }

        public static bool TryFindVendor(ushort vendor, out PciDev dev)
        {
            for (int i = 0; i < s_count; i++)
                if (s_devs[i].VendorID == vendor)
                { dev = s_devs[i]; return true; }
            dev = default;
            return false;
        }
    }
}
