// Iterative mark phase for our Serial GC.
//
// Algorithm: depth-first traversal with explicit mark stack.
// Ported from Kevin Gosse's GCHeap.Mark.cs (MIT):
//   https://github.com/kevingosse/ManagedDotnetGC
//
// Differences from Gosse:
//   - No managed Stack<T> — we have fixed-size nint buffer in .bss
//     (sized at compile time via [StructLayout(Size = N)]).
//   - No Action<IntPtr> delegate — use managed function pointer
//     `delegate*<nint, void>` passed to GcObject.EnumerateObjectReferences.
//   - Only scans our GcHeap segments. Pointers outside are skipped
//     (safe for conservative root scanning: random stack values that
//     happen to look like pointers but point outside the heap are ignored).
//
// Not yet: finalization queue, weak handles, dependent handles, SyncBlock.
// These are intentionally out of scope for our single-threaded non-generational GC.

using System.Runtime.InteropServices;

namespace SharpOS.Std.NoRuntime
{
    [StructLayout(LayoutKind.Sequential, Size = GcMark.StackCapacity * 8)]
    internal struct GcMarkStackStorage { }

    internal static unsafe class GcMark
    {
        public const int StackCapacity = 4096;  // 4K entries × 8 bytes = 32 KB in .bss

        private static GcMarkStackStorage s_stack;
        private static int s_count;

        // Diagnostics
        private static uint s_markedCount;

        public static uint LastMarkedCount => s_markedCount;

        // Start a fresh mark pass. Caller should call this before pushing the
        // first root; it resets the mark stack and the per-pass counter.
        public static void Begin()
        {
            s_count = 0;
            s_markedCount = 0;
        }

        // Push a root pointer and drain the mark stack until empty. Safe to
        // call multiple times (each root traversal is transitive, but shared
        // mark bits prevent double-work).
        public static void MarkFromRoot(nint rootPtr)
        {
            if (rootPtr == 0)
                return;

            Push(rootPtr);
            Drain();
        }

        private static void Drain()
        {
            while (s_count > 0)
            {
                nint ptr = Pop();
                GcObject* obj = (GcObject*)ptr;

                if (obj == null)
                    continue;

                // Only mark objects inside our heap. Pointers into .rdata,
                // kernel structs, or arbitrary stack values are skipped.
                if (GcHeap.FindSegmentContaining(ptr) == null)
                    continue;

                if (obj->IsMarked())
                    continue;

                obj->Mark();
                s_markedCount++;

                GcObject.EnumerateObjectReferences(obj, &Push);
            }
        }

        // Push callback used by EnumerateObjectReferences. Must match the
        // delegate*<nint, void> signature.
        private static void Push(nint ptr)
        {
            if (s_count >= StackCapacity)
                return; // stack overflow — silently drop (should not happen in practice)

            fixed (GcMarkStackStorage* basePtr = &s_stack)
            {
                nint* slots = (nint*)basePtr;
                slots[s_count] = ptr;
                s_count++;
            }
        }

        private static nint Pop()
        {
            s_count--;
            fixed (GcMarkStackStorage* basePtr = &s_stack)
            {
                nint* slots = (nint*)basePtr;
                return slots[s_count];
            }
        }

        // Clear all mark bits in the heap. Walks all segments and all objects
        // in each segment.
        //
        // SAFETY: assumes every block in a segment is a valid GcObject with
        // a readable MethodTable. Breaks out of the segment walk if we
        // encounter a zero-sized block (would cause infinite loop) or a
        // block that runs past the segment end. This protects against
        // corrupted heap walkability (e.g. raw AllocateRaw blocks mixed in).
        public static void UnmarkAllObjects()
        {
            GcSegmentHeader* seg = GcHeap.FirstSegment;
            while (seg != null)
            {
                nint p = seg->ObjectStart;
                nint end = seg->Current;

                while (p < end)
                {
                    GcObject* o = (GcObject*)p;
                    if (o->MethodTable == null)
                        break; // unreadable MT → abort this segment walk

                    uint size = o->ComputeSize();
                    if (size == 0)
                        break; // would loop forever

                    if (o->IsMarked())
                        o->Unmark();

                    // Align to next 16-byte boundary (matches GcHeap.ObjectAlignment)
                    size = (size + 15u) & ~15u;
                    p += (nint)size;
                }

                seg = seg->Next;
            }
        }
    }
}
