namespace OS.Kernel
{
    internal static unsafe class PhysicalMemory
    {
        private const ulong PageSize = 4096;
        private const ulong MinAllocAddress = 0x00100000;
        // Freelist capacity = 256K entries × 8 bytes = 2 MB. Covers 1 GB
        // of freed pages — more than any realistic single-session churn.
        // Lazily allocated on first FreePage call (skip cost if nothing
        // ever frees, e.g. minimal kernel-only smoke tests).
        private const int FreeListCapacity = 256 * 1024;

        private static MemoryRegion* s_regions;
        private static uint s_regionCount;
        private static uint s_nextRegionIndex;
        private static ulong s_cursor;
        private static ulong s_regionEnd;
        private static bool s_initialized;

        // Page freelist. Frees always arrive one page at a time — callers
        // decompose their runs — but requests do not: the kernel heap grows by
        // many contiguous pages at once, and so does every image the loader
        // maps. So a multi-page request falls back to searching the list for a
        // run, which is what makes freeing worth anything at all: while only
        // AllocPage consulted the list, processes could hand their pages back
        // all day and the heap would still die of "no physical pages".
        private static ulong[]? s_freeList;
        private static int s_freeListTop;     // index of NEXT free slot (== count)
        private static ulong s_freeTotal;
        private static ulong s_reuseTotal;
        private static ulong s_handedOut;

        /// <summary>Pages ever handed to a caller, freelist reuse included.</summary>
        public static ulong HandedOutPages => s_handedOut;

        /// <summary>Pages ever pushed back, and pages ever taken back out.</summary>
        public static ulong FreedPages => s_freeTotal;
        public static ulong ReusedPages => s_reuseTotal;

        /// <summary>Pages sitting in the freelist right now.</summary>
        public static ulong FreeListPages => (ulong)s_freeListTop;

        /// <summary>Bytes left in the region the bump cursor is currently in.</summary>
        public static ulong CursorRemainingBytes => s_regionEnd > s_cursor ? s_regionEnd - s_cursor : 0;

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

        // Push a 4K page back onto the freelist. Lazily initializes storage.
        // Caller MUST ensure the page is no longer mapped anywhere (no VA
        // points at it) and not currently in any other live structure.
        // Serialized against preemption: the free list is shared, and two threads
        // interleaved here hand the SAME frame to both — a corruption far worse
        // than a wrong flag, and one that would surface far from its cause.
        public static void FreePage(ulong pa)
        {
            Threading.Preemption.Suppress();
            try { FreePageCore(pa); }
            finally { Threading.Preemption.Allow(); }
        }

        private static void FreePageCore(ulong pa)
        {
            if (pa == 0) return;
            if (s_freeList == null) s_freeList = new ulong[FreeListCapacity];
            if (s_freeListTop >= FreeListCapacity) return;   // full — leak
            s_freeList[s_freeListTop++] = pa & ~(PageSize - 1);
            s_freeTotal++;
        }

        // Serialized against preemption: the free list is shared, and two threads
        // interleaved here hand the SAME frame to both — a corruption far worse
        // than a wrong flag, and one that would surface far from its cause.
        public static ulong AllocPage()
        {
            Threading.Preemption.Suppress();
            try { return AllocPageCore(); }
            finally { Threading.Preemption.Allow(); }
        }

        private static ulong AllocPageCore()
        {
            // Reuse from freelist when possible — keeps long PS sessions
            // from racing the bump cursor past the end of usable RAM.
            if (s_freeListTop > 0 && s_freeList != null)
            {
                ulong pa = s_freeList[--s_freeListTop];
                s_reuseTotal++;
                s_handedOut++;
                return pa;
            }
            return AllocPages(1);
        }

        // Serialized against preemption: the free list is shared, and two threads
        // interleaved here hand the SAME frame to both — a corruption far worse
        // than a wrong flag, and one that would surface far from its cause.
        public static ulong AllocPages(uint count)
        {
            Threading.Preemption.Suppress();
            try { return AllocPagesCore(count); }
            finally { Threading.Preemption.Allow(); }
        }

        private static ulong AllocPagesCore(uint count)
        {
            if (!s_initialized || count == 0)
                return 0;

            ulong bytes = (ulong)count * PageSize;

            while (true)
            {
                if (s_cursor == 0 || s_cursor + bytes > s_regionEnd)
                {
                    if (!MoveToNextUsableRegion())
                        return TakeContiguousFromFreeList(count);
                }

                ulong address = AlignUp(s_cursor, PageSize);
                if (address + bytes > s_regionEnd)
                {
                    s_cursor = 0;
                    s_regionEnd = 0;
                    continue;
                }

                s_cursor = address + bytes;
                s_handedOut += count;
                return address;
            }
        }

        /// <summary>
        /// Takes <paramref name="count"/> physically contiguous pages out of the
        /// freelist, or 0 when no such run is there.
        /// </summary>
        /// <remarks>
        /// Only ever reached once the bump cursor has run out of regions, so the
        /// sort it does is paid at the moment the alternative is failing the
        /// allocation. The list is kept sorted afterwards, which costs the next
        /// caller nothing and lets this one just scan.
        /// </remarks>
        private static ulong TakeContiguousFromFreeList(uint count)
        {
            if (s_freeList == null || s_freeListTop < (int)count)
                return 0;

            global::System.Array.Sort(s_freeList, 0, s_freeListTop);

            int runStart = 0;
            for (int i = 1; i <= s_freeListTop; i++)
            {
                bool contiguous = i < s_freeListTop
                    && s_freeList[i] == s_freeList[i - 1] + PageSize;

                if (contiguous) continue;

                if (i - runStart >= (int)count)
                {
                    ulong address = s_freeList[runStart];

                    // Remove the run by closing the gap. Order is preserved, so
                    // the list stays sorted for whoever comes next.
                    int removed = (int)count;
                    for (int j = runStart; j + removed < s_freeListTop; j++)
                        s_freeList[j] = s_freeList[j + removed];

                    s_freeListTop -= removed;
                    s_reuseTotal += (ulong)removed;
                    s_handedOut += (ulong)removed;
                    return address;
                }

                runStart = i;
            }

            return 0;
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
