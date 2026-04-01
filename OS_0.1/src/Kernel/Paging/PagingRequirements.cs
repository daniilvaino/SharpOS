namespace OS.Kernel.Paging
{
    internal struct PagingRequirements
    {
        public ulong PageSize;
        public ulong DirectMapBase;
        public uint InitialPageTablePages;
    }
}
