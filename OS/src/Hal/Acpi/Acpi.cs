using OS.Boot;

namespace OS.Hal.Acpi
{
    // ACPI table tree orchestrator. Boot path:
    //   1. Walk EFI_SYSTEM_TABLE.ConfigurationTable for ACPI 2.0 GUID.
    //   2. Validate RSDP (signature + checksum).
    //   3. Walk XSDT — array of 64-bit pointers to other ACPI tables.
    //   4. For each entry: read SDT header signature, classify.
    //   5. Cache pointers to MADT / HPET / MCFG / FADT for later consumers
    //      (timers/scheduler/PCI drivers).
    //
    // We deliberately don't parse table contents here — just locate them.
    // Per-table parsers (MadtParser, HpetParser, McfgParser) live in their
    // own files and pull the cached pointer when they're invoked.
    //
    // ACPI 1.0 (RSDT, 32-bit pointers) is rejected — modern x64 firmware
    // always provides 2.0+. Simplifies code.
    internal static unsafe class Acpi
    {
        // ACPI 2.0 RSDP GUID per UEFI spec: {8868E871-E4F1-11D3-BC22-0080C73C8881}
        // Compared field-by-field with hardcoded constants to avoid a static
        // initializer (would force a cctor → ClassConstructorRunner trap).
        private static bool IsAcpi20Guid(EFI_GUID* g)
        {
            return g->Data1 == 0x8868E871
                && g->Data2 == 0xE4F1
                && g->Data3 == 0x11D3
                && g->Data4_0 == 0xBC && g->Data4_1 == 0x22
                && g->Data4_2 == 0x00 && g->Data4_3 == 0x80 && g->Data4_4 == 0xC7
                && g->Data4_5 == 0x3C && g->Data4_6 == 0x88 && g->Data4_7 == 0x81;
        }

        private static bool s_initialized;
        private static Rsdp* s_rsdp;
        private static byte* s_xsdt;
        private static int s_xsdtEntryCount;

        // Cached SDT pointers. null if table not present.
        public static byte* MadtPtr;
        public static byte* HpetPtr;
        public static byte* McfgPtr;
        public static byte* FadtPtr;

        public static bool IsInitialized => s_initialized;
        public static int XsdtEntryCount => s_xsdtEntryCount;

        public static bool Init(EFI_SYSTEM_TABLE* systemTable)
        {
            if (s_initialized) return true;
            if (systemTable == null) return false;

            // 1. Find ACPI 2.0 RSDP via configuration table.
            Rsdp* rsdp = FindRsdp(systemTable);
            if (rsdp == null) return false;

            if (!rsdp->HasValidSignature()) return false;
            if (rsdp->Revision < 2) return false;          // ACPI 1.0 only — reject
            if (!rsdp->HasValidV1Checksum()) return false;
            if (!rsdp->HasValidV2Checksum()) return false;

            ulong xsdtPhys = rsdp->XsdtAddress;
            if (xsdtPhys == 0) return false;
            byte* xsdt = (byte*)xsdtPhys;

            // 2. Validate XSDT header.
            AcpiTableHeader* xsdtHdr = (AcpiTableHeader*)xsdt;
            if (xsdtHdr->Signature != AcpiTableHeader.SigXSDT) return false;
            if (xsdtHdr->Length < 36 || xsdtHdr->Length > 65536) return false;
            if (!AcpiTableHeader.ValidateChecksum(xsdt, xsdtHdr->Length)) return false;

            // 3. Walk XSDT entries (64-bit physical pointers, after 36-byte header).
            int entryBytes = (int)xsdtHdr->Length - 36;
            int entryCount = entryBytes / 8;

            byte* entries = xsdt + 36;
            for (int i = 0; i < entryCount; i++)
            {
                ulong tablePhys = ReadUnalignedU64(entries + i * 8);
                if (tablePhys == 0) continue;
                byte* table = (byte*)tablePhys;
                AcpiTableHeader* hdr = (AcpiTableHeader*)table;
                if (hdr->Length < 36 || hdr->Length > 4 * 1024 * 1024) continue;

                // Validate per-table checksum before trusting pointers.
                if (!AcpiTableHeader.ValidateChecksum(table, hdr->Length)) continue;

                if      (hdr->Signature == AcpiTableHeader.SigMADT) MadtPtr = table;
                else if (hdr->Signature == AcpiTableHeader.SigHPET) HpetPtr = table;
                else if (hdr->Signature == AcpiTableHeader.SigMCFG) McfgPtr = table;
                else if (hdr->Signature == AcpiTableHeader.SigFADT) FadtPtr = table;
            }

            s_rsdp = rsdp;
            s_xsdt = xsdt;
            s_xsdtEntryCount = entryCount;
            s_initialized = true;
            return true;
        }

        // Walk EFI_CONFIGURATION_TABLE entries looking for ACPI 2.0 GUID.
        private static Rsdp* FindRsdp(EFI_SYSTEM_TABLE* systemTable)
        {
            if (systemTable->ConfigurationTable == null) return null;
            ulong count = systemTable->NumberOfTableEntries;
            EFI_CONFIGURATION_TABLE* table = systemTable->ConfigurationTable;
            for (ulong i = 0; i < count; i++)
            {
                EFI_GUID g = table[i].VendorGuid;
                if (IsAcpi20Guid(&g))
                    return (Rsdp*)table[i].VendorTable;
            }
            return null;
        }

        // XSDT entries are 64-bit pointers but the table itself is byte-packed
        // (no alignment guarantees beyond 4-byte). Read explicitly.
        private static ulong ReadUnalignedU64(byte* p)
        {
            return (ulong)p[0] | ((ulong)p[1] << 8) | ((ulong)p[2] << 16) | ((ulong)p[3] << 24)
                 | ((ulong)p[4] << 32) | ((ulong)p[5] << 40) | ((ulong)p[6] << 48) | ((ulong)p[7] << 56);
        }
    }
}
