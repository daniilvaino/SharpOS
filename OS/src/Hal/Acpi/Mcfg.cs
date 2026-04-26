namespace OS.Hal.Acpi
{
    // MCFG — PCI Express Memory-mapped Configuration. Signature "MCFG".
    //
    // Layout after the 36-byte AcpiTableHeader:
    //   offset 36   u64 Reserved (8 bytes)
    //   offset 44   array of 16-byte Configuration Space Allocation entries:
    //       u64 BaseAddress       (ECAM base for this segment group)
    //       u16 SegmentGroupNumber
    //       u8  StartBus
    //       u8  EndBus
    //       u32 Reserved
    //
    // For each (segment, bus, device, function), the MMIO config space
    // lives at: BaseAddress + (bus << 20) + (device << 15) + (function << 12)
    //
    // Phase 5 PCI enumeration uses these entries to discover devices.
    internal static unsafe class Mcfg
    {
        public static bool IsAvailable => Acpi.McfgPtr != null;

        public static int EntryCount
        {
            get
            {
                byte* mcfg = Acpi.McfgPtr;
                if (mcfg == null) return 0;
                AcpiTableHeader* hdr = (AcpiTableHeader*)mcfg;
                int payloadBytes = (int)hdr->Length - 44; // header (36) + reserved (8)
                if (payloadBytes < 0) return 0;
                return payloadBytes / 16;
            }
        }

        public static bool TryGetEntry(int index, out ulong baseAddress, out ushort segment, out byte startBus, out byte endBus)
        {
            baseAddress = 0; segment = 0; startBus = 0; endBus = 0;
            if ((uint)index >= (uint)EntryCount) return false;

            byte* entry = Acpi.McfgPtr + 44 + index * 16;
            baseAddress = *(ulong*)(entry + 0);
            segment = *(ushort*)(entry + 8);
            startBus = entry[10];
            endBus = entry[11];
            return true;
        }
    }
}
