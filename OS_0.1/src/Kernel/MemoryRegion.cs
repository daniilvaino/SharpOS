namespace OS.Kernel
{
    internal enum MemoryRegionType : uint
    {
        Unknown = 0,
        Usable = 1,
        Reserved = 2,
        Acpi = 3,
        Mmio = 4,
        BootServices = 5,
        RuntimeServices = 6,
        Loader = 7,
    }

    internal struct MemoryRegion
    {
        public ulong PhysicalStart;
        public ulong PageCount;
        public MemoryRegionType Type;
    }
}
