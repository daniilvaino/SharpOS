namespace OS.Kernel
{
    internal static unsafe class MemoryDiagnostics
    {
        public static ulong CountUsablePages(MemoryMapInfo map)
        {
            if (map.Regions == null || map.RegionCount == 0)
                return 0;

            ulong total = 0;
            for (uint i = 0; i < map.RegionCount; i++)
            {
                if (map.Regions[i].Type == MemoryRegionType.Usable)
                    total += map.Regions[i].PageCount;
            }

            return total;
        }
    }
}
