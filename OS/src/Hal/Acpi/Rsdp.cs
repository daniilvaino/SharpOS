using System.Runtime.InteropServices;

namespace OS.Hal.Acpi
{
    // Root System Description Pointer — entry into the ACPI table tree.
    // UEFI hands us this via EFI_CONFIGURATION_TABLE (one of two GUIDs:
    // ACPI 1.0 or ACPI 2.0+). We require ACPI 2.0+ — XSDT (64-bit pointers)
    // is the only sane path on x64.
    //
    // Layout:
    //   offset 0   char[8]  Signature ("RSD PTR ")
    //   offset 8   u8       Checksum   (first 20 bytes sum to 0)
    //   offset 9   char[6]  OEMID
    //   offset 15  u8       Revision   (0=ACPI 1.0, 2+=2.0+)
    //   offset 16  u32      RsdtAddress  (32-bit physical, ACPI 1.0 path)
    //   --- end of ACPI 1.0 RSDP, 20 bytes ---
    //   offset 20  u32      Length     (whole RSDP incl. 2.0 extension)
    //   offset 24  u64      XsdtAddress (64-bit physical, preferred)
    //   offset 32  u8       ExtendedChecksum (whole RSDP sums to 0)
    //   offset 33  u8[3]    Reserved
    //   --- end of ACPI 2.0 RSDP, 36 bytes ---
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 36)]
    internal unsafe struct Rsdp
    {
        public fixed byte Signature[8];
        public byte Checksum;
        public fixed byte OemId[6];
        public byte Revision;
        public uint RsdtAddress;
        public uint Length;
        public ulong XsdtAddress;
        public byte ExtendedChecksum;
        public fixed byte Reserved[3];

        // Validate the "RSD PTR " signature (note trailing space).
        public bool HasValidSignature()
        {
            fixed (byte* sig = Signature)
            {
                return sig[0] == (byte)'R' && sig[1] == (byte)'S' &&
                       sig[2] == (byte)'D' && sig[3] == (byte)' ' &&
                       sig[4] == (byte)'P' && sig[5] == (byte)'T' &&
                       sig[6] == (byte)'R' && sig[7] == (byte)' ';
            }
        }

        // Sum first 20 bytes (ACPI 1.0 RSDP) — must be zero.
        public bool HasValidV1Checksum()
        {
            fixed (byte* p = &Signature[0])
            {
                byte sum = 0;
                for (int i = 0; i < 20; i++) sum += p[i];
                return sum == 0;
            }
        }

        // Sum entire `Length` bytes (ACPI 2.0+ extended RSDP) — must be zero.
        public bool HasValidV2Checksum()
        {
            if (Length < 36 || Length > 4096) return false;
            fixed (byte* p = &Signature[0])
            {
                byte sum = 0;
                for (uint i = 0; i < Length; i++) sum += p[i];
                return sum == 0;
            }
        }
    }
}
