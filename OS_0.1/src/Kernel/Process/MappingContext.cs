using OS.Kernel.Paging;

namespace OS.Kernel.Process
{
    internal unsafe struct MappingPageEntry
    {
        public ulong VirtualAddress;
        public ulong PhysicalAddress;
        public PageFlags Flags;
    }

    internal unsafe struct MappingSnapshot
    {
        public MappingPageEntry* Entries;
        public uint Count;
    }

    internal unsafe struct MappingContext
    {
        public MappingSnapshot ImageSnapshot;
        public MappingSnapshot StackSnapshot;
    }
}
