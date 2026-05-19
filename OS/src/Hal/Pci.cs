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
        private const int MaxBus = 2;        // buses scanned/mapped (0..MaxBus-1)
        private const int MaxDevs = 32;

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

        public static int Count => s_count;
        public static PciDev Get(int i) => s_devs[i];

        // ECAM: cfg space of (bus,slot,func) at
        //   Base + (bus<<20) + (slot<<15) + (func<<12).
        public static void Init()
        {
            if (s_scanned) return;
            s_scanned = true;
            s_count = 0;

            if (!OS.Hal.Acpi.Mcfg.IsAvailable) return;
            if (!OS.Hal.Acpi.Mcfg.TryGetEntry(0, out ulong baseAddr,
                    out ushort segment, out byte startBus, out byte _))
                return;

            // Identity-map the bus window we scan (MMIO above RAM —
            // unmapped in the pager PML4 by default).
            ulong winSize = (ulong)MaxBus << 20;
            if (!VirtualMemory.MapFixed((void*)baseAddr, baseAddr, winSize, exec: false))
                return;

            for (int busOff = 0; busOff < MaxBus; busOff++)
            {
                byte bus = (byte)(startBus + busOff);
                ulong busAddr = baseAddr + ((ulong)busOff << 20);

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
                        if (s_count >= MaxDevs) return;

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
                        _ = segment;
                    }
                }
            }
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
