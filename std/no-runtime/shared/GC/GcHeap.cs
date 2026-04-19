// GcHeap — linked list of GcSegment blocks, bump allocator + freelist reuse.
//
// Allocation priority:
//   1. Freelist first-fit — scan singly-linked list of free-object markers
//      (produced by GcSweep). Split blocks with remainder >= MinFreeBlockSize.
//   2. Bump within current segment.
//   3. Grow: ask GcMemorySource for another block and bump in it.
//
// Freelist structure: each free-object uses the first 8 bytes of its payload
// as a `next` pointer to the following free-object. Payload is at offset 12
// (after MT* + Length). Minimum reusable block is 32 bytes so it fits the
// header + next-slot + 16-byte alignment padding.
//
// After GcSweep turns dead objects into free markers, it calls
// RebuildFreelist() which walks all segments and re-links them.

namespace SharpOS.Std.NoRuntime
{
    internal static unsafe class GcHeap
    {
        private const uint DefaultSegmentSize = 256 * 1024; // 256 KB per segment
        private const uint ObjectAlignment = 16;            // allocations aligned to 16 bytes

        // Smallest free block we're willing to track. 32 bytes = 12 header +
        // 8 next-pointer + 12 slack (aligned up to 16). Smaller free blocks
        // stay walkable (as free markers) but are not reusable.
        private const uint MinFreeBlockSize = 32;
        private const int FreeNextOffset = 12; // offset of next-pointer inside a free object

        private static GcSegmentHeader* s_firstSegment;
        private static GcSegmentHeader* s_currentSegment;
        private static uint s_segmentCount;
        private static ulong s_allocCount;
        private static ulong s_allocBytes;
        private static bool s_initialized;

        // Freelist head (0 = empty). Each entry is a raw pointer to a free
        // object; its next-pointer sits at (node + FreeNextOffset).
        private static nint s_freelistHead;
        private static uint s_freelistNodes;
        private static ulong s_freelistReuseCount;  // diagnostics: alloc hits
        private static ulong s_freelistSplitCount;  // diagnostics: block splits

        public static bool IsInitialized => s_initialized;
        public static uint SegmentCount => s_segmentCount;
        public static ulong AllocCount => s_allocCount;
        public static ulong AllocBytes => s_allocBytes;
        public static GcSegmentHeader* FirstSegment => s_firstSegment;
        public static uint FreelistNodes => s_freelistNodes;
        public static ulong FreelistReuseCount => s_freelistReuseCount;
        public static ulong FreelistSplitCount => s_freelistSplitCount;

        public static bool Init()
        {
            if (s_initialized)
                return true;

            GcSegmentHeader* first = AllocateSegment(DefaultSegmentSize);
            if (first == null)
                return false;

            s_firstSegment = first;
            s_currentSegment = first;
            s_segmentCount = 1;
            s_initialized = true;
            return true;
        }

        // Raw allocation: freelist first-fit → bump → grow. Caller writes the
        // MethodTable / length / payload into the returned region.
        public static void* AllocateRaw(uint size)
        {
            if (!s_initialized)
                return null;
            if (size == 0)
                return null;

            uint aligned = (size + (ObjectAlignment - 1)) & ~(ObjectAlignment - 1);

            // 1. Freelist first-fit.
            void* reused = TryAllocateFromFreelist(aligned);
            if (reused != null)
                return reused;

            // 2. Bump within current segment.
            GcSegmentHeader* seg = s_currentSegment;
            if (seg == null)
                return null;

            nint available = seg->End - seg->Current;
            if ((ulong)available >= aligned)
            {
                void* result = (void*)seg->Current;
                seg->Current += (nint)aligned;
                s_allocCount++;
                s_allocBytes += aligned;
                return result;
            }

            // 3. Grow: need a new segment.
            uint segSize = DefaultSegmentSize;
            while (segSize < aligned + (uint)sizeof(GcSegmentHeader))
                segSize *= 2;

            GcSegmentHeader* fresh = AllocateSegment(segSize);
            if (fresh == null)
                return null;

            s_currentSegment->Next = fresh;
            s_currentSegment = fresh;
            s_segmentCount++;

            void* res = (void*)fresh->Current;
            fresh->Current += (nint)aligned;
            s_allocCount++;
            s_allocBytes += aligned;
            return res;
        }

        // First-fit walk: remove the first block whose aligned size >= needed.
        // If the block is bigger and the remainder is at least MinFreeBlockSize,
        // split the tail into a new free node (re-inserted at list head).
        private static void* TryAllocateFromFreelist(uint aligned)
        {
            if (s_freelistHead == 0)
                return null;

            GcMethodTable* freeMt = GcSweep.FreeObjectMt;
            nint prev = 0;
            nint cur = s_freelistHead;

            while (cur != 0)
            {
                GcObject* block = (GcObject*)cur;
                uint blockSize = block->ComputeSize();
                if (blockSize == 0)
                    return null; // corrupt
                uint blockAligned = (blockSize + (ObjectAlignment - 1)) & ~(ObjectAlignment - 1);
                nint nextNode = *(nint*)(cur + FreeNextOffset);

                if (blockAligned >= aligned)
                {
                    // Unlink from freelist.
                    if (prev == 0)
                        s_freelistHead = nextNode;
                    else
                        *(nint*)(prev + FreeNextOffset) = nextNode;
                    s_freelistNodes--;

                    uint remaining = blockAligned - aligned;
                    if (remaining >= MinFreeBlockSize)
                    {
                        nint tailPtr = cur + (nint)aligned;
                        GcObject* tail = (GcObject*)tailPtr;
                        tail->RawMethodTable = freeMt;
                        tail->Length = remaining - 12;
                        // Insert at head.
                        *(nint*)(tailPtr + FreeNextOffset) = s_freelistHead;
                        s_freelistHead = tailPtr;
                        s_freelistNodes++;
                        s_freelistSplitCount++;
                    }

                    s_allocCount++;
                    s_allocBytes += aligned;
                    s_freelistReuseCount++;
                    return (void*)cur;
                }

                prev = cur;
                cur = nextNode;
            }

            return null;
        }

        // Walk all segments; re-link every free-object (of at least
        // MinFreeBlockSize) into a fresh freelist. Called by GcSweep.Run
        // after the mark/sweep pass. Smaller free blocks stay on the heap
        // (walkable) but aren't tracked for reuse.
        public static void RebuildFreelist()
        {
            GcMethodTable* freeMt = GcSweep.FreeObjectMt;
            s_freelistHead = 0;
            s_freelistNodes = 0;

            GcSegmentHeader* seg = s_firstSegment;
            while (seg != null)
            {
                nint p = seg->ObjectStart;
                nint end = seg->Current;

                while (p < end)
                {
                    GcObject* o = (GcObject*)p;
                    if (o->MethodTable == null)
                        break;
                    uint size = o->ComputeSize();
                    if (size == 0)
                        break;
                    uint alignedSize = (size + (ObjectAlignment - 1)) & ~(ObjectAlignment - 1);

                    if (o->MethodTable == freeMt && alignedSize >= MinFreeBlockSize)
                    {
                        *(nint*)(p + FreeNextOffset) = s_freelistHead;
                        s_freelistHead = p;
                        s_freelistNodes++;
                    }

                    p += (nint)alignedSize;
                }
                seg = seg->Next;
            }
        }

        // Linear search — OK for small segment count.
        public static GcSegmentHeader* FindSegmentContaining(nint addr)
        {
            GcSegmentHeader* seg = s_firstSegment;
            while (seg != null)
            {
                if (addr >= seg->Start && addr < seg->End)
                    return seg;
                seg = seg->Next;
            }
            return null;
        }

        private static GcSegmentHeader* AllocateSegment(uint totalSize)
        {
            byte* block = (byte*)GcMemorySource.AllocateBlock(totalSize);
            if (block == null)
                return null;

            GcSegmentHeader* hdr = (GcSegmentHeader*)block;
            hdr->Start = (nint)block;
            hdr->ObjectStart = (nint)(block + sizeof(GcSegmentHeader));
            hdr->End = (nint)(block + totalSize);
            hdr->Current = hdr->ObjectStart;
            hdr->Next = null;
            return hdr;
        }
    }
}
