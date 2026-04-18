namespace OS.Kernel.Paging
{
    internal struct PagingRequirements
    {
        public ulong PageSize;
        public ulong DirectMapBase;
        public uint InitialPageTablePages;

        public void Normalize()
        {
            if (PageSize == 0)
                PageSize = X64PageTable.PageSize;

            if (InitialPageTablePages == 0)
                InitialPageTablePages = 1;
        }

        public bool IsValid()
        {
            if (PageSize != X64PageTable.PageSize)
                return false;

            if (InitialPageTablePages == 0)
                return false;

            if ((DirectMapBase & (X64PageTable.PageSize - 1)) != 0)
                return false;

            return true;
        }
    }
}
