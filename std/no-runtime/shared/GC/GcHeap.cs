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

        // Raw allocation: freelist first-fit → bump → grow. Returned region is
        // zeroed — callers (RhpNewFast, RhpNewArray, etc.) then write the
        // MethodTable / length over the leading bytes. Zero-init is required
        // so that `new int[n]` / `new T[n]` behave per-spec (default(T)
        // elements) and so freelist-reused blocks don't leak stale references
        // that would fool the conservative scanner.
        // Non-reentrancy hooks, installed by the host (the kernel installs
        // preemption suppression; apps leave them null).
        //
        // The allocator updates a free list and a bump pointer in several
        // steps. On one CPU that is safe only while nobody else can run in the
        // middle of it — true under cooperative scheduling, false the moment a
        // timer can take the CPU away. A lock is the wrong tool here and was
        // already tried: the logging path allocates, so a naive lock
        // re-enters itself (step95). Suppressing the switch is reentrancy-safe
        // by construction, because it is a counter and nothing waits on it.
        //
        // Left as hooks rather than a direct call so std stays free of kernel
        // types — same shape as GC.s_collectHook.
        public static delegate*<void> s_enterCritical;
        public static delegate*<void> s_leaveCritical;

        // Largest single allocation. Above it the size arithmetic below wraps
        // in uint: 0xFFFFFFF8 aligned up to a 0-byte slot, a few bytes less
        // to a segment far smaller than the zero-fill that followed, and past
        // 2 GiB the segment-size doubling reached 0 and spun forever with
        // preemption suppressed.
        public const uint MaxAllocationSize = 0x7FFF0000;

        // The fallback OutOfMemoryException (see OutOfMemory). Made by
        // PrepareOutOfMemory as soon as the heap can hold it: by the time it
        // is needed there may be no room left to make one. Kept as a raw
        // address in a registered root slot rather than a reference static,
        // so it exists before the image's GC statics do.
        private static nint s_outOfMemory;
        private static bool s_allocatingOutOfMemory;

        // Where an allocation failure goes when throwing cannot help: an
        // allocation before the heap exists, or before PrepareOutOfMemory has
        // run. The kernel points it at Panic.Fail, apps at an exit; left null,
        // the machine stops here.
        public static delegate*<string, void> s_fatal;

        /// <summary>
        /// Makes the <see cref="OutOfMemoryException"/> thrown when there is
        /// no room for a fresh one, together with its stack-trace buffer.
        /// Call right after <see cref="Init"/>. NativeAOT does the same first
        /// thing in CoreLib's library initializer
        /// (PreallocatedOutOfMemoryException.Initialize).
        /// </summary>
        public static void PrepareOutOfMemory()
        {
            if (s_outOfMemory != 0)
                return;

            // With a message, and made here for the same reason the object is:
            // at the moment of failure there is no room to build a string. An
            // exception that reaches the log as "(no message)" says only that
            // something refused to allocate — not which heap, and not that the
            // heap may well be empty and merely too broken up to serve.
            var exception = new System.OutOfMemoryException(
                "No memory could be allocated. The heap may still have free "
                + "space that is too fragmented to satisfy this request — see "
                + "the [oom] line for the request size and the largest free block.");
            exception.ReserveStackTrace();
            s_outOfMemory = *(nint*)&exception;

            fixed (nint* slot = &s_outOfMemory)
                GcRoots.RegisterRawSlot(slot);
        }

        /// <summary>
        /// The exception to throw for an allocation that cannot be satisfied:
        /// <c>throw GcHeap.OutOfMemory();</c>. Does not return when there is
        /// nothing that can be thrown.
        /// </summary>
        /// <remarks>
        /// As NativeAOT's GetRuntimeException: a fresh exception while there
        /// is still room for one — a failed 2 GiB array leaves plenty — and
        /// the preallocated one when there is not. The flag keeps the attempt
        /// from recursing: its own failure comes back here and takes the
        /// preallocated one, which the catch below then drops.
        /// </remarks>
        // Where a refused allocation says what it actually hit.
        //
        // "Out of memory" is the wrong words for the common case and cost a
        // day to see past: the heap was EMPTY — 1.85 million objects had just
        // been swept — and the request still failed, because nothing left was
        // contiguous. The exception carries no message and no numbers, so the
        // difference between "nothing free" and "nothing big enough" had to be
        // reconstructed by hand from nine collections and a symbolized trace.
        //
        // One line at the point of failure says it outright. Nothing here
        // allocates: the digits go into a stack buffer, and the sink takes
        // bytes — the heap that just refused a request is the last thing to
        // ask for a formatted string.
        public static delegate*<byte*, void> s_diagnostic;

        private static uint s_lastRequest;

        private static void ReportOutOfMemory()
        {
            if (s_diagnostic == null) return;

            uint blocks = 0;
            ulong freeBytes = 0;
            uint largest = 0;

            // Bounded: this walks a list that is, by hypothesis, enormous, and
            // a corrupt one must not turn a diagnosis into a hang.
            nint cur = s_freelistHead;
            for (uint guard = 0; cur != 0 && guard < 4000000u; guard++)
            {
                uint size = ((GcObject*)cur)->ComputeSize();
                if (size == 0) break;
                blocks++;
                freeBytes += size;
                if (size > largest) largest = size;
                cur = *(nint*)(cur + FreeNextOffset);
            }

            byte* line = stackalloc byte[160];
            int n = 0;
            Put(line, ref n, "[oom] request=");
            PutULong(line, ref n, s_lastRequest);
            Put(line, ref n, " free=");
            PutULong(line, ref n, freeBytes);
            Put(line, ref n, " in ");
            PutULong(line, ref n, blocks);
            Put(line, ref n, " blocks largest=");
            PutULong(line, ref n, largest);
            Put(line, ref n, " segments=");
            PutULong(line, ref n, s_segmentCount);
            Put(line, ref n, "\n");
            line[n] = 0;

            s_diagnostic(line);
        }

        private static void Put(byte* buffer, ref int at, string text)
        {
            for (int i = 0; i < text.Length && at < 158; i++)
                buffer[at++] = (byte)text[i];
        }

        private static void PutULong(byte* buffer, ref int at, ulong value)
        {
            byte* digits = stackalloc byte[20];
            int count = 0;
            do { digits[count++] = (byte)('0' + (int)(value % 10UL)); value /= 10UL; }
            while (value != 0);
            while (count > 0 && at < 158) buffer[at++] = digits[--count];
        }

        public static System.OutOfMemoryException OutOfMemory()
        {
            if (!s_initialized)
                Fatal("object allocated before the GC heap exists");
            if (s_outOfMemory == 0)
                Fatal("out of memory before the OutOfMemoryException was made");

            ReportOutOfMemory();

            if (!s_allocatingOutOfMemory)
            {
                s_allocatingOutOfMemory = true;
                System.OutOfMemoryException fresh = null;
                try
                {
                    fresh = new System.OutOfMemoryException(
                        "No memory could be allocated. The heap may still have "
                        + "free space that is too fragmented to satisfy this "
                        + "request — see the [oom] line for the numbers.");
                    // Its trace buffer now, not in the middle of the throw.
                    fresh.ReserveStackTrace();
                }
                catch
                {
                    fresh = null;
                }
                s_allocatingOutOfMemory = false;

                if (fresh != null)
                    return fresh;
            }

            System.OutOfMemoryException preallocated = null;
            *(nint*)&preallocated = s_outOfMemory;
            preallocated.ResetStackTrace();
            return preallocated;
        }

        internal static void Fatal(string message)
        {
            if (s_fatal != null)
                s_fatal(message);
            while (true) { }
        }

        // Set when the heap took a new segment, cleared by whoever reads it.
        //
        // A flag rather than a callback on purpose: a report printed from
        // inside the allocator re-enters it through the print path, and the
        // caller is holding the allocation critical section. The consumer
        // picks it up from its own idle loop, where printing is free to
        // allocate.
        private static bool s_segmentGrew;

        /// <summary>True once per new segment; reading it clears it.</summary>
        public static bool TakeSegmentGrewFlag()
        {
            if (!s_segmentGrew) return false;
            s_segmentGrew = false;
            return true;
        }

        public static void* AllocateRaw(uint size)
        {
            if (!s_initialized)
                return null;
            if (size == 0 || size > MaxAllocationSize)
                return null;

            // Remembered for the refusal report: by the time OutOfMemory() is
            // asked for the exception, the size that could not be met is gone.
            s_lastRequest = size;

            if (s_enterCritical != null) s_enterCritical();
            void* allocated = AllocateRawCore(size);
            if (s_leaveCritical != null) s_leaveCritical();

            if (allocated != null)
            {
                // Sampled outside the critical section: the profiler walks the
                // stack, which is longer than anything the lock is meant to
                // cover.
                if (GC.AllocSampleEvery != 0 && GC.s_allocSampleHook != null
                    && (s_allocCount % GC.AllocSampleEvery) == 0)
                    GC.s_allocSampleHook(size);

                return allocated;
            }

            // Out of room — collect, then ask once more.
            //
            // This step was missing, and its absence did not look like a
            // memory problem from where it surfaced: the heap simply began
            // returning null, and the first caller to write through that null
            // faulted. The report named a string constructor, three frames
            // below whoever had actually been allocating.
            //
            // Collection runs OUTSIDE the critical section on purpose: the
            // collector walks stacks and allocates nothing, but it is long, and
            // holding off the scheduler for its duration is exactly the pause
            // suppression exists to keep short.
            if (GC.s_collectHook != null)
            {
                GC.s_collectHook();

                if (s_enterCritical != null) s_enterCritical();
                allocated = AllocateRawCore(size);
                if (s_leaveCritical != null) s_leaveCritical();
            }

            return allocated;
        }

        private static void* AllocateRawCore(uint size)
        {

            uint aligned = (size + (ObjectAlignment - 1)) & ~(ObjectAlignment - 1);

            void* result;

            // 1. Freelist first-fit.
            result = TryAllocateFromFreelist(aligned);
            if (result == null)
            {
                // 2. Bump within current segment.
                GcSegmentHeader* seg = s_currentSegment;
                if (seg == null)
                    return null;

                nint available = seg->End - seg->Current;
                if ((ulong)available >= aligned)
                {
                    result = (void*)seg->Current;
                    seg->Current += (nint)aligned;
                    s_allocCount++;
                    s_allocBytes += aligned;
                }
                else
                {
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
                    s_segmentGrew = true;

                    result = (void*)fresh->Current;
                    fresh->Current += (nint)aligned;
                    s_allocCount++;
                    s_allocBytes += aligned;
                }
            }

            // Zero the region in 8-byte chunks (aligned is always 16-multiple).
            ulong* p = (ulong*)result;
            uint qwords = aligned >> 3;
            for (uint i = 0; i < qwords; i++)
                p[i] = 0;

            return result;
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

                    // remaining is a multiple of ObjectAlignment (both blockAligned
                    // and aligned are), so it is either 0 or >= 16.
                    uint remaining = blockAligned - aligned;
                    if (remaining >= ObjectAlignment)
                    {
                        // ALWAYS turn a non-empty remainder into a walkable free
                        // marker (MT@0 + Length@8 = 12 bytes, fits in 16). If we
                        // skip this for small remainders, the heap walk (sweep /
                        // GcMark unmark) steps by the allocated object's
                        // ComputeSize and lands on the untracked remainder,
                        // reading its STALE bytes as a MethodTable — e.g.
                        // leftover boxed-double NaN 0xFFFFFFFF00000000 ->
                        // `test [mt+2]` #PF. This was the pervasive, non-
                        // deterministic heap corruptor (step131). The remainder
                        // is only ADDED to the freelist (next-pointer @+12, needs
                        // >= MinFreeBlockSize=32 so the pointer fits) when large
                        // enough; smaller ones stay walkable-but-unreused.
                        nint tailPtr = cur + (nint)aligned;
                        GcObject* tail = (GcObject*)tailPtr;
                        tail->RawMethodTable = freeMt;
                        tail->Length = remaining - 12;      // ComputeSize == remaining
                        if (remaining >= MinFreeBlockSize)
                        {
                            // Room for the next-pointer -> track for reuse.
                            *(nint*)(tailPtr + FreeNextOffset) = s_freelistHead;
                            s_freelistHead = tailPtr;
                            s_freelistNodes++;
                            s_freelistSplitCount++;
                        }
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
            // sizeof(GcSegmentHeader) = 40 → block+40 is only 8-aligned even if
            // block is page-aligned. Bump round-up to ObjectAlignment (16) so
            // the first user allocation lands at a 16-aligned address. All
            // subsequent allocations bump by 16-multiples and stay aligned.
            // Required by CoreCLR (and CRT spec): malloc-returned memory must
            // be aligned for MAX_ALIGN_T = 16 on x64 — `movaps` etc. otherwise #GP.
            nint rawStart = (nint)(block + sizeof(GcSegmentHeader));
            hdr->ObjectStart = (rawStart + (nint)(ObjectAlignment - 1)) & ~(nint)(ObjectAlignment - 1);
            hdr->End = (nint)(block + totalSize);
            hdr->Current = hdr->ObjectStart;
            hdr->Next = null;
            return hdr;
        }
    }
}
