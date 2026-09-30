// GcHeap — linked list of GcSegment blocks, bump allocator + freelist reuse.
//
// Allocation priority:
//   1. Free list, bucketed by size class — first-fit within the request's own
//      class (bounded), then the head of the smallest larger class, which
//      fits by construction. Split blocks with remainder >= MinFreeBlockSize.
//   2. Bump within current segment.
//   3. Grow: ask GcMemorySource for another block and bump in it.
//
// Freelist structure: one singly-linked list per size class, bucket b holding
// blocks of 2^(5+b)..2^(6+b)-1 bytes. Each free-object uses the first 8 bytes
// of its payload as a `next` pointer. Payload is at offset 12 (after
// MT* + Length). Minimum reusable block is 32 bytes so it fits the header +
// next-slot + 16-byte alignment padding.
//
// GcSweep builds this list as it walks (BeginFreelistRebuild + LinkFreeBlock),
// joining runs of adjacent dead blocks into one entry. RebuildFreelist() still
// exists and still derives the list from the heap itself, which is the way
// back if the list is ever doubted.

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

        // Free blocks, bucketed by size class. Bucket b holds blocks whose
        // size is in [2^(5+b), 2^(6+b)) — 32..63, 64..127, and so on. Each
        // entry is a raw pointer to a free object; its next-pointer sits at
        // (node + FreeNextOffset).
        //
        // One list was enough while the sweep joined nothing, because the
        // list was short for a different reason: it was mostly identical
        // 256-byte scraps and a big request failed against all of them. With
        // runs joined, a uniform workload leaves few blocks — but a MIXED one
        // still leaves many small ones, and first-fit down a list of blocks
        // that cannot hold the request costs the whole list to answer no.
        //
        // Buckets make that answer free: every block in a bucket above the
        // request's own is larger than the request by construction, so its
        // head fits without being measured.
        //
        // Heads live in a fixed-size struct in .bss rather than an array: a
        // `static readonly nint[]` would need a class constructor, and class
        // constructors do not run here (limits doc, ClassConstructorRunner).
        private const int FreeBucketCount = 32;

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential,
            Size = FreeBucketCount * 8)]
        private struct FreeBucketHeads { }
        private static FreeBucketHeads s_buckets;

        private static nint* BucketHeads()
            => (nint*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_buckets);

        // 32..63 -> 0, 64..127 -> 1, ... Sizes below MinFreeBlockSize never
        // reach a bucket: they are not tracked at all.
        private static int BucketOf(uint size)
        {
            uint v = size >> 5;
            int b = 0;
            while (v > 1) { v >>= 1; b++; }
            return b < FreeBucketCount ? b : FreeBucketCount - 1;
        }

        // How far first-fit may walk inside the request's OWN bucket before
        // giving up and taking a guaranteed fit from a larger one. Blocks
        // there straddle the request — some hold it, some do not — so this is
        // the only bucket that can be searched in vain, and the only one that
        // needs a bound.
        private const int MaxSameBucketProbes = 8;

        private static uint s_freelistNodes;
        private static ulong s_freelistReuseCount;  // diagnostics: alloc hits
        private static ulong s_freelistSplitCount;  // diagnostics: block splits

        // Free blocks examined while looking for one big enough, over the
        // life of the heap.
        //
        // The cost of an allocation is not visible any other way: a first-fit
        // walk down a list of four thousand blocks that are all too small
        // costs four thousand reads and answers "no", and from outside that is
        // indistinguishable from a heap that is simply busy. Measured as a
        // count rather than as time, because a count is the same number on a
        // loaded host and on an idle one.
        private static ulong s_freelistProbes;

        // Bounds of every segment taken together, and the segment found last.
        //
        // The marker asks "is this address in the heap" about every candidate
        // it pops, twice: once for the candidate and once for the MethodTable
        // it claims. Almost every answer is no — stack words, .rdata pointers,
        // kernel structs — and each no cost a walk of the whole segment list.
        // The bounds answer those in two comparisons; the one-entry cache
        // answers the yes cases, which cluster in whichever segment is being
        // allocated into.
        private static nint s_heapLow;
        private static nint s_heapHigh;
        private static GcSegmentHeader* s_lastFound;

        // Segments examined, over the life of the heap. The cost of the
        // question, in a number that does not depend on how busy the host is.
        private static ulong s_segmentScanSteps;

        public static bool IsInitialized => s_initialized;
        public static GcSegmentHeader* FirstSegment => s_firstSegment;

        // The counters below are read AROUND an allocation to measure what it
        // cost, and every one of them is NoInlining for that reason alone.
        //
        // ILC models `newarr` and `newobj` as allocations that do not write
        // user statics — which is true of every allocation but ours, because
        // ours IS the allocator and these are its counters. Two reads of the
        // same counter either side of `new byte[4096]` were therefore folded
        // into one, and the difference came out zero however much work had
        // been done. That cost three wrong diagnoses in a row: the measurement
        // said "the free list was never consulted" while the free list was
        // serving the request perfectly.
        //
        // A call that cannot be inlined cannot be folded away, so each read
        // happens where it is written.
        public static uint SegmentCount
        {
            [System.Runtime.CompilerServices.MethodImpl(
                System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            get => s_segmentCount;
        }
        public static ulong AllocCount
        {
            [System.Runtime.CompilerServices.MethodImpl(
                System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            get => s_allocCount;
        }
        public static ulong AllocBytes
        {
            [System.Runtime.CompilerServices.MethodImpl(
                System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            get => s_allocBytes;
        }
        public static uint FreelistNodes
        {
            [System.Runtime.CompilerServices.MethodImpl(
                System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            get => s_freelistNodes;
        }
        public static ulong FreelistReuseCount
        {
            [System.Runtime.CompilerServices.MethodImpl(
                System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            get => s_freelistReuseCount;
        }
        public static ulong FreelistSplitCount
        {
            [System.Runtime.CompilerServices.MethodImpl(
                System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            get => s_freelistSplitCount;
        }
        public static ulong FreelistProbeCount
        {
            [System.Runtime.CompilerServices.MethodImpl(
                System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            get => s_freelistProbes;
        }
        public static ulong SegmentScanSteps
        {
            [System.Runtime.CompilerServices.MethodImpl(
                System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            get => s_segmentScanSteps;
        }

        /// <summary>Bit b set when size class b has at least one block.</summary>
        /// <remarks>
        /// One number that says where the free memory actually is. The stats
        /// above say how much and how large, and those two agreed with each
        /// other while a request still found nothing — which narrows the
        /// question to "in which bucket", and nothing could answer it.
        /// </remarks>
        public static uint NonEmptyBucketMask()
        {
            nint* heads = BucketHeads();
            uint mask = 0;
            for (int b = 0; b < FreeBucketCount; b++)
                if (heads[b] != 0) mask |= 1u << b;
            return mask;
        }

        /// <summary>The size class a block of this size belongs to.</summary>
        public static int BucketIndexOf(uint size) => BucketOf(size);

        /// <summary>AllocCount without the NoInlining guard. For one test.</summary>
        /// <remarks>
        /// Exists to demonstrate the hazard the guard exists for, and for
        /// nothing else. Read either side of an allocation with no call in
        /// between, this one can be folded into a single read and its
        /// difference comes out zero while the count really did go up.
        ///
        /// Keeping the demonstration in the tree turns "I believe the reads
        /// were folded" into a number anybody can look at, which is worth one
        /// unused property.
        /// </remarks>
        public static ulong AllocCountFoldable => s_allocCount;

        // The address each path actually used for the bucket heads.
        //
        // BucketHeads() is Unsafe.AsPointer over a static, and a static is a
        // moveable variable to the compiler. If ILC were to expand that to
        // different storage at different call sites, the linker would write
        // blocks into one table and the search would read another — which is
        // exactly the picture that appeared once and could not be explained:
        // the statistics walk found a 256 KiB block while a request for four
        // kilobytes found nothing at all. Recorded rather than argued about.
        private static nint s_headsAddrAlloc;
        private static nint s_headsAddrLink;
        private static nint s_headsAddrStats;

        public static nint HeadsAddrAlloc => s_headsAddrAlloc;
        public static nint HeadsAddrLink => s_headsAddrLink;
        public static nint HeadsAddrStats => s_headsAddrStats;

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

        /// <summary>Which heap this is, for the refusal report.</summary>
        /// <remarks>
        /// The kernel and every application run their own copy of this class
        /// over their own memory, and both print through s_diagnostic to the
        /// same log. Three refusals came back unlabelled and could not be told
        /// apart: one of them had four segments and another two hundred and
        /// fifty-six, which is the only reason it was obvious they were
        /// different heaps at all.
        /// </remarks>
        public static string s_heapTag;

        private static uint s_lastRequest;

        /// <summary>Records a size refused before the allocator was reached.</summary>
        /// <remarks>
        /// RhpNewArray rejects an impossible length without calling
        /// AllocateRaw at all, so the size never reaches s_lastRequest and
        /// the refusal report names the previous allocation instead. Clamped
        /// rather than truncated: the number is for a human, and a 64-bit
        /// request that does not fit in the field should read as enormous,
        /// not as whatever the low half happens to be.
        /// </remarks>
        public static void NoteRefusedRequest(ulong size)
            => s_lastRequest = size > uint.MaxValue ? uint.MaxValue : (uint)size;

        /// <summary>Shape of the free memory: how much, in how many pieces,
        /// and how big the biggest one is.</summary>
        /// <remarks>
        /// Three numbers rather than one, because "out of memory" with 63 MB
        /// free is not a quantity problem: what a doubling array needs is one
        /// CONTIGUOUS piece, and the largest block is the only number that
        /// answers whether it exists.
        ///
        /// Bounded: this walks a list that is, by hypothesis, enormous, and a
        /// corrupt one must not turn a diagnosis into a hang.
        ///
        /// Shared with the refusal report below so a test and the log can
        /// never disagree about what was free at the moment of failure.
        /// </remarks>
        public static void GetFreeStats(out ulong freeBytes, out uint blocks, out uint largest)
        {
            blocks = 0;
            freeBytes = 0;
            largest = 0;

            nint* heads = BucketHeads();
            s_headsAddrStats = (nint)heads;
            uint guard = 0;
            for (int b = 0; b < FreeBucketCount; b++)
            {
                nint cur = heads[b];
                while (cur != 0 && guard < 4000000u)
                {
                    guard++;
                    uint size = ((GcObject*)cur)->ComputeSize();
                    if (size == 0) break;
                    blocks++;
                    freeBytes += size;
                    if (size > largest) largest = size;
                    cur = *(nint*)(cur + FreeNextOffset);
                }
            }
        }

        private static void ReportOutOfMemory()
        {
            if (s_diagnostic == null) return;

            GetFreeStats(out ulong freeBytes, out uint blocks, out uint largest);

            byte* line = stackalloc byte[224];
            int n = 0;
            Put(line, ref n, "[oom] heap=");
            Put(line, ref n, s_heapTag ?? "?");
            Put(line, ref n, " request=");
            PutULong(line, ref n, s_lastRequest);
            Put(line, ref n, " free=");
            PutULong(line, ref n, freeBytes);
            Put(line, ref n, " in ");
            PutULong(line, ref n, blocks);
            Put(line, ref n, " blocks largest=");
            PutULong(line, ref n, largest);
            Put(line, ref n, " segments=");
            PutULong(line, ref n, s_segmentCount);

            // Where the free memory sits, by size class, and which class the
            // request needed. A refusal whose largest block dwarfs the request
            // is not a shortage — it is a search that did not look, and these
            // two numbers are what tell the two apart at a glance.
            Put(line, ref n, " mask=0x");
            PutHex(line, ref n, NonEmptyBucketMask());
            Put(line, ref n, " wanted=");
            PutULong(line, ref n, (ulong)BucketOf(
                s_lastRequest < MinFreeBlockSize ? MinFreeBlockSize
                : (s_lastRequest + (ObjectAlignment - 1)) & ~(ObjectAlignment - 1)));
            Put(line, ref n, "\n");
            line[n] = 0;

            s_diagnostic(line);
        }

        private static void Put(byte* buffer, ref int at, string text)
        {
            for (int i = 0; i < text.Length && at < 158; i++)
                buffer[at++] = (byte)text[i];
        }

        private static void PutHex(byte* buffer, ref int at, ulong value)
        {
            const string digits = "0123456789ABCDEF";
            bool started = false;
            for (int shift = 28; shift >= 0; shift -= 4)
            {
                int nibble = (int)((value >> shift) & 0xF);
                if (nibble == 0 && !started && shift != 0) continue;
                started = true;
                if (at < 222) buffer[at++] = (byte)digits[nibble];
            }
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

            // Re-stated here, and this is not redundant. The collection above
            // runs arbitrary code, and anything it allocates comes back
            // through this method and overwrites the remembered size. The
            // refusal report then names that allocation instead of the one
            // that failed — which is how a report came back reading
            // "request=192 ... largest=51936" and looked like a search that
            // refused a block 270 times larger than it needed. The search was
            // fine; the label was wrong.
            if (allocated == null)
                s_lastRequest = size;

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

        // Own size class first, then the smallest larger one.
        //
        // Searching the request's own bucket keeps a small request out of a
        // large block, which is what stops splitting from grinding big blocks
        // into scraps. It is bounded, because that bucket is the only one
        // whose blocks might not fit. Everything above it fits by
        // construction, so the first non-empty one answers immediately.
        private static void* TryAllocateFromFreelist(uint aligned)
        {
            if (aligned < MinFreeBlockSize)
                aligned = MinFreeBlockSize;

            nint* heads = BucketHeads();
            s_headsAddrAlloc = (nint)heads;
            int own = BucketOf(aligned);

            void* fromOwn = TakeFromBucket(own, heads, aligned, MaxSameBucketProbes);
            if (fromOwn != null) return fromOwn;

            for (int b = own + 1; b < FreeBucketCount; b++)
            {
                if (heads[b] == 0) continue;
                void* fromLarger = TakeFromBucket(b, heads, aligned, 1);
                if (fromLarger != null) return fromLarger;
            }

            return null;
        }

        // First-fit inside one bucket, walking at most `maxProbes` entries.
        private static void* TakeFromBucket(int bucket, nint* heads, uint aligned, int maxProbes)
        {
            GcMethodTable* freeMt = GcSweep.FreeObjectMt;
            nint prev = 0;
            nint cur = heads[bucket];

            for (int probe = 0; cur != 0 && probe < maxProbes; probe++)
            {
                s_freelistProbes++;
                GcObject* block = (GcObject*)cur;
                uint blockSize = block->ComputeSize();
                if (blockSize == 0)
                    return null; // corrupt
                uint blockAligned = (blockSize + (ObjectAlignment - 1)) & ~(ObjectAlignment - 1);
                nint nextNode = *(nint*)(cur + FreeNextOffset);

                if (blockAligned >= aligned)
                {
                    // Unlink from its bucket.
                    if (prev == 0)
                        heads[bucket] = nextNode;
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
                            // Room for the next-pointer -> track for reuse,
                            // in the bucket its new size belongs to.
                            int tailBucket = BucketOf(remaining);
                            *(nint*)(tailPtr + FreeNextOffset) = heads[tailBucket];
                            heads[tailBucket] = tailPtr;
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

        /// <summary>Empties the free list so a sweep can build it as it goes.</summary>
        /// <remarks>
        /// Paired with <see cref="LinkFreeBlock"/>. The sweep already walks
        /// every object in address order, which is exactly the walk
        /// RebuildFreelist used to repeat afterwards — one pass instead of
        /// two, over a heap of tens of thousands of objects.
        /// </remarks>
        public static void BeginFreelistRebuild()
        {
            nint* heads = BucketHeads();
            for (int b = 0; b < FreeBucketCount; b++) heads[b] = 0;
            s_freelistNodes = 0;
        }

        /// <summary>Tracks a free block for reuse, biggest-first by address.</summary>
        /// <remarks>
        /// Blocks below MinFreeBlockSize are NOT tracked: the next-pointer
        /// lives at +12 and would not fit. They stay on the heap as walkable
        /// free markers, which is what keeps the heap walk from reading their
        /// stale bytes as a MethodTable (step131).
        /// </remarks>
        public static void LinkFreeBlock(nint address, uint alignedSize)
        {
            if (address == 0 || alignedSize < MinFreeBlockSize) return;
            nint* heads = BucketHeads();
            s_headsAddrLink = (nint)heads;
            int bucket = BucketOf(alignedSize);
            *(nint*)(address + FreeNextOffset) = heads[bucket];
            heads[bucket] = address;
            s_freelistNodes++;
        }

        // Walk all segments; re-link every free-object (of at least
        // MinFreeBlockSize) into a fresh freelist. No longer called by the
        // sweep, which builds the list as it walks; kept because it rebuilds
        // the list from the heap itself, which is the only way back if the
        // list is ever doubted.
        public static void RebuildFreelist()
        {
            GcMethodTable* freeMt = GcSweep.FreeObjectMt;
            BeginFreelistRebuild();

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
                        LinkFreeBlock(p, alignedSize);

                    p += (nint)alignedSize;
                }
                seg = seg->Next;
            }
        }

        // Two comparisons for the common answer, a walk only for the rest.
        //
        // The old comment here said "linear search — OK for small segment
        // count", and it was wrong in exactly our case: the count is small,
        // but the question is asked twice per marked candidate, and the
        // answer is almost always no, which is the case that cost the entire
        // list.
        public static GcSegmentHeader* FindSegmentContaining(nint addr)
        {
            // Outside every segment: no walk at all. This is the answer for
            // stack words, MethodTable pointers and anything else the marker
            // picks up, which is nearly everything it picks up.
            if (addr < s_heapLow || addr >= s_heapHigh)
                return null;

            // Inside the bounds is not yet inside a segment — segments are
            // separate allocations with gaps between them — so the walk still
            // decides. It starts with the last segment that answered yes,
            // because marking runs through one segment at a time.
            GcSegmentHeader* cached = s_lastFound;
            if (cached != null)
            {
                s_segmentScanSteps++;
                if (addr >= cached->Start && addr < cached->End)
                    return cached;
            }

            GcSegmentHeader* seg = s_firstSegment;
            while (seg != null)
            {
                s_segmentScanSteps++;
                if (addr >= seg->Start && addr < seg->End)
                {
                    s_lastFound = seg;
                    return seg;
                }
                seg = seg->Next;
            }
            return null;
        }

        // Widens the bounds to cover a new segment. Called for every segment
        // the heap takes, including the first.
        private static void NoteSegmentBounds(GcSegmentHeader* seg)
        {
            if (seg == null) return;
            if (s_heapLow == 0 || seg->Start < s_heapLow) s_heapLow = seg->Start;
            if (seg->End > s_heapHigh) s_heapHigh = seg->End;
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

            // Every segment widens the bounds the lookup rejects against.
            NoteSegmentBounds(hdr);
            return hdr;
        }
    }
}
