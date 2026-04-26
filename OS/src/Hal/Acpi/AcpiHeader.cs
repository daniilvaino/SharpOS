using System.Runtime.InteropServices;

namespace OS.Hal.Acpi
{
    // Common 36-byte ACPI System Description Table header. Every ACPI
    // table (RSDT, XSDT, MADT, HPET, MCFG, FADT, ...) begins with this.
    //
    //   offset 0   char[4] Signature       (e.g. "MADT", "HPET", "MCFG")
    //   offset 4   u32     Length          (bytes incl. header)
    //   offset 8   u8      Revision
    //   offset 9   u8      Checksum        (whole table sums to 0)
    //   offset 10  char[6] OEMID
    //   offset 16  char[8] OEMTableID
    //   offset 24  u32     OEMRevision
    //   offset 28  u32     CreatorID
    //   offset 32  u32     CreatorRevision
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 36)]
    internal unsafe struct AcpiTableHeader
    {
        public uint Signature;     // 4 bytes interpreted as uint
        public uint Length;
        public byte Revision;
        public byte Checksum;
        // OEMID + OEMTableID + revisions are not used in this step;
        // accessed via byte* arithmetic when needed.

        // Verify that the entire table sums to zero (mod 256). Each ACPI
        // table is self-checksumming.
        public static bool ValidateChecksum(byte* table, uint length)
        {
            byte sum = 0;
            for (uint i = 0; i < length; i++) sum += table[i];
            return sum == 0;
        }

        // Common signatures (4-char ASCII, little-endian uint).
        // const so no static cctor — avoids ClassConstructorRunner trap.
        public const uint SigRSDT = 0x54445352u; // "RSDT"
        public const uint SigXSDT = 0x54445358u; // "XSDT"
        public const uint SigMADT = 0x43495041u; // "APIC" (MADT)
        public const uint SigHPET = 0x54455048u; // "HPET"
        public const uint SigMCFG = 0x4746434Du; // "MCFG"
        public const uint SigFADT = 0x50434146u; // "FACP" (FADT)
    }
}
