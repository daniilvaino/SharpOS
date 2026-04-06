namespace OS.Kernel.Paging
{
    internal static unsafe class X64PageTable
    {
        public const ulong PageSize = 4096;
        private const ulong LargePage2MBSize = 2UL * 1024UL * 1024UL;
        private const ulong LargePage1GBSize = 1024UL * 1024UL * 1024UL;

        private const ulong PresentMask = 1UL << 0;
        private const ulong WritableMask = 1UL << 1;
        private const ulong UserMask = 1UL << 2;
        private const ulong PageSizeMask = 1UL << 7;
        private const ulong AddressMask = 0x000FFFFFFFFFF000UL;
        private const ulong AddressMask2MB = 0x000FFFFFFFE00000UL;
        private const ulong AddressMask1GB = 0x000FFFFFC0000000UL;

        private const ulong LeafFlagMask =
            (ulong)(PageFlags.Present | PageFlags.Writable | PageFlags.User |
            PageFlags.WriteThrough | PageFlags.CacheDisable | PageFlags.Global | PageFlags.NoExecute);

        private static ulong s_rootTable;
        private static ulong s_kernelRootTable;
        private static ulong s_kernelCr3;
        private static ulong s_pagerCr3;
        private static ulong s_sparePageList;
        private static uint s_sparePageCount;

        private static uint s_tablePages;
        private static uint s_mappedPages;
        private static uint s_mapCalls;
        private static uint s_mapFailures;
        private static uint s_queryCalls;
        private static uint s_queryHits;
        private static uint s_unmapCalls;
        private static uint s_unmapFailures;

        private enum ResolvedEntryLevel : byte
        {
            Pt = 1,
            PdLarge = 2,
            PdptLarge = 3,
        }

        public static ulong RootTablePhysical => s_rootTable;

        public static ulong KernelRootTablePhysical => s_kernelRootTable;

        public static void SetExecBuffer(void* buffer, uint size)
        {
            Cr3Accessor.SetExecBuffer(buffer, size);
        }

        public static void SetJumpStubBuffer(void* buffer, uint size)
        {
            global::OS.Kernel.Exec.JumpStub.SetExecBuffer(buffer, size);
        }

        public static bool Init(PagingRequirements requirements)
        {
            ResetState();

            if (!Cr3Accessor.TryInitialize())
                return false;

            if (!Cr3Accessor.TryRead(out ulong currentCr3))
                return false;

            s_kernelCr3 = currentCr3;
            s_kernelRootTable = currentCr3 & AddressMask;
            if (s_kernelRootTable == 0)
                return false;

            if (!TryCloneTableRecursive(s_kernelRootTable, 4, out s_rootTable))
                return false;

            s_pagerCr3 = (s_rootTable & AddressMask) | (s_kernelCr3 & ~AddressMask);

            uint reservePages = requirements.InitialPageTablePages;
            if (reservePages == 0)
                reservePages = 1;

            for (uint i = 0; i < reservePages; i++)
            {
                ulong page = global::OS.Kernel.PhysicalMemory.AllocPage();
                if (page == 0)
                    return false;

                ZeroPage(page);
                PushSparePage(page);
                s_tablePages++;
            }

            return s_rootTable != 0;
        }

        public static bool TryActivatePagerCr3()
        {
            if (s_pagerCr3 == 0)
                return false;

            return Cr3Accessor.TryWrite(s_pagerCr3);
        }

        public static bool TryActivateKernelCr3()
        {
            if (s_kernelCr3 == 0)
                return false;

            return Cr3Accessor.TryWrite(s_kernelCr3);
        }

        public static bool IsPagerCr3Active()
        {
            if (!Cr3Accessor.TryRead(out ulong activeCr3))
                return false;

            return (activeCr3 & AddressMask) == (s_rootTable & AddressMask);
        }

        public static bool TryGetActiveCr3(out ulong activeCr3)
        {
            return Cr3Accessor.TryRead(out activeCr3);
        }

        public static bool TryGetPagerCr3(out ulong pagerCr3)
        {
            pagerCr3 = s_pagerCr3;
            return pagerCr3 != 0;
        }

        public static bool TryGetKernelCr3(out ulong kernelCr3)
        {
            kernelCr3 = s_kernelCr3;
            return kernelCr3 != 0;
        }

        public static bool Map(ulong virtualAddress, ulong physicalAddress, PageFlags flags)
        {
            s_mapCalls++;

            if (s_rootTable == 0)
            {
                s_mapFailures++;
                return false;
            }

            bool userPage = (flags & PageFlags.User) == PageFlags.User;

            ulong* pml4 = (ulong*)s_rootTable;
            if (!GetOrCreateNextTable(pml4, Pml4Index(virtualAddress), userPage, out ulong* pdpt))
            {
                s_mapFailures++;
                return false;
            }

            if (!GetOrCreateNextTable(pdpt, PdptIndex(virtualAddress), userPage, out ulong* pd))
            {
                s_mapFailures++;
                return false;
            }

            if (!GetOrCreateNextTable(pd, PdIndex(virtualAddress), userPage, out ulong* pt))
            {
                s_mapFailures++;
                return false;
            }

            uint ptIndex = PtIndex(virtualAddress);
            if ((pt[ptIndex] & PresentMask) != 0)
            {
                s_mapFailures++;
                return false;
            }

            pt[ptIndex] = CreateLeafEntry(physicalAddress, flags);
            s_mappedPages++;
            return true;
        }

        public static bool Unmap(ulong virtualAddress)
        {
            s_unmapCalls++;

            if (!TryResolveMappedEntry(virtualAddress, out ulong* entry, out ulong value, out ResolvedEntryLevel level))
            {
                s_unmapFailures++;
                return false;
            }

            if ((value & PresentMask) == 0)
            {
                s_unmapFailures++;
                return false;
            }

            *entry = 0;
            if (level == ResolvedEntryLevel.Pt && s_mappedPages > 0)
                s_mappedPages--;

            return true;
        }

        public static bool TryQuery(ulong virtualAddress, out ulong physicalAddress, out PageFlags flags)
        {
            s_queryCalls++;
            bool found = TryQueryForRoot(s_rootTable, virtualAddress, out physicalAddress, out flags);
            if (found)
                s_queryHits++;

            return found;
        }

        public static bool TryQueryKernel(ulong virtualAddress, out ulong physicalAddress, out PageFlags flags)
        {
            return TryQueryForRoot(s_kernelRootTable, virtualAddress, out physicalAddress, out flags);
        }

        public static bool TryGetWalkInfo(ulong virtualAddress, out PageWalkInfo walkInfo)
        {
            walkInfo = default;
            walkInfo.VirtualAddress = virtualAddress;
            walkInfo.Pml4Index = Pml4Index(virtualAddress);
            walkInfo.PdptIndex = PdptIndex(virtualAddress);
            walkInfo.PdIndex = PdIndex(virtualAddress);
            walkInfo.PtIndex = PtIndex(virtualAddress);

            if (s_rootTable == 0)
                return false;

            ulong* pml4 = (ulong*)s_rootTable;
            walkInfo.Pml4Entry = pml4[walkInfo.Pml4Index];
            walkInfo.Pml4Present = IsPresent(walkInfo.Pml4Entry);
            if (!walkInfo.Pml4Present || IsLargePage(walkInfo.Pml4Entry))
                return true;

            ulong* pdpt = (ulong*)(walkInfo.Pml4Entry & AddressMask);
            walkInfo.PdptEntry = pdpt[walkInfo.PdptIndex];
            walkInfo.PdptPresent = IsPresent(walkInfo.PdptEntry);
            if (!walkInfo.PdptPresent || IsLargePage(walkInfo.PdptEntry))
                return true;

            ulong* pd = (ulong*)(walkInfo.PdptEntry & AddressMask);
            walkInfo.PdEntry = pd[walkInfo.PdIndex];
            walkInfo.PdPresent = IsPresent(walkInfo.PdEntry);
            if (!walkInfo.PdPresent || IsLargePage(walkInfo.PdEntry))
                return true;

            ulong* pt = (ulong*)(walkInfo.PdEntry & AddressMask);
            walkInfo.PtEntry = pt[walkInfo.PtIndex];
            walkInfo.PtPresent = IsPresent(walkInfo.PtEntry);
            walkInfo.LeafEntry = walkInfo.PtEntry;
            walkInfo.LeafPresent = walkInfo.PtPresent;
            return true;
        }

        public static void GetSummary(out PagingSummary summary)
        {
            summary = default;
            summary.RootTablePhysical = s_rootTable;
            summary.KernelRootTablePhysical = s_kernelRootTable;
            summary.KernelCr3 = s_kernelCr3;
            summary.PagerCr3 = s_pagerCr3;
            if (Cr3Accessor.TryRead(out ulong activeCr3))
            {
                summary.ActiveCr3 = activeCr3;
                summary.IsPagerRootActive = (activeCr3 & AddressMask) == (s_rootTable & AddressMask);
            }

            summary.TablePages = s_tablePages;
            summary.SpareTablePages = s_sparePageCount;
            summary.MappedPages = s_mappedPages;
            summary.MapCalls = s_mapCalls;
            summary.MapFailures = s_mapFailures;
            summary.QueryCalls = s_queryCalls;
            summary.QueryHits = s_queryHits;
            summary.UnmapCalls = s_unmapCalls;
            summary.UnmapFailures = s_unmapFailures;
        }

        private static bool TryGetLeafEntry(ulong virtualAddress, out ulong* entry)
        {
            entry = null;
            if (s_rootTable == 0)
                return false;

            ulong* pml4 = (ulong*)s_rootTable;
            if (!TryGetNextTable(pml4, Pml4Index(virtualAddress), out ulong* pdpt))
                return false;

            if (!TryGetNextTable(pdpt, PdptIndex(virtualAddress), out ulong* pd))
                return false;

            if (!TryGetNextTable(pd, PdIndex(virtualAddress), out ulong* pt))
                return false;

            entry = &pt[PtIndex(virtualAddress)];
            return true;
        }

        private static bool TryResolveMappedEntry(
            ulong virtualAddress,
            out ulong* entry,
            out ulong value,
            out ResolvedEntryLevel level)
        {
            return TryResolveMappedEntryForRoot(s_rootTable, virtualAddress, out entry, out value, out level);
        }

        private static bool TryResolveMappedEntryForRoot(
            ulong rootTable,
            ulong virtualAddress,
            out ulong* entry,
            out ulong value,
            out ResolvedEntryLevel level)
        {
            entry = null;
            value = 0;
            level = 0;

            if (rootTable == 0)
                return false;

            ulong* pml4 = (ulong*)rootTable;
            uint pml4Index = Pml4Index(virtualAddress);
            ulong pml4Entry = pml4[pml4Index];
            if (!IsPresent(pml4Entry) || IsLargePage(pml4Entry))
                return false;

            ulong* pdpt = (ulong*)(pml4Entry & AddressMask);
            uint pdptIndex = PdptIndex(virtualAddress);
            ulong pdptEntry = pdpt[pdptIndex];
            if (!IsPresent(pdptEntry))
                return false;

            if (IsLargePage(pdptEntry))
            {
                entry = &pdpt[pdptIndex];
                value = pdptEntry;
                level = ResolvedEntryLevel.PdptLarge;
                return true;
            }

            ulong* pd = (ulong*)(pdptEntry & AddressMask);
            uint pdIndex = PdIndex(virtualAddress);
            ulong pdEntry = pd[pdIndex];
            if (!IsPresent(pdEntry))
                return false;

            if (IsLargePage(pdEntry))
            {
                entry = &pd[pdIndex];
                value = pdEntry;
                level = ResolvedEntryLevel.PdLarge;
                return true;
            }

            ulong* pt = (ulong*)(pdEntry & AddressMask);
            uint ptIndex = PtIndex(virtualAddress);
            ulong ptEntry = pt[ptIndex];

            entry = &pt[ptIndex];
            value = ptEntry;
            level = ResolvedEntryLevel.Pt;
            return true;
        }

        private static bool TryQueryForRoot(ulong rootTable, ulong virtualAddress, out ulong physicalAddress, out PageFlags flags)
        {
            physicalAddress = 0;
            flags = PageFlags.None;

            if (!TryResolveMappedEntryForRoot(rootTable, virtualAddress, out ulong* entry, out ulong value, out ResolvedEntryLevel level))
                return false;

            if ((value & PresentMask) == 0)
                return false;

            ulong basePhysical;
            ulong offset;
            if (level == ResolvedEntryLevel.Pt)
            {
                basePhysical = value & AddressMask;
                offset = virtualAddress & (PageSize - 1);
            }
            else if (level == ResolvedEntryLevel.PdLarge)
            {
                basePhysical = value & AddressMask2MB;
                offset = virtualAddress & (LargePage2MBSize - 1);
            }
            else
            {
                basePhysical = value & AddressMask1GB;
                offset = virtualAddress & (LargePage1GBSize - 1);
            }

            if (basePhysical > 0xFFFFFFFFFFFFFFFFUL - offset)
                return false;

            physicalAddress = basePhysical + offset;
            flags = (PageFlags)(value & LeafFlagMask);
            return true;
        }

        private static bool GetOrCreateNextTable(ulong* table, uint index, bool userPage, out ulong* nextTable)
        {
            ulong entry = table[index];

            if ((entry & PresentMask) == 0)
            {
                ulong page = AllocateTablePage();
                if (page == 0)
                {
                    nextTable = null;
                    return false;
                }

                ulong newEntry = (page & AddressMask) | PresentMask | WritableMask;
                if (userPage)
                    newEntry |= UserMask;

                table[index] = newEntry;
                nextTable = (ulong*)page;
                return true;
            }

            if ((entry & PageSizeMask) != 0)
            {
                nextTable = null;
                return false;
            }

            if (userPage && (entry & UserMask) == 0)
            {
                entry |= UserMask;
                table[index] = entry;
            }

            nextTable = (ulong*)(entry & AddressMask);
            return true;
        }

        private static bool TryGetNextTable(ulong* table, uint index, out ulong* nextTable)
        {
            nextTable = null;
            ulong entry = table[index];

            if ((entry & PresentMask) == 0)
                return false;

            if ((entry & PageSizeMask) != 0)
                return false;

            nextTable = (ulong*)(entry & AddressMask);
            return true;
        }

        private static ulong CreateLeafEntry(ulong physicalAddress, PageFlags flags)
        {
            ulong normalizedFlags = ((ulong)(flags | PageFlags.Present)) & LeafFlagMask;
            return (physicalAddress & AddressMask) | normalizedFlags;
        }

        private static bool IsPresent(ulong entry)
        {
            return (entry & PresentMask) != 0;
        }

        private static bool IsLargePage(ulong entry)
        {
            return (entry & PageSizeMask) != 0;
        }

        private static ulong AllocateTablePage()
        {
            ulong page = PopSparePage();
            if (page != 0)
                return page;

            page = global::OS.Kernel.PhysicalMemory.AllocPage();
            if (page == 0)
                return 0;

            ZeroPage(page);
            s_tablePages++;
            return page;
        }

        private static bool TryCloneTableRecursive(ulong sourceTablePage, uint level, out ulong clonedTablePage)
        {
            clonedTablePage = 0;
            if (sourceTablePage == 0 || level == 0)
                return false;

            ulong newTablePage = global::OS.Kernel.PhysicalMemory.AllocPage();
            if (newTablePage == 0)
                return false;

            s_tablePages++;
            OS.Kernel.Util.Memory.MemCopy((void*)newTablePage, (void*)sourceTablePage, (uint)PageSize);

            if (level == 1)
            {
                clonedTablePage = newTablePage;
                return true;
            }

            ulong* entries = (ulong*)newTablePage;
            for (uint i = 0; i < 512; i++)
            {
                ulong entry = entries[i];
                if ((entry & PresentMask) == 0 || (entry & PageSizeMask) != 0)
                    continue;

                ulong childSourceTable = entry & AddressMask;
                if (childSourceTable == 0)
                    return false;

                if (!TryCloneTableRecursive(childSourceTable, level - 1, out ulong childClonedTable))
                    return false;

                entries[i] = (entry & ~AddressMask) | childClonedTable;
            }

            clonedTablePage = newTablePage;
            return true;
        }

        private static void ZeroPage(ulong pageAddress)
        {
            ulong* page = (ulong*)pageAddress;
            for (uint i = 0; i < 512; i++)
                page[i] = 0;
        }

        private static void PushSparePage(ulong page)
        {
            *((ulong*)page) = s_sparePageList;
            s_sparePageList = page;
            s_sparePageCount++;
        }

        private static ulong PopSparePage()
        {
            if (s_sparePageList == 0)
                return 0;

            ulong page = s_sparePageList;
            s_sparePageList = *((ulong*)page);
            s_sparePageCount--;
            ZeroPage(page);
            return page;
        }

        private static uint Pml4Index(ulong virtualAddress) => (uint)((virtualAddress >> 39) & 0x1FFUL);

        private static uint PdptIndex(ulong virtualAddress) => (uint)((virtualAddress >> 30) & 0x1FFUL);

        private static uint PdIndex(ulong virtualAddress) => (uint)((virtualAddress >> 21) & 0x1FFUL);

        private static uint PtIndex(ulong virtualAddress) => (uint)((virtualAddress >> 12) & 0x1FFUL);

        private static void ResetState()
        {
            s_rootTable = 0;
            s_kernelRootTable = 0;
            s_kernelCr3 = 0;
            s_pagerCr3 = 0;
            s_sparePageList = 0;
            s_sparePageCount = 0;
            s_tablePages = 0;
            s_mappedPages = 0;
            s_mapCalls = 0;
            s_mapFailures = 0;
            s_queryCalls = 0;
            s_queryHits = 0;
            s_unmapCalls = 0;
            s_unmapFailures = 0;
        }
    }
}
