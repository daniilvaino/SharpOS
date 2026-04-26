namespace OS.Hal.Acpi
{
    // MADT — Multiple APIC Description Table. Signature "APIC".
    //
    // Layout after the 36-byte AcpiTableHeader:
    //   offset 36   u32  LocalApicAddress  (32-bit physical, may be overridden by entry type 5)
    //   offset 40   u32  Flags             (bit 0 = legacy PIC present)
    //   offset 44+  variable-length entries:
    //       u8 Type, u8 Length, then Type-specific bytes
    //
    // Entry types we care about:
    //   0  Processor Local APIC      (per-CPU info — used for SMP enumeration)
    //   1  IO APIC                   (IRQ routing)
    //   2  Interrupt Source Override (legacy IRQ remap)
    //   5  Local APIC Address Override (64-bit override of MADT.LocalApicAddress)
    //
    // For step 38 we just extract LocalApicAddress + count Local APICs +
    // count IO APICs. Detailed entry parsing comes in Phase 3 (SMP) and
    // Phase 5 (IRQ routing).
    internal static unsafe class Madt
    {
        public static bool IsAvailable => Acpi.MadtPtr != null;

        public static ulong LocalApicAddress
        {
            get
            {
                byte* madt = Acpi.MadtPtr;
                if (madt == null) return 0;
                ulong baseAddr = *(uint*)(madt + 36);

                // Walk entries looking for Type 5 (Local APIC Address Override).
                AcpiTableHeader* hdr = (AcpiTableHeader*)madt;
                byte* p = madt + 44;
                byte* end = madt + hdr->Length;
                while (p < end)
                {
                    byte type = p[0];
                    byte len = p[1];
                    if (len < 2) break;
                    if (type == 5 && len >= 16)
                    {
                        // Type 5: 2 byte type/length, 2 byte reserved, 8 byte 64-bit override.
                        baseAddr = *(ulong*)(p + 4);
                    }
                    p += len;
                }
                return baseAddr;
            }
        }

        public static int LocalApicCount => CountEntriesOfType(0);
        public static int IoApicCount    => CountEntriesOfType(1);

        private static int CountEntriesOfType(byte targetType)
        {
            byte* madt = Acpi.MadtPtr;
            if (madt == null) return 0;
            AcpiTableHeader* hdr = (AcpiTableHeader*)madt;
            byte* p = madt + 44;
            byte* end = madt + hdr->Length;
            int count = 0;
            while (p < end)
            {
                byte type = p[0];
                byte len = p[1];
                if (len < 2) break;
                if (type == targetType) count++;
                p += len;
            }
            return count;
        }
    }
}
