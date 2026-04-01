namespace OS.Kernel.Paging
{
    internal static unsafe class X64PageTable
    {
        public const ulong PageSize = 4096;

        private const ulong PresentMask = 1UL << 0;
        private const ulong WritableMask = 1UL << 1;
        private const ulong UserMask = 1UL << 2;
        private const ulong PageSizeMask = 1UL << 7;
        private const ulong AddressMask = 0x000FFFFFFFFFF000UL;

        private const ulong LeafFlagMask =
            (ulong)(PageFlags.Present | PageFlags.Writable | PageFlags.User |
            PageFlags.WriteThrough | PageFlags.CacheDisable | PageFlags.Global | PageFlags.NoExecute);

        private static ulong s_rootTable;
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

        public static bool Init(PagingRequirements requirements)
        {
            ResetState();

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

            s_rootTable = PopSparePage();
            return s_rootTable != 0;
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

            if (!TryGetLeafEntry(virtualAddress, out ulong* entry))
            {
                s_unmapFailures++;
                return false;
            }

            if ((*entry & PresentMask) == 0)
            {
                s_unmapFailures++;
                return false;
            }

            *entry = 0;
            if (s_mappedPages > 0)
                s_mappedPages--;

            return true;
        }

        public static bool TryQuery(ulong virtualAddress, out ulong physicalAddress, out PageFlags flags)
        {
            s_queryCalls++;
            physicalAddress = 0;
            flags = PageFlags.None;

            if (!TryGetLeafEntry(virtualAddress, out ulong* entry))
                return false;

            ulong value = *entry;
            if ((value & PresentMask) == 0)
                return false;

            physicalAddress = value & AddressMask;
            flags = (PageFlags)(value & LeafFlagMask);
            s_queryHits++;
            return true;
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
