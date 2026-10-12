// GcSegment — contiguous heap region with bump allocator.
//
// Layout within a memory block returned by GcMemorySource:
//   [SegmentHeader (56 bytes)]
//   [object-start bitmap: one bit per 16 bytes of the object area]
//   [object data ... ] (from ObjectStart, 16-aligned)
//
// Next field forms a linked list of all segments in the heap (used by
// GcHeap for iteration and FindSegmentContaining).
//
// The bitmap (Starts) resolves interior pointers and conservative
// candidates to the object they fall in (GcHeap.FindObjectStart).

using System.Runtime.InteropServices;

namespace SharpOS.Std.NoRuntime
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct GcSegmentHeader
    {
        // Pointer to start of whole block (same as &this, useful for debugging/free)
        public nint Start;

        // First address available for object allocation (after this header).
        public nint ObjectStart;

        // End of segment (exclusive).
        public nint End;

        // Bump pointer: next allocation goes here.
        public nint Current;

        // Next segment in the heap linked list, or null for the tail.
        public GcSegmentHeader* Next;

        // One bit per 16 bytes of the object area: set where a live object
        // starts. Lies between this header and ObjectStart. The allocator
        // sets a bit for every object it hands out; the sweep recomputes the
        // bits it walks (live: set; dead or free: clear). The marker resolves
        // every candidate through it (GcHeap.FindObjectStart).
        public byte* Starts;

        // Bytes in Starts: a bit index past it is a bug, refused loudly.
        public nint StartsBytes;
    }
}
