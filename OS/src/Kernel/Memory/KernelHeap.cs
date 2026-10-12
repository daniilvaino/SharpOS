using OS.Hal;
using OS.Kernel.Util;

namespace OS.Kernel.Memory
{
    // The kernel's raw heap: kernel strings, the managed heap's segments,
    // interface-dispatch caches, per-thread C++ catch stacks.
    //
    // Every block, used or free, sits on one list in address order inside
    // its region; that list is what joins a freed block with its neighbours,
    // and it is never searched. Free blocks are also on one of the bucket
    // lists below, which is what an allocation searches.
    //
    // Before the buckets an allocation walked the address list from the head
    // to the first free block big enough, and Free walked it again to check
    // the pointer. The list only grows — pages are never handed back and the
    // dispatch caches are never freed — so by the end of a battery it held
    // about 0.8 million blocks and one allocation cost ~5 ms, 96-99 % of all
    // samples (done/step202.md).
    internal static unsafe class KernelHeap
    {
        private const uint PageSize = 4096;
        private const uint InitialPages = 4;
        private const uint DefaultGrowPages = 4;
        private const uint AllocationAlignment = 16;
        private const uint MaxRequestSize = 0x7FFF0000;

        private static HeapBlock* s_head;
        private static HeapBlock* s_tail;
        private static bool s_initialized;

        // Lowest and highest address any region covers: a cheap first check
        // in Free before a pointer is dereferenced as a block header.
        private static ulong s_lowest;
        private static ulong s_highest;

        private static uint s_heapPages;

        /// <summary>Pages the heap holds, free blocks included: it never gives any back.</summary>
        public static uint HeapPages => s_heapPages;
        private static uint s_allocCount;
        private static uint s_freeCount;
        private static uint s_growCount;
        private static uint s_coalesceCount;
        private static uint s_allocFailures;

        // Blocks on the address list and how many of them are free; free
        // blocks looked at while searching, over the life of the heap. The
        // last one is the cost of allocating, and it is what the grow line
        // prints: without it "faster" is a feeling.
        private static uint s_blockCount;
        private static uint s_freeBlocks;
        private static ulong s_probeSteps;

        public static bool IsInitialized => s_initialized;

        internal static HeapBlock* FirstBlock => s_head;

        // Free blocks, bucketed by payload size: bucket b holds sizes in
        // [2^(4+b), 2^(5+b)) — 16..31, 32..63, and so on. Every block in a
        // bucket above the request's own is larger than the request by
        // construction, so its head fits without being measured; only the
        // request's own bucket is searched, and only so far.
        //
        // The links live in the free block's payload (16 bytes at least, the
        // minimum a block is ever given). Heads live in .bss, in a fixed-size
        // struct: the heap starts in Phase 1, long before static
        // initializers can run.
        private const int BucketCount = 32;
        private const int MaxSameBucketProbes = 8;

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential,
            Size = BucketCount * 8)]
        private struct BucketHeadStorage { }
        private static BucketHeadStorage s_buckets;

        private struct FreeLinks
        {
            public HeapBlock* PrevFree;
            public HeapBlock* NextFree;
        }

        private static HeapBlock** BucketHeads()
            => (HeapBlock**)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_buckets);

        private static FreeLinks* LinksOf(HeapBlock* block)
            => (FreeLinks*)HeapBlockOps.GetPayload(block);

        // 16..31 -> 0, 32..63 -> 1, ...
        private static int BucketOf(uint size)
        {
            uint v = size >> 4;
            int b = 0;
            while (v > 1) { v >>= 1; b++; }
            return b < BucketCount ? b : BucketCount - 1;
        }

        public static bool Init()
        {
            if (s_initialized)
                return true;

            ResetState();
            if (!AddRegion(InitialPages))
                return false;

            s_initialized = true;
            return true;
        }

        // Alloc and Free run with preemption suppressed, not under a lock.
        // The heap re-enters itself: growing it from inside AddRegion can
        // allocate again (the periodic GcHeapCensus dump formats strings into
        // this heap), and a non-reentrant lock would deadlock there.
        // Suppression is a counter that nests with itself and that nothing
        // waits on. On one CPU that is enough — nobody else can be running.
        public static void* Alloc(uint size)
        {
            if (!s_initialized || size == 0)
                return null;

            OS.Kernel.Threading.Preemption.Suppress();
            try { return AllocCore(size); }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
        }

        private static void* AllocCore(uint size)
        {
            uint requestedSize = AlignRequest(size);
            if (requestedSize == 0)
            {
                s_allocFailures++;
                return null;
            }

            while (true)
            {
                HeapBlock* block = FindFit(requestedSize);
                if (block != null)
                    return AllocateFromBlock(block, requestedSize);

                uint growPages = PagesForPayload(requestedSize);
                if (growPages < DefaultGrowPages)
                    growPages = DefaultGrowPages;

                if (!AddRegion(growPages))
                {
                    s_allocFailures++;
                    return null;
                }
            }
        }

        public static void Free(void* pointer)
        {
            if (!s_initialized || pointer == null)
                return;

            // Same protection as Alloc, and for a sharper reason: freeing
            // merges with both neighbours, rewriting the shared block list in
            // several steps. Interrupted midway, it leaves the list in a state
            // the next Alloc walks straight into. Suppressing only Alloc guards
            // Alloc alone — it does nothing for the thread that is inside Free.
            OS.Kernel.Threading.Preemption.Suppress();
            try { FreeCore(pointer); }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
        }

        private static void FreeCore(void* pointer)
        {
            HeapBlock* block = HeapBlockOps.FromPayload(pointer);
            if (!LooksLikeBlock(block))
            {
                Log.Write(LogLevel.Warn, "heap free ignored: unknown pointer");
                return;
            }

            if (block->IsFree != 0)
            {
                Log.Write(LogLevel.Warn, "heap free ignored: block already free");
                return;
            }

            block->IsFree = 1;
            s_freeCount++;

            // Free blocks are always joined as far as they go, so one step
            // each way is all a newly freed block can need.
            HeapBlock* merged = block;
            HeapBlock* previous = merged->Prev;
            if (previous != null && previous->IsFree != 0 && HeapBlockOps.AreAdjacent(previous, merged))
            {
                RemoveFree(previous);
                Absorb(previous, merged);
                merged = previous;
            }

            HeapBlock* next = merged->Next;
            if (next != null && next->IsFree != 0 && HeapBlockOps.AreAdjacent(merged, next))
            {
                RemoveFree(next);
                Absorb(merged, next);
            }

            InsertFree(merged);
        }

        // A pointer the heap handed out: inside a region, aligned, and its
        // neighbours on the address list point back at it. Replaces a walk
        // from the head of the list to the block; a stray pointer into the
        // middle of some block fails the back-links.
        private static bool LooksLikeBlock(HeapBlock* block)
        {
            ulong address = (ulong)block;
            if (address < s_lowest || address + HeapBlockOps.HeaderSize > s_highest)
                return false;
            if ((address & 7) != 0)
                return false;

            HeapBlock* previous = block->Prev;
            HeapBlock* next = block->Next;
            if (previous == null ? s_head != block : previous->Next != block)
                return false;
            if (next == null ? s_tail != block : next->Prev != block)
                return false;
            return true;
        }

        internal static void GetSummary(out HeapSummary summary)
        {
            summary = default;
            summary.IsInitialized = s_initialized;
            summary.HeapPages = s_heapPages;
            summary.AllocCount = s_allocCount;
            summary.FreeCount = s_freeCount;
            summary.GrowCount = s_growCount;
            summary.CoalesceCount = s_coalesceCount;
            summary.AllocFailures = s_allocFailures;

            HeapBlock* current = s_head;
            while (current != null)
            {
                summary.BlockCount++;
                if (current->IsFree != 0)
                    summary.FreeBytes += current->Size;
                else
                    summary.UsedBytes += current->Size;

                current = current->Next;
            }
        }

        private static void ResetState()
        {
            s_head = null;
            s_tail = null;
            s_initialized = false;
            s_lowest = ulong.MaxValue;
            s_highest = 0;
            s_heapPages = 0;
            s_allocCount = 0;
            s_freeCount = 0;
            s_growCount = 0;
            s_coalesceCount = 0;
            s_allocFailures = 0;
            s_blockCount = 0;
            s_freeBlocks = 0;
            s_probeSteps = 0;

            HeapBlock** heads = BucketHeads();
            for (int i = 0; i < BucketCount; i++)
                heads[i] = null;
        }

        private static uint AlignRequest(uint size)
        {
            // Sizes near 4 GiB would align up past zero into a tiny block.
            if (size > MaxRequestSize)
                return 0;

            uint aligned = BitOps.AlignUp(size, AllocationAlignment);
            if (aligned < HeapBlockOps.MinimumSplitPayload)
                aligned = HeapBlockOps.MinimumSplitPayload;
            return aligned;
        }

        private static uint PagesForPayload(uint payloadSize)
        {
            uint totalBytes = payloadSize + HeapBlockOps.HeaderSize;
            uint pages = totalBytes / PageSize;
            if ((totalBytes % PageSize) != 0)
                pages++;

            if (pages == 0)
                pages = 1;

            return pages;
        }

        private static HeapBlock* FindFit(uint requestedSize)
        {
            HeapBlock** heads = BucketHeads();
            int own = BucketOf(requestedSize);

            // The own bucket holds blocks on both sides of the request, so it
            // is the only one that can be searched in vain: look a few deep.
            HeapBlock* current = heads[own];
            for (int probes = 0; current != null && probes < MaxSameBucketProbes; probes++)
            {
                s_probeSteps++;
                if (current->Size >= requestedSize)
                    return current;
                current = LinksOf(current)->NextFree;
            }

            for (int b = own + 1; b < BucketCount; b++)
            {
                s_probeSteps++;
                if (heads[b] != null)
                    return heads[b];
            }

            // Nothing larger anywhere: the rest of the own bucket before
            // asking for pages, so a fit deeper than the probe limit is not
            // left behind while the heap grows past it.
            while (current != null)
            {
                s_probeSteps++;
                if (current->Size >= requestedSize)
                    return current;
                current = LinksOf(current)->NextFree;
            }

            return null;
        }

        private static void* AllocateFromBlock(HeapBlock* block, uint requestedSize)
        {
            RemoveFree(block);

            if (HeapBlockOps.CanSplit(block, requestedSize))
            {
                HeapBlock* split = HeapBlockOps.Split(block, requestedSize);
                if (block == s_tail)
                    s_tail = split;
                s_blockCount++;
                InsertFree(split);
            }

            block->IsFree = 0;
            s_allocCount++;
            return HeapBlockOps.GetPayload(block);
        }

        private static void InsertFree(HeapBlock* block)
        {
            HeapBlock** heads = BucketHeads();
            int b = BucketOf(block->Size);
            FreeLinks* links = LinksOf(block);
            links->PrevFree = null;
            links->NextFree = heads[b];
            if (heads[b] != null)
                LinksOf(heads[b])->PrevFree = block;
            heads[b] = block;
            s_freeBlocks++;
        }

        // Must run before the block's Size changes: the bucket is found from it.
        private static void RemoveFree(HeapBlock* block)
        {
            FreeLinks* links = LinksOf(block);
            if (links->PrevFree != null)
                LinksOf(links->PrevFree)->NextFree = links->NextFree;
            else
                BucketHeads()[BucketOf(block->Size)] = links->NextFree;

            if (links->NextFree != null)
                LinksOf(links->NextFree)->PrevFree = links->PrevFree;

            links->PrevFree = null;
            links->NextFree = null;
            s_freeBlocks--;
        }

        // Joins `right` into `left` on the address list; neither is on a
        // bucket list at this point.
        private static void Absorb(HeapBlock* left, HeapBlock* right)
        {
            left->Size += HeapBlockOps.HeaderSize + right->Size;
            left->Next = right->Next;

            if (right->Next != null)
                right->Next->Prev = left;
            else
                s_tail = left;

            s_blockCount--;
            s_coalesceCount++;
        }

        private static bool AddRegion(uint pageCount)
        {
            if (pageCount == 0)
                return false;

            ulong regionBase = global::OS.Kernel.PhysicalMemory.AllocPages(pageCount);
            if (regionBase == 0)
            {
                // Numbers, not a guess: which of the three it is — the pool
                // genuinely spent, pages returned but too scattered for a run
                // this long, or nothing ever returned at all — is not something
                // the bare message can tell apart. Printed without allocating:
                // the heap that just failed to grow is the one a formatted
                // number would come from, and its failure would log again.
                Log.Begin(LogLevel.Warn);
                Console.Write("heap grow failed: no physical pages want=");
                Console.WriteUIntRaw(pageCount);
                Console.Write(" handedOut=");
                Console.WriteUIntRaw((uint)global::OS.Kernel.PhysicalMemory.HandedOutPages);
                Console.Write(" freed=");
                Console.WriteUIntRaw((uint)global::OS.Kernel.PhysicalMemory.FreedPages);
                Console.Write(" reused=");
                Console.WriteUIntRaw((uint)global::OS.Kernel.PhysicalMemory.ReusedPages);
                Console.Write(" inFreeList=");
                Console.WriteUIntRaw((uint)global::OS.Kernel.PhysicalMemory.FreeListPages);
                Console.Write(" heapPages=");
                Console.WriteUIntRaw((uint)s_heapPages);
                Log.EndLine();
                return false;
            }

            uint regionBytes = pageCount * PageSize;
            if (regionBytes <= HeapBlockOps.HeaderSize + HeapBlockOps.MinimumSplitPayload)
                return false;

            HeapBlock* block = (HeapBlock*)regionBase;
            block->Size = regionBytes - HeapBlockOps.HeaderSize;
            block->IsFree = 1;
            block->Next = null;
            block->Prev = s_tail;

            if (s_tail != null)
                s_tail->Next = block;
            else
                s_head = block;

            s_tail = block;
            s_heapPages += pageCount;
            s_growCount++;
            s_blockCount++;
            if (regionBase < s_lowest)
                s_lowest = regionBase;
            if (regionBase + regionBytes > s_highest)
                s_highest = regionBase + regionBytes;

            // A region that starts where the previous one ended continues its
            // free tail. Joined and on a bucket list before anything below can
            // allocate again.
            HeapBlock* previous = block->Prev;
            if (previous != null && previous->IsFree != 0 && HeapBlockOps.AreAdjacent(previous, block))
            {
                RemoveFree(previous);
                Absorb(previous, block);
                block = previous;
            }
            InsertFree(block);

            // On the measurement channel, not the log's: this is a number for
            // whoever compares runs, and it was being painted over a
            // full-screen program. A Terminal.Gui redraw makes the terminal
            // engine allocate, the heap grows sixty-five pages at a time, and
            // the notice appeared and vanished under the next repaint - forty
            // eight times in one session of it. Same argument OutputRouting
            // already makes for [perf]: drawing a measurement is one more cost
            // inside the thing being measured.
            //
            // blocks/free: the address list and the bucket lists; probes:
            // free blocks looked at by every allocation so far.
            PerfLine("[info] heap grow pages: ");
            PerfUInt(pageCount);
            PerfLine(" blocks=");
            PerfUInt(s_blockCount);
            PerfLine(" free=");
            PerfUInt(s_freeBlocks);
            PerfLine(" allocs=");
            PerfUInt(s_allocCount);
            PerfLine(" probes=");
            PerfULong(s_probeSteps);
            PerfLine("\r\n");

            // Periodically, what the heap is full OF. Growth alone says only
            // that it is filling; a machine left at a prompt produced 502 of
            // these lines and nothing else, which named no culprit. The census
            // walks the objects and names the types.
            uint every = OS.Kernel.Diagnostics.Probes.HeapCensusEveryGrowths;
            if (every != 0 && (s_growCount % every) == 0)
                GcHeapCensus.Dump(12);

            return true;
        }

        private static void PerfLine(string text)
        {
            for (int i = 0; i < text.Length; i++)
                OS.Hal.Platform.WriteChar(text[i], OS.Hal.OutputChannel.Perf);
        }

        private static void PerfUInt(uint value) => PerfULong(value);

        private static void PerfULong(ulong value)
        {
            char* digits = stackalloc char[20];
            int count = 0;
            do
            {
                digits[count++] = (char)('0' + (int)(value % 10u));
                value /= 10u;
            } while (value != 0);

            while (count > 0)
                OS.Hal.Platform.WriteChar(digits[--count], OS.Hal.OutputChannel.Perf);
        }
    }
}
