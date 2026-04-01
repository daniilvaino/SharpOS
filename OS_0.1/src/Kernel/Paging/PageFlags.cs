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

    internal static class PageFlagOps
    {
        public const PageFlags SupportedMask =
            PageFlags.Present |
            PageFlags.Writable |
            PageFlags.User |
            PageFlags.WriteThrough |
            PageFlags.CacheDisable |
            PageFlags.Global |
            PageFlags.NoExecute;

        public static bool IsSupported(PageFlags flags)
        {
            return (flags & ~SupportedMask) == 0;
        }

        public static PageFlags NormalizeForMap(PageFlags flags)
        {
            return (flags & SupportedMask) | PageFlags.Present;
        }
    }
}
