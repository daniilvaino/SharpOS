namespace OS.Kernel
{
    internal static unsafe class PhysicalMemory
    {
        private const ulong PageSize = 4096;
        private const ulong MinAllocAddress = 0x00100000;

        private static MemoryRegion* s_regions;
        private static uint s_regionCount;
        private static uint s_nextRegionIndex;
        private static ulong s_cursor;
        private static ulong s_regionEnd;
        private static bool s_initialized;

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

        public static ulong AllocPage()
        {
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
