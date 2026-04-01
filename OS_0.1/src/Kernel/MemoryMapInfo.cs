namespace OS.Kernel
{
    internal unsafe struct MemoryMapInfo
    {
        public MemoryRegion* Regions;
        public uint RegionCount;
    }
}
