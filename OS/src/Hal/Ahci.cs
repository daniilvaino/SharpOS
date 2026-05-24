// Driver implementations adapted from MOOS by nifanfa
// (https://github.com/nifanfa/MOOS), released under the Unlicense
// (public domain). Standing on shoulders of fellow public domain contributors.
//
// Ported from MOOS Kernel/Driver/SATA.cs. Cuts / remaps:
//  - MOOS.FS.Disk -> OS.Hal.Disk; namespace -> OS.Hal; public->internal.
//  - PCI.Devices registry -> OS.Hal.Pci.TryFind(0x01,0x06); PCI command
//    register enabled via ECAM (memory-space + bus-master bits).
//  - Allocator.Allocate/ZeroFill -> AllocDma: the HBA reads/writes
//    command list / FIS / PRDT / data by PHYSICAL address, so these
//    buffers are PhysicalMemory pages identity-mapped (virt==phys),
//    low-RAM (<4G), zero-filled. ABAR is MMIO above RAM ->
//    identity-mapped like ECAM/framebuffer.
//  - Native.Stosb -> inline zero loop. BitHelpers -> inline bit ops.
//  - List<SATADevice> -> first usable SATA port only (the boot disk);
//    Console.ToString/Panic.Error dropped. Write kept but unused (the
//    FAT layer is read-only).
//
// Pure MMIO + polling, no Native/port I/O -> Invariant-1 clean.

using System.Runtime.CompilerServices;
using OS.Kernel;
using OS.Kernel.Memory;

namespace OS.Hal
{
    internal static unsafe class Ahci
    {
        private const uint GhcAhciEnable = 1u << 31;
        private const uint GhcInterruptEnable = 1u << 1;

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
        public struct HBA
        {
            public uint HostCapability;
            public uint GlobalHostControl;
            public uint InterruptStatus;
            public uint PortsImplemented;
            public uint Version;
            public uint CCCControl;
            public uint CCCPorts;
            public uint EnclosureManagementLocation;
            public uint EnclosureManagementControl;
            public uint HostCapabilitiesExtended;
            public uint BIOSHandoffControlStatus;
            public fixed byte Reserved0[0x74];
            public fixed byte Vendor[0x60];
            public HBAPort Ports;
        }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
        public struct HBAPort
        {
            public ulong CommandListBase;
            public ulong FISBaseAddress;
            public uint InterruptStatus;
            public uint InterruptEnable;
            public uint CommandStatus;
            public uint Reserved0;
            public uint TaskFileData;
            public uint Signature;
            public uint SataStatus;
            public uint SataControl;
            public uint SataError;
            public uint SataActive;
            public uint CommandIssue;
            public uint SataNotification;
            public uint FISSwitchControl;
            public fixed uint Reserved1[11];
            public fixed uint Vendor[4];
        }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
        struct HBACommandHeader
        {
            public byte P1;
            public byte P2;
            public ushort PRDTLength;
            public uint PRDBCount;
            public ulong CommandTableBaseAddress;
            public fixed uint Reserved1[4];

            public byte CommandFISLength
            {
                get => (byte)(P1 & 0x1F);
                set { unchecked { P1 &= (byte)~0x1F; } P1 |= (byte)(value & 0x1F); }
            }
            public bool Write
            {
                get => (P1 & (1 << 6)) != 0;
                set { if (value) P1 |= 1 << 6; else unchecked { P1 &= (byte)~(1 << 6); } }
            }
            public bool ClearBusy
            {
                get => (P2 & (1 << 2)) != 0;
                set { if (value) P2 |= 1 << 2; else unchecked { P2 &= (byte)~(1 << 2); } }
            }
        }

        public enum SATAPortType { NONE = 0, SATA = 1, SEMB = 2, PM = 3, ATAPI = 4 }

        public static HBA* Controller = null;
        public static SATADevice Device = null;

        private static ulong s_readCommands;
        private static ulong s_readSectors;
        private static ulong s_readTicks;
        private static ulong s_readFailures;

        public static ulong ReadCommands => s_readCommands;
        public static ulong ReadSectors => s_readSectors;
        public static ulong ReadTicks => s_readTicks;
        public static ulong ReadFailures => s_readFailures;

        private static void DisableInterrupts(HBA* controller, HBAPort* port)
        {
            if (controller != null)
            {
                controller->GlobalHostControl =
                    (controller->GlobalHostControl | GhcAhciEnable) & ~GhcInterruptEnable;
                unchecked { controller->InterruptStatus = (uint)-1; }
            }

            if (port != null)
            {
                port->InterruptEnable = 0;
                unchecked { port->InterruptStatus = (uint)-1; }
            }
        }

        // DMA page(s): physical from PhysicalMemory, identity-mapped so
        // virt==phys (the HBA reads/writes these by physical address),
        // zero-filled. Low RAM => 32-bit-safe.
        public static ulong AllocDma(uint pages)
        {
            ulong phys = PhysicalMemory.AllocPages(pages);
            if (phys == 0) return 0;
            ulong size = (ulong)pages * 4096UL;
            if (!VirtualMemory.MapFixed((void*)phys, phys, size, exec: false))
                return 0;
            byte* b = (byte*)phys;
            for (ulong i = 0; i < size; i++) b[i] = 0;
            return phys;
        }

        // Non-hoistable MMIO read. Without this the JIT caches a port
        // register across a poll loop (the loop body never writes
        // through Port, so the read looks loop-invariant) and the spin
        // never observes hardware progress. NoInlining forces a real
        // load every poll — correctness by design, not by an
        // accidental call barrier elsewhere in the loop.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static uint Rd(uint* p) => *p;

        private static ulong NowTicks()
            => global::OS.Hal.Timer.Hpet.IsInitialized
                ? global::OS.Hal.Timer.Hpet.ReadCounter()
                : 0;

        private static ulong ElapsedTicks(ulong start)
        {
            if (start == 0) return 0;
            ulong end = NowTicks();
            return end >= start ? end - start : 0;
        }

        private static void RecordRead(uint sectors, ulong start, bool ok)
        {
            s_readCommands++;
            s_readSectors += sectors;
            s_readTicks += ElapsedTicks(start);
            if (!ok) s_readFailures++;
        }

        public static bool Initialize()
        {
            if (Device != null) return true;          // idempotent
            if (!Pci.TryFind(0x01, 0x06, out Pci.PciDev dev)) return false;

            // Enable memory space + bus master in the PCI command reg
            // (ECAM offset 0x04). ECAM window already identity-mapped by
            // Pci.Init.
            ushort* cmd = (ushort*)(dev.EcamAddress + 0x04);
            *cmd |= 0x0006;

            ulong abar = dev.Bar5 & ~0xFUL;
            if (abar == 0) return false;
            if (!VirtualMemory.MapFixed((void*)abar, abar, 0x2000, exec: false))
                return false;
            Controller = (HBA*)abar;
            DisableInterrupts(Controller, null);

            for (int k = 0; k < 32; k++)
            {
                if ((Controller->PortsImplemented & (1u << k)) == 0) continue;
                HBAPort* port = &(&Controller->Ports)[k];
                DisableInterrupts(Controller, port);
                SATAPortType type = CheckPortType(port);
                if (type != SATAPortType.SATA && type != SATAPortType.ATAPI) continue;

                SATADevice sata = new SATADevice();
                sata.PortType = type;
                sata.Port = port;
                if (!sata.Configure()) return false;
                Device = sata;            // base ctor also set Disk.Instance
                return true;
            }
            return false;
        }

        public static SATAPortType CheckPortType(HBAPort* port)
        {
            uint statStat = port->SataStatus;
            byte intpowman = (byte)((statStat >> 8) & 0b111);
            byte devdetect = (byte)(statStat & 0b111);
            if (devdetect != 0x3) return SATAPortType.NONE;
            if (intpowman != 0x1) return SATAPortType.NONE;
            switch (port->Signature)
            {
                case 0x00000101: return SATAPortType.SATA;
                case 0xEB140101: return SATAPortType.ATAPI;
                case 0x96690101: return SATAPortType.PM;
                case 0xC33C0101: return SATAPortType.SEMB;
                default: return SATAPortType.NONE;
            }
        }

        public sealed class SATADevice : Disk
        {
            [System.Runtime.InteropServices.StructLayout(
                System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
            struct FIS_REG_H2D
            {
                public byte FISType;
                public byte PMC;              // PortMultiplier | (CmdCtrl<<7)
                public byte Command;
                public byte FeatureLow;
                public byte LBA0;
                public byte LBA1;
                public byte LBA2;
                public byte DeviceRegister;
                public byte LBA3;
                public byte LBA4;
                public byte LBA5;
                public byte FeatureHigh;
                public ushort Count;
                public byte ISOCommandCompletion;
                public byte Control;
                public fixed byte Reserved1[4];
            }

            [System.Runtime.InteropServices.StructLayout(
                System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
            struct HBAPRDTEntry
            {
                public ulong DataBaseAddress;
                public uint Reserved0;
                public uint ByteCount_IOC;

                public bool InterruptOnCompletion
                {
                    set { if (value) ByteCount_IOC |= 1U << 31;
                          else unchecked { ByteCount_IOC &= ~(1U << 31); } }
                }
                public uint ByteCount
                {
                    set { ByteCount_IOC &= ~0x3FFFFFU; ByteCount_IOC |= value & 0x3FFFFFU; }
                }
            }

            [System.Runtime.InteropServices.StructLayout(
                System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
            struct HBACommandTable
            {
                public fixed byte CommandFIS[64];
                public fixed byte ATAPICommand[16];
                public fixed byte Reserved[48];
                public HBAPRDTEntry PRDTEntry;
            }

            public SATAPortType PortType;
            public HBAPort* Port;

            public bool Configure()
            {
                DisableInterrupts(Controller, Port);
                if (!StopCMD()) return false;
                DisableInterrupts(Controller, Port);

                ulong clb = AllocDma(1);
                if (clb == 0) return false;
                Port->CommandListBase = clb;

                ulong fb = AllocDma(1);
                if (fb == 0) return false;
                Port->FISBaseAddress = fb;

                HBACommandHeader* cmdhdr = (HBACommandHeader*)clb;
                for (int i = 0; i < 32; i++)
                {
                    cmdhdr[i].PRDTLength = 8;
                    ulong ct = AllocDma(1);
                    if (ct == 0) return false;
                    cmdhdr[i].CommandTableBaseAddress = ct;
                }

                bool started = StartCMD();
                DisableInterrupts(Controller, Port);
                return started;
            }

            // Bounded busy-wait by REAL TIME (HPET), not iteration
            // count: a stuck bit must fail loudly, never spin forever,
            // but a slow-but-completing command (QEMU VVFAT lazily
            // materialises sectors — the 2nd+ I/O is much slower than
            // the 1st) must not be cut off mid-flight. Returns a HPET
            // tick deadline `ms` from now; Expired() checks it. Falls
            // back to a large iteration budget if HPET isn't up.
            private const long FallbackSpins = 2_000_000_000;

            private static ulong Deadline(uint ms)
            {
                if (!global::OS.Hal.Timer.Hpet.IsInitialized) return 0;
                ulong hz = global::OS.Hal.Timer.Hpet.FrequencyHz;
                return global::OS.Hal.Timer.Hpet.ReadCounter() + hz / 1000UL * ms;
            }

            private static bool Expired(ulong deadline, ref long spins)
            {
                if (deadline != 0)
                    return global::OS.Hal.Timer.Hpet.ReadCounter() >= deadline;
                return --spins <= 0;
            }

            public bool StartCMD()
            {
                ulong dl = Deadline(2000); long sp = FallbackSpins;
                while ((Rd(&Port->CommandStatus) & 0x8000) != 0)
                    if (Expired(dl, ref sp)) return false;
                Port->CommandStatus |= 0x0010;
                Port->CommandStatus |= 0x0001;
                return true;
            }

            public bool StopCMD()
            {
                Port->CommandStatus &= ~0x0001U;
                Port->CommandStatus &= ~0x0010U;
                ulong dl = Deadline(2000); long sp = FallbackSpins;
                while (true)
                {
                    if ((Rd(&Port->CommandStatus) & 0x4000) != 0) { if (Expired(dl, ref sp)) return false; continue; }
                    if ((Rd(&Port->CommandStatus) & 0x8000) != 0) { if (Expired(dl, ref sp)) return false; continue; }
                    break;
                }
                return true;
            }

            public const int SectorSize = 512;

            public override bool Read(ulong sector, uint count, byte* p)
            {
                ulong start = NowTicks();
                bool ok = ReadOrWrite(sector, (ushort)count, p, false);
                RecordRead(count, start, ok);
                return ok;
            }

            public override bool Write(ulong sector, uint count, byte* p)
                => ReadOrWrite(sector, (ushort)count, p, true);

            private bool ReadOrWrite(ulong Sector, ushort Count, byte* Buffer, bool Write)
            {
                if (Count == 0 || Count >= 512) return false;
                if (PortType == SATAPortType.ATAPI && Write) return false;
                DisableInterrupts(Controller, Port);
                int Slot = FindSlot();
                if (Slot == -1) return false;

                ushort totalCount = Count;
                ushort remaining = Count;
                HBACommandHeader* hdr = (HBACommandHeader*)Port->CommandListBase;
                hdr += Slot;
                hdr->CommandFISLength = (byte)(sizeof(FIS_REG_H2D) / sizeof(uint));
                hdr->Write = Write;
                hdr->ClearBusy = true;
                hdr->PRDTLength = (ushort)(((totalCount - 1) >> 4) + 1);

                HBACommandTable* table = (HBACommandTable*)hdr->CommandTableBaseAddress;
                byte* zt = (byte*)table;
                ulong zsize = (ulong)(sizeof(HBACommandTable)
                                      + (hdr->PRDTLength - 1) * sizeof(HBAPRDTEntry));
                for (ulong z = 0; z < zsize; z++) zt[z] = 0;

                int i;
                for (i = 0; i < hdr->PRDTLength - 1; i++)
                {
                    (&table->PRDTEntry)[i].DataBaseAddress = (ulong)Buffer;
                    (&table->PRDTEntry)[i].ByteCount = 8 * 1024 - 1;
                    (&table->PRDTEntry)[i].InterruptOnCompletion = false;
                    Buffer += 8 * 1024;
                    remaining = (ushort)(remaining - 16);
                }
                (&table->PRDTEntry)[i].DataBaseAddress = (ulong)Buffer;
                (&table->PRDTEntry)[i].ByteCount = (uint)((remaining << 9) - 1);
                (&table->PRDTEntry)[i].InterruptOnCompletion = false;

                FIS_REG_H2D* FIS = (FIS_REG_H2D*)table->CommandFIS;
                FIS->FISType = 0x27;
                FIS->PMC = 1 << 7;                 // CommandControl=1
                FIS->Command = (byte)(Write ? 0x35 : 0x25);
                FIS->LBA0 = (byte)(Sector & 0xFF);
                FIS->LBA1 = (byte)((Sector >> 8) & 0xFF);
                FIS->LBA2 = (byte)((Sector >> 16) & 0xFF);
                FIS->LBA3 = (byte)((Sector >> 24) & 0xFF);
                FIS->LBA4 = (byte)((Sector >> 32) & 0xFF);
                FIS->LBA5 = (byte)((Sector >> 40) & 0xFF);
                FIS->DeviceRegister = 1 << 6;
                FIS->Count = totalCount;

                // Wait device-ready, issue, wait completion. All polls
                // read MMIO through Rd() (no compile-time hoist) and
                // are bounded by a real-time HPET deadline (a stuck
                // bit fails loudly; a slow-but-completing command — the
                // 2nd+ VVFAT access is much slower than the 1st — is
                // not cut off).
                ulong dl = Deadline(2000); long sp = FallbackSpins;
                while ((Rd(&Port->TaskFileData) & (0x80 | 0x08)) != 0)
                    if (Expired(dl, ref sp)) return false;

                Port->CommandIssue = (uint)(1 << Slot);

                dl = Deadline(8000); sp = FallbackSpins;
                while (true)
                {
                    if ((Rd(&Port->CommandIssue) & (1 << Slot)) == 0) break;
                    if ((Rd(&Port->InterruptStatus) & (1 << 30)) != 0) return false;
                    if (Expired(dl, ref sp)) return false;
                }
                if ((Rd(&Port->InterruptStatus) & (1 << 30)) != 0) return false;

                dl = Deadline(2000); sp = FallbackSpins;
                while (Rd(&Port->CommandIssue) != 0)
                    if (Expired(dl, ref sp)) return false;
                DisableInterrupts(Controller, Port);
                return true;
            }

            public int FindSlot()
            {
                uint Slots = Port->SataActive | Port->CommandIssue;
                for (int i = 0; i < 32; i++)
                    if ((Slots & (1 << i)) == 0) return i;
                return -1;
            }
        }
    }
}
