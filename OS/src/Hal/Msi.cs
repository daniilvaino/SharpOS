using OS.Kernel.Memory;

#pragma warning disable CS0162 // Probes/Trace switches are compile-time constants: the branch a switch turns off is meant to be dead (docs/build-warnings.md)

namespace OS.Hal
{
    // Message-signalled interrupts for a PCI function (plan §11, stage 1).
    //
    // A device with MSI writes a word to an address instead of raising a
    // pin: the address names the local APIC (0xFEE00000 | id << 12), the word
    // carries the vector. No IOAPIC, no routing tables, no shared lines — the
    // controllers QEMU and the rig have (AHCI, xHCI, network cards) all do it.
    //
    // MSI is preferred where both exist: one vector is all a driver here asks
    // for, and MSI needs no table in a BAR. MSI-X is taken when it is the only
    // one (qemu-xhci offers both; some real xHCI controllers only MSI-X).
    // Either way the legacy INTx pin is switched off (PCI command bit 10).
    internal static unsafe class Msi
    {
        private const byte CapMsi = 0x05;
        private const byte CapMsiX = 0x11;

        /// <summary>
        /// Points the function's MSI (or MSI-X entry 0) at this CPU's local APIC
        /// with <paramref name="vector"/> and enables it. False when the function
        /// has neither; <paramref name="kind"/> says which was used.
        /// </summary>
        public static bool TryEnable(Pci.PciDev dev, byte vector, out string kind)
        {
            kind = "none";
            if (dev.EcamAddress == 0) return false;
            byte* cfg = (byte*)dev.EcamAddress;

            uint msi = Find(cfg, CapMsi);
            uint msix = Find(cfg, CapMsiX);
            ulong address = 0xFEE00000UL | ((ulong)OS.Hal.Apic.LocalApic.Id << 12);

            if (msi != 0)
            {
                ushort control = *(ushort*)(cfg + msi + 2);
                bool wide = (control & 0x80) != 0;
                *(uint*)(cfg + msi + 4) = (uint)address;
                if (wide) *(uint*)(cfg + msi + 8) = 0;
                *(ushort*)(cfg + msi + (wide ? 12u : 8u)) = vector;      // fixed, edge
                if ((control & 0x100) != 0)                              // per-vector masks: unmask
                    *(uint*)(cfg + msi + (wide ? 16u : 12u)) = 0;
                control = (ushort)((control & ~0x70) | 1);               // one vector, enabled
                *(ushort*)(cfg + msi + 2) = control;
                kind = "msi";
            }
            else if (msix != 0)
            {
                ushort control = *(ushort*)(cfg + msix + 2);
                uint entries = (uint)(control & 0x7FF) + 1;
                uint table = *(uint*)(cfg + msix + 4);
                ulong bar = BarAddress(dev, (int)(table & 7));
                if (bar == 0) return false;
                ulong at = bar + (table & ~7u);
                ulong page = at & ~0xFFFUL;
                ulong span = ((at + entries * 16UL + 0xFFFUL) & ~0xFFFUL) - page;
                if (!VirtualMemory.MapFixed((void*)page, page, span, exec: false,
                                            VirtualMemory.MemoryKind.Device))
                    return false;

                // Function mask while the table is written, then enable.
                *(ushort*)(cfg + msix + 2) = (ushort)(control | 0x4000 | 0x8000);
                uint* e = (uint*)at;
                for (uint i = 0; i < entries; i++) e[i * 4 + 3] = 1;    // every entry masked
                e[0] = (uint)address;
                e[1] = (uint)(address >> 32);
                e[2] = vector;
                e[3] = 0;                                                 // entry 0 unmasked
                *(ushort*)(cfg + msix + 2) = (ushort)((control | 0x8000) & ~0x4000);
                kind = "msi-x";
            }
            else
            {
                return false;
            }

            ushort* command = (ushort*)(cfg + 4);
            *command = (ushort)(*command | 0x0400);                       // INTx off
            return true;
        }

        // The capability list (status bit 4), walked with a bound: a list
        // that loops back on itself must not hang the boot.
        private static uint Find(byte* cfg, byte id)
        {
            if ((*(ushort*)(cfg + 6) & 0x10) == 0) return 0;
            uint at = (uint)(cfg[0x34] & 0xFC);
            for (int guard = 0; at >= 0x40 && guard < 48; guard++)
            {
                if (cfg[at] == id) return at;
                at = (uint)(cfg[at + 1] & 0xFC);
            }
            return 0;
        }

        private static ulong BarAddress(Pci.PciDev dev, int index)
        {
            uint* bars = (uint*)(dev.EcamAddress + 0x10);
            if (index > 5) return 0;
            uint low = bars[index];
            if ((low & 1) != 0) return 0;                                 // I/O space
            ulong address = low & ~0xFUL;
            if (((low >> 1) & 3) == 2 && index < 5)                       // 64-bit BAR
                address |= (ulong)bars[index + 1] << 32;
            return address;
        }
    }

    // Device interrupt vectors from 0x30, one per source, each with a handler
    // the dispatcher calls with interrupts off. A handler must not allocate,
    // block or take a lock: it reads and clears the device's status, notes it
    // for the driver and posts the source (IrqWait.Post).
    internal static unsafe class DeviceIrq
    {
        public const int FirstVector = 0x30;
        public const int MaxSources = OS.Kernel.Threading.IrqWait.MaxSources;

        private static int s_count;
        private static nint* s_handlers;
        private static ulong* s_counts;

        public static int Count => s_count;

        /// <summary>Interrupts taken from one source since it was wired.</summary>
        public static ulong Taken(int source)
            => s_counts == null || (uint)source >= (uint)s_count ? 0 : s_counts[source];

        /// <summary>
        /// A vector for a new source, its IDT gate wired to the dispatcher. -1
        /// without a local APIC (UEFI still owns interrupts, or none at all)
        /// or with no room left.
        /// </summary>
        public static int Register(delegate*<void> handler)
        {
            if (!OS.Kernel.Diagnostics.Probes.DeviceInterrupts) return -1;
            if (!OS.Hal.Apic.LocalApic.IsEnabled || s_count >= MaxSources) return -1;
            if (s_handlers == null)
            {
                s_handlers = (nint*)KernelHeap.Alloc((uint)(MaxSources * sizeof(nint)));
                s_counts = (ulong*)KernelHeap.Alloc((uint)(MaxSources * sizeof(ulong)));
                if (s_handlers == null || s_counts == null) return -1;
                for (int i = 0; i < MaxSources; i++) { s_handlers[i] = 0; s_counts[i] = 0; }
                OS.Kernel.Threading.IrqWait.Init();
            }
            int source = s_count;
            if (!OS.Hal.Idt.Idt.TryWireIrqVector(FirstVector + source)) return -1;
            s_handlers[source] = (nint)handler;
            s_count++;
            return source;
        }

        public static byte VectorOf(int source) => (byte)(FirstVector + source);

        /// <summary>From the interrupt dispatcher: false when the vector is not a device's.</summary>
        public static bool Dispatch(int vector)
        {
            int source = vector - FirstVector;
            if ((uint)source >= (uint)s_count) return false;
            s_counts[source]++;
            ((delegate*<void>)s_handlers[source])();
            return true;
        }
    }
}
