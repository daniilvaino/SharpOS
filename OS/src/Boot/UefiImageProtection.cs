using System.Runtime.InteropServices;

namespace OS.Boot
{
    /// <summary>
    /// Lifts the firmware's memory protection from the kernel image before
    /// anything patches it.
    /// </summary>
    /// <remarks>
    /// The patchers write machine code over method bodies inside the image —
    /// BootStackSwitch first, before the IDT and before the first line of the
    /// banner — and some shellcode runs from buffers inside the image's .data.
    /// Newer firmware maps a loaded image the way its sections ask: code
    /// read-only, data no-execute (EDK2 image protection, with the UEFI 2.10
    /// memory attribute protocol to change it). The first such write faults
    /// into the firmware's own exception handler, which prints to a debug port
    /// nobody sees and stops. An MSI B650 on AMI 1.P7 showed the screen cleared
    /// by TryMaximizeTextMode, a cursor, and nothing else; the same stick ran
    /// on a laptop whose older firmware maps images read-write.
    ///
    /// Firmware without the protocol either leaves images writable and
    /// executable or offers no way to change it; the line printed here says
    /// which case this machine is.
    /// </remarks>
    internal static unsafe class UefiImageProtection
    {
        private const ulong EfiMemoryRp = 0x0000000000002000;
        private const ulong EfiMemoryXp = 0x0000000000004000;
        private const ulong EfiMemoryRo = 0x0000000000020000;
        private const ulong PageSize = 4096;

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct MemoryAttributeProtocol
        {
            public readonly delegate* unmanaged<MemoryAttributeProtocol*, ulong, ulong, ulong*, ulong> GetMemoryAttributes;
            public readonly delegate* unmanaged<MemoryAttributeProtocol*, ulong, ulong, ulong, ulong> SetMemoryAttributes;
            public readonly delegate* unmanaged<MemoryAttributeProtocol*, ulong, ulong, ulong, ulong> ClearMemoryAttributes;
        }

        /// <summary>
        /// Clears read-only and no-execute on the whole image and reports what
        /// it found on one line of the firmware console.
        /// </summary>
        public static void MakeImageWritable(BootContext context)
        {
            EFI_SYSTEM_TABLE* st = context.SystemTable;
            if (st == null || st->BootServices == null)
                return;

            EFI_BOOT_SERVICES* bs = st->BootServices;
            if (bs->HandleProtocol == null || bs->LocateProtocol == null)
                return;

            EFI_GUID loadedImageGuid = LoadedImageGuid();
            EFI_LOADED_IMAGE_PROTOCOL* image = null;
            if (bs->HandleProtocol(context.ImageHandle, &loadedImageGuid, (void**)&image) != 0 || image == null)
            {
                Say(st, "[uefi] image protection: no loaded-image protocol");
                return;
            }

            ulong baseAddress = (ulong)image->ImageBase;
            ulong size = (image->ImageSize + PageSize - 1) & ~(PageSize - 1);

            EFI_GUID attributeGuid = MemoryAttributeGuid();
            MemoryAttributeProtocol* attributes = null;
            if (bs->LocateProtocol(&attributeGuid, null, (void**)&attributes) != 0 || attributes == null)
            {
                UefiConsole.Write(st, "[uefi] image protection: no memory attribute protocol, base=0x");
                WriteHex(st, baseAddress);
                UefiConsole.Write(st, "\n");

                // Older firmware protects images without offering the
                // protocol (EDK2 image protection predates it). Then the page
                // tables are the only place to look, and to change it.
                FixPageTables(st, bs, baseAddress, size, FirstWritableSectionRva((byte*)baseAddress));
                return;
            }

            // The first page past the headers is code (.text); the first page
            // of .data is the first writable section. Both are read before
            // anything changes so the line says what the firmware did.
            ulong codeAttributes = Query(attributes, baseAddress + PageSize);
            ulong dataAttributes = Query(attributes, baseAddress + FirstWritableSectionRva((byte*)baseAddress));

            ulong status = attributes->ClearMemoryAttributes(attributes, baseAddress, size, EfiMemoryRo | EfiMemoryXp | EfiMemoryRp);

            UefiConsole.Write(st, "[uefi] image protection: base=0x");
            WriteHex(st, baseAddress);
            UefiConsole.Write(st, " size=0x");
            WriteHex(st, size);
            UefiConsole.Write(st, " code=0x");
            WriteHex(st, codeAttributes);
            UefiConsole.Write(st, " data=0x");
            WriteHex(st, dataAttributes);
            UefiConsole.Write(st, " clear RO/XP/RP status=0x");
            WriteHex(st, status);
            UefiConsole.Write(st, " now code=0x");
            WriteHex(st, Query(attributes, baseAddress + PageSize));
            UefiConsole.Write(st, "\n");
        }

        private const ulong EntryPresent = 1UL << 0;
        private const ulong EntryWritable = 1UL << 1;
        private const ulong EntryLarge = 1UL << 7;
        private const ulong EntryNoExecute = 1UL << 63;
        private const ulong EntryAddress = 0x000FFFFFFFFFF000UL;
        private const ulong Cr0WriteProtect = 1UL << 16;
        private const ulong Cr4La57 = 1UL << 12;

        // Walks the firmware's page tables (identity-mapped before
        // ExitBootServices) for the image: prints the entries behind one code
        // page and one data page, then makes every entry on the way to the
        // image writable and executable. CR0.WP is off while it writes,
        // because firmware that protects images may protect its page tables
        // too. Directory entries are changed as well as leaves: RW and NX at
        // any level apply to everything below it.
        private static void FixPageTables(EFI_SYSTEM_TABLE* st, EFI_BOOT_SERVICES* bs, ulong baseAddress, ulong size, ulong dataRva)
        {
            byte* stubs = null;
            if (bs->AllocatePool(EFI_MEMORY_TYPE.EfiLoaderCode, 64, (void**)&stubs) != 0 || stubs == null)
            {
                Say(st, "[uefi] page tables: no memory for the control-register stubs");
                return;
            }

            // mov rax, cr0; ret | mov cr0, rcx; ret | mov rax, cr3; ret |
            // mov cr3, rcx; ret | mov rax, cr4; ret — eight bytes apart.
            stubs[0] = 0x0F; stubs[1] = 0x20; stubs[2] = 0xC0; stubs[3] = 0xC3;
            stubs[8] = 0x0F; stubs[9] = 0x22; stubs[10] = 0xC1; stubs[11] = 0xC3;
            stubs[16] = 0x0F; stubs[17] = 0x20; stubs[18] = 0xD8; stubs[19] = 0xC3;
            stubs[24] = 0x0F; stubs[25] = 0x22; stubs[26] = 0xD9; stubs[27] = 0xC3;
            stubs[32] = 0x0F; stubs[33] = 0x20; stubs[34] = 0xE0; stubs[35] = 0xC3;

            var readCr0 = (delegate* unmanaged<ulong>)(stubs + 0);
            var writeCr0 = (delegate* unmanaged<ulong, void>)(stubs + 8);
            var readCr3 = (delegate* unmanaged<ulong>)(stubs + 16);
            var writeCr3 = (delegate* unmanaged<ulong, void>)(stubs + 24);
            var readCr4 = (delegate* unmanaged<ulong>)(stubs + 32);

            if ((readCr4() & Cr4La57) != 0)
            {
                Say(st, "[uefi] page tables: 5-level paging, not walked");
                return;
            }

            ulong cr0 = readCr0();
            ulong cr3 = readCr3();

            UefiConsole.Write(st, "[uefi] page tables: cr0=0x");
            WriteHex(st, cr0);
            UefiConsole.Write(st, " cr3=0x");
            WriteHex(st, cr3);
            UefiConsole.Write(st, "\n");
            Describe(st, "code", cr3, baseAddress + PageSize);
            Describe(st, "data", cr3, baseAddress + dataRva);

            int changed = 0;
            writeCr0(cr0 & ~Cr0WriteProtect);
            for (ulong va = baseAddress; va < baseAddress + size;)
                va += Open(cr3, va, ref changed);
            writeCr3(cr3);                 // drop the stale translations
            writeCr0(cr0);

            UefiConsole.Write(st, "[uefi] page tables: entries opened=0x");
            WriteHex(st, (ulong)changed);
            UefiConsole.Write(st, "\n");
        }

        // One line: the entry at every level for this address.
        private static void Describe(EFI_SYSTEM_TABLE* st, string what, ulong cr3, ulong va)
        {
            UefiConsole.Write(st, "[uefi]   ");
            UefiConsole.Write(st, what);
            UefiConsole.Write(st, " 0x");
            WriteHex(st, va);

            ulong* table = (ulong*)(cr3 & EntryAddress);
            for (int level = 4; level >= 1; level--)
            {
                ulong entry = table[(va >> (12 + 9 * (level - 1))) & 511];
                UefiConsole.Write(st, level == 4 ? " L4=0x" : level == 3 ? " L3=0x" : level == 2 ? " L2=0x" : " L1=0x");
                WriteHex(st, entry);
                if ((entry & EntryPresent) == 0 || level == 1 || (level < 4 && (entry & EntryLarge) != 0))
                    break;
                table = (ulong*)(entry & EntryAddress);
            }
            UefiConsole.Write(st, "\n");
        }

        // Makes the entries on the way to va writable and executable; returns
        // how far the mapping that holds va reaches, to step to the next one.
        private static ulong Open(ulong cr3, ulong va, ref int changed)
        {
            ulong* table = (ulong*)(cr3 & EntryAddress);
            for (int level = 4; level >= 1; level--)
            {
                int shift = 12 + 9 * (level - 1);
                ulong* entry = &table[(va >> shift) & 511];
                ulong reach = 1UL << shift;

                if ((*entry & EntryPresent) == 0)
                    return reach - (va & (reach - 1));

                ulong opened = (*entry | EntryWritable) & ~EntryNoExecute;
                if (opened != *entry)
                {
                    *entry = opened;
                    changed++;
                }

                if (level == 1 || (level < 4 && (*entry & EntryLarge) != 0))
                    return reach - (va & (reach - 1));

                table = (ulong*)(*entry & EntryAddress);
            }
            return PageSize;
        }

        private static ulong Query(MemoryAttributeProtocol* attributes, ulong address)
        {
            ulong value = 0;
            ulong status = attributes->GetMemoryAttributes(attributes, address & ~(PageSize - 1), PageSize, &value);
            return status == 0 ? value : 0xFFFFFFFFFFFFFFFFUL;
        }

        // RVA of the first section the image asks to be writable, from the
        // PE headers mapped at the image base; one page past the headers if
        // there is none to find.
        private static ulong FirstWritableSectionRva(byte* image)
        {
            if (image[0] != (byte)'M' || image[1] != (byte)'Z')
                return PageSize;

            int pe = *(int*)(image + 0x3C);
            ushort sections = *(ushort*)(image + pe + 6);
            ushort optionalSize = *(ushort*)(image + pe + 20);
            byte* section = image + pe + 24 + optionalSize;

            for (int i = 0; i < sections; i++, section += 40)
            {
                uint characteristics = *(uint*)(section + 36);
                if ((characteristics & 0x80000000u) != 0)
                    return *(uint*)(section + 12);
            }

            return PageSize;
        }

        private static void Say(EFI_SYSTEM_TABLE* st, string line)
        {
            UefiConsole.Write(st, line);
            UefiConsole.Write(st, "\n");
        }

        // No heap yet: digits straight to the firmware console.
        private static void WriteHex(EFI_SYSTEM_TABLE* st, ulong value)
        {
            bool started = false;
            for (int shift = 60; shift >= 0; shift -= 4)
            {
                int digit = (int)((value >> shift) & 0xF);
                if (digit == 0 && !started && shift != 0)
                    continue;
                started = true;
                UefiConsole.WriteChar(st, (char)(digit < 10 ? '0' + digit : 'A' + digit - 10));
            }
        }

        private static EFI_GUID LoadedImageGuid()
        {
            EFI_GUID guid = default;
            guid.Data1 = 0x5B1B31A1; guid.Data2 = 0x9562; guid.Data3 = 0x11D2;
            guid.Data4_0 = 0x8E; guid.Data4_1 = 0x3F; guid.Data4_2 = 0x00;
            guid.Data4_3 = 0xA0; guid.Data4_4 = 0xC9; guid.Data4_5 = 0x69;
            guid.Data4_6 = 0x72; guid.Data4_7 = 0x3B;
            return guid;
        }

        // EFI_MEMORY_ATTRIBUTE_PROTOCOL, UEFI 2.10:
        // {f4560cf6-40ec-4b4a-a192-bf1d57d0b189}
        private static EFI_GUID MemoryAttributeGuid()
        {
            EFI_GUID guid = default;
            guid.Data1 = 0xF4560CF6; guid.Data2 = 0x40EC; guid.Data3 = 0x4B4A;
            guid.Data4_0 = 0xA1; guid.Data4_1 = 0x92; guid.Data4_2 = 0xBF;
            guid.Data4_3 = 0x1D; guid.Data4_4 = 0x57; guid.Data4_5 = 0xD0;
            guid.Data4_6 = 0xB1; guid.Data4_7 = 0x89;
            return guid;
        }
    }
}
