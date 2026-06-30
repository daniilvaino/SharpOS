namespace OS.Kernel
{
    internal static unsafe class PhysicalMemory
    {
        private const ulong PageSize = 4096;
        private const ulong MinAllocAddress = 0x00100000;
        // Freelist capacity = 256K entries × 8 bytes = 2 MB. Covers 1 GB
        // of freed pages — more than any realistic single-session churn.
        // Lazily allocated on first FreePage call (skip cost if nothing
        // ever frees, e.g. minimal kernel-only smoke tests).
        private const int FreeListCapacity = 256 * 1024;

        private static MemoryRegion* s_regions;
        private static uint s_regionCount;
        private static uint s_nextRegionIndex;
        private static ulong s_cursor;
        private static ulong s_regionEnd;
        private static bool s_initialized;

        // Page freelist — single-page allocations only (matches AllocPage
        // callers: VirtualMemory.Commit and TryDemandCommit both loop one
        // page at a time). Multi-page contiguous frees (AllocPages count>1)
        // are NOT freed here; the caller must decompose them, otherwise the
        // pages just leak back to the bump cursor's never-revisit zone.
        private static ulong[]? s_freeList;
        private static int s_freeListTop;     // index of NEXT free slot (== count)
        private static ulong s_freeTotal;
        private static ulong s_reuseTotal;

        public static void Init(MemoryMapInfo map)
        {
            s_regions = map.Regions;
            s_regionCount = map.RegionCount;
            s_nextRegionIndex = 0;
            s_cursor = 0;
            s_regionEnd = 0;
            s_initialized = true;
            MoveToNextUsableRegion();
        }

        // Push a 4K page back onto the freelist. Lazily initializes storage.
        // Caller MUST ensure the page is no longer mapped anywhere (no VA
        // points at it) and not currently in any other live structure.
        public static void FreePage(ulong pa)
        {
            if (pa == 0) return;
            if (s_freeList == null) s_freeList = new ulong[FreeListCapacity];
            if (s_freeListTop >= FreeListCapacity) return;   // full — leak
            s_freeList[s_freeListTop++] = pa & ~(PageSize - 1);
            s_freeTotal++;
        }

        public static ulong AllocPage()
        {
            // Reuse from freelist when possible — keeps long PS sessions
            // from racing the bump cursor past the end of usable RAM.
            if (s_freeListTop > 0 && s_freeList != null)
            {
                ulong pa = s_freeList[--s_freeListTop];
                s_reuseTotal++;
                return pa;
            }
            return AllocPages(1);
        }

        public static ulong AllocPages(uint count)
        {
            if (!s_initialized || count == 0)
                return 0;

            ulong bytes = (ulong)count * PageSize;

            while (true)
            {
                if (s_cursor == 0 || s_cursor + bytes > s_regionEnd)
                {
                    if (!MoveToNextUsableRegion())
                        return 0;
                }

                ulong address = AlignUp(s_cursor, PageSize);
                if (address + bytes > s_regionEnd)
                {
                    s_cursor = 0;
                    s_regionEnd = 0;
                    continue;
                }

                s_cursor = address + bytes;
                return address;
            }
        }

        private static bool MoveToNextUsableRegion()
        {
            if (s_regions == null || s_regionCount == 0)
                return false;

            while (s_nextRegionIndex < s_regionCount)
            {
                MemoryRegion* region = &s_regions[s_nextRegionIndex];
                s_nextRegionIndex++;

                if (region->Type != MemoryRegionType.Usable || region->PageCount == 0)
                    continue;

                ulong start = region->PhysicalStart;
                ulong end = start + region->PageCount * PageSize;
                start = AlignUp(start, PageSize);

                if (start < MinAllocAddress)
                    start = MinAllocAddress;

                if (start >= end)
                    continue;

                s_cursor = start;
                s_regionEnd = end;
                return true;
            }

            return false;
        }

        private static ulong AlignUp(ulong value, ulong alignment)
        {
            ulong mask = alignment - 1;
            return (value + mask) & ~mask;
        }
    }
}
