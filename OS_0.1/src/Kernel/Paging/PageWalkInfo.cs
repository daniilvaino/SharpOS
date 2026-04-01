namespace OS.Kernel.Paging
{
    internal struct PageWalkInfo
    {
        public ulong VirtualAddress;
        public uint Pml4Index;
        public uint PdptIndex;
        public uint PdIndex;
        public uint PtIndex;

        public ulong Pml4Entry;
        public ulong PdptEntry;
        public ulong PdEntry;
        public ulong PtEntry;
        public ulong LeafEntry;

        public bool Pml4Present;
        public bool PdptPresent;
        public bool PdPresent;
        public bool PtPresent;
        public bool LeafPresent;
    }
}
