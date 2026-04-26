namespace OS.Hal.Acpi
{
    // HPET — High Precision Event Timer description. Signature "HPET".
    //
    // Layout after the 36-byte AcpiTableHeader:
    //   offset 36   u32 EventTimerBlockId
    //   offset 40   GAS BaseAddress (12 bytes, ACPI Generic Address Structure):
    //                 u8 AddressSpaceId (0 = system memory)
    //                 u8 RegisterBitWidth
    //                 u8 RegisterBitOffset
    //                 u8 AccessSize
    //                 u64 Address     ← memory-mapped register block base
    //   offset 52   u8 HpetNumber
    //   offset 53   u16 MinimumTick
    //   offset 55   u8 PageProtection
    //
    // For step 38 we expose Base address. Phase 1d wires this to a
    // Stopwatch / clock source. The HPET register block itself starts
    // at Base and exposes 64-bit counter + N comparators.
    internal static unsafe class Hpet
    {
        public static bool IsAvailable => Acpi.HpetPtr != null;

        public static ulong Base
        {
            get
            {
                byte* hpet = Acpi.HpetPtr;
                if (hpet == null) return 0;
                // GAS Address field is at offset 36+4 (skip EventTimerBlockId)
                // + 4 (skip space/width/offset/accesssize) = offset 44.
                return *(ulong*)(hpet + 44);
            }
        }

        public static byte AddressSpaceId
        {
            get
            {
                byte* hpet = Acpi.HpetPtr;
                if (hpet == null) return 0xFF;
                return hpet[40];
            }
        }
    }
}
