// Sweep phase for our Serial GC.
//
// Algorithm (from Kevin Gosse's GCHeap.Sweep.cs, MIT):
//   Linear walk through each segment. For every object:
//     - if marked: clear the mark bit (ready for next GC pass)
//     - if unmarked AND not already a free-object: overwrite with a
//       free-object marker so the heap remains walkable.
//   The bytes of a dead object become a "free block" that a future
//   allocator can reuse (or ignore — see GcHeap allocation policy).
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
            mt->Flags = 0;
            mt->BaseSize = 12;
            mt->RelatedType = null;
            s_initialized = true;
        }

        // Run one sweep pass over all GcHeap segments. Assumes Mark phase
        // has already marked live objects. After Run, mark bits are cleared
        // (ready for next GC pass) and dead objects replaced with free markers.
        public static void Run()
        {
            EnsureInit();

            s_sweptCount = 0;
            s_keptCount = 0;

            GcMethodTable* freeMt = FreeObjectMt;

            GcSegmentHeader* seg = GcHeap.FirstSegment;
            while (seg != null)
            {
                nint p = seg->ObjectStart;
                nint end = seg->Current;

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
                    }
                    else if (!isAlreadyFree)
                    {
                        // Overwrite this dead object with a free marker.
                        // Preserve total aligned size so walk stays consistent.
                        o->RawMethodTable = freeMt;
                        o->Length = aligned - 12; // so ComputeSize == aligned
                        s_sweptCount++;
                    }
                    // else: already a free object, leave as is

                    p += (nint)aligned;
                }

                seg = seg->Next;
            }

            // Re-link the just-created free markers so AllocateRaw can reuse them.
            GcHeap.RebuildFreelist();
        }
    }
}
