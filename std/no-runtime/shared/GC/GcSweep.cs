// Sweep phase for our Serial GC.
//
// Algorithm (from Kevin Gosse's GCHeap.Sweep.cs, MIT, with coalescing
// added in step177):
//   Linear walk through each segment, in address order. For every object:
//     - if marked: clear the mark bit (ready for next GC pass), and close
//       whatever run of dead bytes came before it;
//     - otherwise: add its bytes to the current run, whether it was a live
//       object that died or a free block from an earlier sweep.
//   A run is written as ONE free marker covering all of it, and linked into
//   the free list there and then. Runs never cross a segment boundary.
//
// The coalescing is the difference between "there are free bytes" and "there
// is a piece large enough". Without it every dead object became its own
// marker and nothing ever merged: the launcher reached 63 MB free in
// thousands of 256-byte pieces and could not grow a List<T>.
//
// The free-object marker is a synthetic GcMethodTable stored in .bss
// that we initialize at first use:
//   ComponentSize = 1      (each "element" is one byte of free space)
//   Flags         = 0      (Canonical kind, HasPointers=false, etc.)
//   BaseSize      = 12     (MT* + Length, no payload in base)
//   RelatedType   = null
//
// With these fields, `ComputeSize()` on a free object returns
// `BaseSize + Length * ComponentSize = 12 + N = total size`. Sweep
// sets Length so the total exactly matches the aligned size of the
// block being replaced.

using System.Runtime.InteropServices;

namespace SharpOS.Std.NoRuntime
{
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    internal struct GcFreeObjectMtStorage { }

    internal static unsafe class GcSweep
    {
        private static GcFreeObjectMtStorage s_freeMt;
        private static bool s_initialized;

        // Diagnostics
        private static uint s_sweptCount;        // objects converted to free
        private static uint s_keptCount;         // objects that survived

        public static uint LastSweptCount => s_sweptCount;
        public static uint LastKeptCount => s_keptCount;

        public static GcMethodTable* FreeObjectMt
        {
            get
            {
                fixed (GcFreeObjectMtStorage* p = &s_freeMt)
                    return (GcMethodTable*)p;
            }
        }

        private static void EnsureInit()
        {
            if (s_initialized)
                return;

            GcMethodTable* mt = FreeObjectMt;
            mt->ComponentSize = 1;
            // major-9: HasComponentSize is the 0x8000 flag bit, NOT the old
            // "ComponentSize != 0" test (major-8). With Flags=0 the free
            // marker reports HasComponentSize=false, so ComputeSize() returns
            // BaseSize (12) and IGNORES Length — a swept free block of any
            // size claims size 12. The sweeper then advances only 16 bytes
            // into a larger free block, lands in its middle, reads garbage as
            // an MT, and faults / mis-walks forever (silent hang on the first
            // Collect that meets a pre-existing free marker). Set the flag so
            // ComputeSize = BaseSize + Length*ComponentSize = aligned again.
            mt->Flags = 0x8000; // HasComponentSizeFlag (see GcMethodTable)
            mt->BaseSize = 12;
            mt->RelatedType = null;
            s_initialized = true;
        }

        // A run of adjacent dead blocks becomes ONE free marker.
        //
        // Before step177 each dead object got its own marker and the markers
        // were never joined, so a heap that had been used and released came
        // back as thousands of separate pieces. The launcher died of it: nine
        // collections, 63 MB free, and no single piece large enough for a
        // List<T> to double into. "Out of memory" was true about shape, not
        // about quantity.
        //
        // Emitted at the first live object after the run and at the end of the
        // segment, never across a segment boundary: two segments are separate
        // allocations and the bytes between them are not ours.
        private static void FlushRun(ref nint runStart, ref nint runBytes, GcMethodTable* freeMt)
        {
            if (runStart == 0 || runBytes <= 0)
            {
                runStart = 0;
                runBytes = 0;
                return;
            }

            GcObject* block = (GcObject*)runStart;
            block->RawMethodTable = freeMt;
            block->Length = (uint)runBytes - 12;   // ComputeSize == runBytes

            GcHeap.LinkFreeBlock(runStart, (uint)runBytes);

            runStart = 0;
            runBytes = 0;
        }

        // Run one sweep pass over all GcHeap segments. Assumes Mark phase
        // has already marked live objects. After Run, mark bits are cleared
        // (ready for next GC pass) and dead objects replaced with free markers.
        public static void Run()
        {
            // Foreign-runtime guard: when CoreCLR is allocating into the
            // kernel GcHeap, its live objects are invisible to the kernel
            // Mark phase (rooted only in CoreCLR's GC graph). Sweeping would
            // free-marker / reuse them → corruption. See GC.ReclamationDisabled.
            if (GC.ReclamationDisabled)
                return;

            EnsureInit();

            s_sweptCount = 0;
            s_keptCount = 0;

            GcMethodTable* freeMt = FreeObjectMt;

            // The list is built as this walk goes, not rebuilt by a second
            // walk afterwards. The sweep already visits every object in
            // address order, which is the same pass RebuildFreelist made.
            GcHeap.BeginFreelistRebuild();

            GcSegmentHeader* seg = GcHeap.FirstSegment;
            while (seg != null)
            {
                nint p = seg->ObjectStart;
                nint end = seg->Current;

                // The run of adjacent dead bytes being accumulated. Zero start
                // means there is none: no object ever lives at address zero.
                nint runStart = 0;
                nint runBytes = 0;

                while (p < end)
                {
                    GcObject* o = (GcObject*)p;
                    if (o->MethodTable == null)
                        break; // corrupt — stop walking this segment

                    uint size = o->ComputeSize();
                    if (size == 0)
                        break;

                    uint aligned = (size + 15u) & ~15u;

                    bool marked = o->IsMarked();
                    bool isAlreadyFree = o->MethodTable == freeMt;

                    if (marked)
                    {
                        o->Unmark();
                        s_keptCount++;

                        // A live object ends the run: the free block must
                        // cover only bytes nobody owns.
                        FlushRun(ref runStart, ref runBytes, freeMt);
                    }
                    else
                    {
                        // Dead, or already free: both are bytes available for
                        // reuse, and a free block that already existed is
                        // exactly what a neighbour should merge with.
                        if (!isAlreadyFree)
                            s_sweptCount++;

                        if (runStart == 0)
                            runStart = p;
                        runBytes += (nint)aligned;
                    }

                    p += (nint)aligned;
                }

                // The segment ends the run too — never merge across the gap
                // between two segments.
                FlushRun(ref runStart, ref runBytes, freeMt);

                seg = seg->Next;
            }
        }
    }
}
