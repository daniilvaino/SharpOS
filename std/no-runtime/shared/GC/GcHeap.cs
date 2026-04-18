// GcHeap — linked list of GcSegment blocks, bump allocator across segments.
//
// Phase 2: bump-only allocation (no sweep, no freelist). Each call to
// AllocateRaw bumps a pointer inside the current segment. When it doesn't
// fit, we ask GcMemorySource for another block. Memory is never released.
//
// Phase 3 adds Mark/Sweep which turns dead objects into freelist nodes and
// reuses space. Phase 2 just proves the segment infrastructure works.

namespace SharpOS.Std.NoRuntime
{
    internal static unsafe class GcHeap
    {
        private const uint DefaultSegmentSize = 256 * 1024; // 256 KB per segment
        private const uint ObjectAlignment = 16;            // allocations aligned to 16 bytes

        private static GcSegmentHeader* s_firstSegment;
        private static GcSegmentHeader* s_currentSegment;
        private static uint s_segmentCount;
        private static ulong s_allocCount;
        private static ulong s_allocBytes;
        private static bool s_initialized;

        public static bool IsInitialized => s_initialized;
        public static uint SegmentCount => s_segmentCount;
        public static ulong AllocCount => s_allocCount;
        public static ulong AllocBytes => s_allocBytes;
        public static GcSegmentHeader* FirstSegment => s_firstSegment;

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

        // Raw allocation: bumps the current segment, grows if needed, returns
        // pointer to size-bytes writable memory. Caller is responsible for
        // writing the MethodTable / length / payload.
        public static void* AllocateRaw(uint size)
        {
            if (!s_initialized)
                return null;
            if (size == 0)
                return null;

            uint aligned = (size + (ObjectAlignment - 1)) & ~(ObjectAlignment - 1);

            GcSegmentHeader* seg = s_currentSegment;
            if (seg == null)
                return null;

            // Fast path: fits in current segment.
            nint available = seg->End - seg->Current;
            if ((ulong)available >= aligned)
            {
                void* result = (void*)seg->Current;
                seg->Current += (nint)aligned;
                s_allocCount++;
                s_allocBytes += aligned;
                return result;
            }

            // Slow path: need a new segment. Size at least the required allocation.
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
