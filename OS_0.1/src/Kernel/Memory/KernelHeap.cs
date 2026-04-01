using OS.Hal;
using OS.Kernel.Util;

namespace OS.Kernel.Memory
{
    internal static unsafe class KernelHeap
    {
        private const uint PageSize = 4096;
        private const uint InitialPages = 4;
        private const uint DefaultGrowPages = 4;
        private const uint AllocationAlignment = 16;

        private static HeapBlock* s_head;
        private static HeapBlock* s_tail;
        private static bool s_initialized;

        private static uint s_heapPages;
        private static uint s_allocCount;
        private static uint s_freeCount;
        private static uint s_growCount;
        private static uint s_coalesceCount;
        private static uint s_allocFailures;

        public static bool IsInitialized => s_initialized;

        internal static HeapBlock* FirstBlock => s_head;

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

        public static void* Alloc(uint size)
        {
            if (!s_initialized || size == 0)
                return null;

            uint requestedSize = AlignRequest(size);

            while (true)
            {
                HeapBlock* block = FindFirstFit(requestedSize);
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

            HeapBlock* block = HeapBlockOps.FromPayload(pointer);
            if (!ContainsBlock(block))
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

            HeapBlock* merged = block;
            while (true)
            {
                HeapBlock* previous = merged;
                merged = MergeWithPrevious(merged);
                merged = MergeWithNext(merged);

                if (merged == previous)
                    break;
            }
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
            s_heapPages = 0;
            s_allocCount = 0;
            s_freeCount = 0;
            s_growCount = 0;
            s_coalesceCount = 0;
            s_allocFailures = 0;
        }

        private static uint AlignRequest(uint size)
        {
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

        private static HeapBlock* FindFirstFit(uint requestedSize)
        {
            HeapBlock* current = s_head;
            while (current != null)
            {
                if (current->IsFree != 0 && current->Size >= requestedSize)
                    return current;

                current = current->Next;
            }

            return null;
        }

        private static void* AllocateFromBlock(HeapBlock* block, uint requestedSize)
        {
            if (HeapBlockOps.CanSplit(block, requestedSize))
            {
                HeapBlock* split = HeapBlockOps.Split(block, requestedSize);
                if (block == s_tail)
                    s_tail = split;
            }

            block->IsFree = 0;
            s_allocCount++;
            return HeapBlockOps.GetPayload(block);
        }

        private static bool AddRegion(uint pageCount)
        {
            if (pageCount == 0)
                return false;

            ulong regionBase = global::OS.Kernel.PhysicalMemory.AllocPages(pageCount);
            if (regionBase == 0)
            {
                Log.Write(LogLevel.Warn, "heap grow failed: no physical pages");
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

            Log.Begin(LogLevel.Info);
            Console.Write("heap grow pages: ");
            Console.WriteUInt(pageCount);
            Log.EndLine();

            MergeWithPrevious(block);
            return true;
        }

        private static HeapBlock* MergeWithPrevious(HeapBlock* block)
        {
            if (block == null || block->Prev == null)
                return block;

            HeapBlock* previous = block->Prev;
            if (previous->IsFree == 0 || block->IsFree == 0)
                return block;

            if (!HeapBlockOps.AreAdjacent(previous, block))
                return block;

            previous->Size += HeapBlockOps.HeaderSize + block->Size;
            previous->Next = block->Next;

            if (block->Next != null)
                block->Next->Prev = previous;
            else
                s_tail = previous;

            s_coalesceCount++;
            Log.Write(LogLevel.Trace, "heap coalesce");
            return previous;
        }

        private static HeapBlock* MergeWithNext(HeapBlock* block)
        {
            if (block == null || block->Next == null)
                return block;

            HeapBlock* next = block->Next;
            if (block->IsFree == 0 || next->IsFree == 0)
                return block;

            if (!HeapBlockOps.AreAdjacent(block, next))
                return block;

            block->Size += HeapBlockOps.HeaderSize + next->Size;
            block->Next = next->Next;

            if (next->Next != null)
                next->Next->Prev = block;
            else
                s_tail = block;

            s_coalesceCount++;
            Log.Write(LogLevel.Trace, "heap coalesce");
            return block;
        }

        private static bool ContainsBlock(HeapBlock* block)
        {
            HeapBlock* current = s_head;
            while (current != null)
            {
                if (current == block)
                    return true;

                current = current->Next;
            }

            return false;
        }
    }
}
