// Public GC API. Apps call `GC.Collect()` to run a full mark + sweep cycle.
//
// Kernel does NOT use this directly — it has its own wrapper that routes
// MarkAll through the register-spill trampoline (GcStackSpill). Apps don't
// have that trampoline, so they get a best-effort conservative scan that
// only sees references ILC has already spilled to the stack. This is the
// Cosmos-style contract and it's fine for our current app workloads.
//
// The order below matters:
//   1. Mark.Begin   — reset mark bits / counters
//   2. Roots.MarkAll — walk static roots + conservative stack scan
//   3. Sweep.Run    — flip unmarked to free-markers; RebuildFreelist is
//                     called at the tail of Sweep so the new free blocks
//                     are immediately available to the next AllocateRaw.

namespace SharpOS.Std.NoRuntime
{
    public static class GC
    {
        public static void Collect()
        {
            GcMark.Begin();
            GcRoots.MarkAll();
            GcSweep.Run();
        }
    }
}
