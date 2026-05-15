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
        // When true, GcSweep.Run() is a no-op: unmarked objects are NOT
        // converted to free markers and the freelist is not rebuilt, so no
        // block is ever reused. Set this once a foreign managed runtime
        // (CoreCLR) starts allocating into the kernel GcHeap: its objects are
        // reachable only from ITS GC graph, which the kernel Mark phase does
        // not scan — so they look dead here and sweep would clobber them
        // (observed: AppContext.s_dataStore zeroed mid-run). The kernel arena
        // is bump-backed; disabling reclamation just forgoes reuse, which is
        // acceptable until CoreCLR is fully off the kernel heap (plan 5b).
        public static bool ReclamationDisabled;

        public static void Collect()
        {
            GcMark.Begin();
            GcRoots.MarkAll();
            GcSweep.Run();
        }
    }
}
