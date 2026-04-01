namespace OS.Kernel.Paging
{
    internal enum PageFlags : ulong
    {
        None = 0,
        Present = 1UL << 0,
        Writable = 1UL << 1,
        User = 1UL << 2,
        WriteThrough = 1UL << 3,
        CacheDisable = 1UL << 4,
        Global = 1UL << 8,
        NoExecute = 1UL << 63,
    }
}
