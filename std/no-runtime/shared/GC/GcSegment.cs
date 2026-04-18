// GcSegment — contiguous heap region with bump allocator.
//
// Layout within a memory block returned by GcMemorySource:
//   [SegmentHeader (40 bytes)]
//   [object data ... ]
//
// Next field forms a linked list of all segments in the heap (used by
// GcHeap for iteration and FindSegmentContaining).
//
// Brick table (for conservative stack scanning / interior pointer
// resolution) is not included in phase 2 — added in phase 3 when Mark
// needs it.

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
    }
}
