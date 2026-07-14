using System.Runtime;
using System.Runtime.InteropServices;
using OS.Boot;
using OS.Hal;
using OS.Kernel;

namespace OS.PAL.SharpOSHost
{
    // step 72 / Frontier-B root fix.
    //
    // CoreCLR's Thread::SetStackLimits caches m_CacheStackBase/Limit from
    // Thread::GetStackUpperBound()/GetStackLowerBound(). Under TARGET_SHARPOS
    // GetStackLowerBound() takes the Windows-style branch:
    //     ClrVirtualQuery(&local,&local,…) → return mbi.AllocationBase;
    // and SharpOS's VirtualQuery is a fake stub that sets
    // AllocationBase = the queried address. So m_CacheStackLimit became
    // &local at SetStackLimits time — an address near the STACK TOP, not
    // the true lower bound. Consequences (all observed):
    //   * stackwalk.cpp:974 (`newSP >= GetCachedStackLimit()`) fires for
    //     every frame deeper than that early frame — the chronic assert
    //     storm.
    //   * the GC stack-root scan / unwind sanity range
    //     [m_CacheStackLimit, m_CacheStackBase] collapses to a sliver near
    //     the top, so live OBJECTREFs that live only in deep reflection
    //     frames fall outside it → stale/garbage objref → object.cpp:255
    //     MethodTable validate → CLR FailFast (the System.Text.Json
    //     reflection-mode crash; subsumes the step-71 stack-band symptom).
    //
    // SharpOS is a unikernel: the whole system runs on the single
    // UEFI-provided boot stack, which lives entirely inside one UEFI
    // memory-map region (identity-mapped low memory — the same VA range
    // CoreClrProbe.SetupTebFacade uses via &local). That region's
    // [PhysicalStart, PhysicalStart + PageCount*4096) is the authoritative,
    // contiguous, guaranteed-mapped bound that contains every frame. We
    // return it as [limit (low), base (high)]. __chkstk is already a no-op
    // on SharpOS (ChkstkStub/Patcher), and nothing dereferences `limit`
    // itself (it is only a comparison bound; GC/unwind walk from the live
    // SP upward), so a generous region-sized window is correct and safe.
    internal static unsafe class SharpOSHostStackBounds
    {
        // out: *outBase = stack TOP (high addr), *outLimit = stack BOTTOM
        // (low addr). On failure leaves *both* zero so the fork-side caller
        // falls back to its previous behavior (weak no-op default keeps the
        // fork linkable if this export is absent).
        [RuntimeExport("SharpOSHost_GetStackBounds")]
        public static void GetStackBounds(ulong* outBase, ulong* outLimit)
        {
            if (outBase != null) *outBase = 0;
            if (outLimit != null) *outLimit = 0;
            if (outBase == null || outLimit == null) return;

            int marker = 0;
            ulong sp = (ulong)&marker;

            // Phase E9 -- if the calling thread is a hosted thread spawned
            // by Scheduler with its own allocated stack, return the TRUE
            // stack range (StackBase..StackTop). Without this every hosted
            // thread inherits the bounds of whatever large UEFI usable
            // memory region its allocated stack happens to fall inside
            // (often also the source of heap pages), and CoreCLR's
            // m_CacheStackLimit ends up hundreds of MB below the actual
            // stack base -- a #PF inside
            // HasStarted/SetStackLimits when probing bytes beyond the
            // real stack but inside the bogus huge "stack" range. Verify
            // the current SP actually lies inside [StackBase, StackTop)
            // before trusting; falls through to the BigStack/UEFI scan
            // otherwise (e.g. boot thread calling into CoreCLR).
            {
                OS.Kernel.Threading.Thread? curr = OS.Kernel.Threading.Scheduler.Current;
                if (curr != null && curr.StackBase != null && curr.StackTop != null)
                {
                    ulong lo = (ulong)curr.StackBase;
                    ulong hi = (ulong)curr.StackTop;
                    if (sp >= lo && sp < hi)
                    {
                        *outBase  = hi;   // TOP  (high)
                        *outLimit = lo;   // BOTTOM (low)
                        return;
                    }
                }
            }

            // Frontier-C: when CoreCLR runs on the BigStack buffer, the
            // memory-map region containing it can be much larger than the
            // dedicated stack allocation. The region heuristic below would
            // hand CoreCLR a bogus giant "stack", disabling its SO guard so
            // a too-deep recursion silently runs off the buffer. Return the
            // TRUE active buffer bounds so m_CacheStackLimit is the real
            // buffer bottom (clean, detectable SO at the limit instead of
            // silent corruption).
            if (OS.Kernel.Memory.BigStack.TryGetActiveBounds(sp, out ulong bsLo, out ulong bsHi))
            {
                *outBase = bsHi;     // TOP  (high)
                *outLimit = bsLo;    // BOTTOM (low)
                return;
            }

            BootInfo bi = Platform.GetBootInfo();
            MemoryRegion* regions = bi.MemoryMap.Regions;
            uint count = bi.MemoryMap.RegionCount;
            if (regions == null || count == 0) return;

            for (uint i = 0; i < count; i++)
            {
                ulong start = regions[i].PhysicalStart;
                ulong end = start + regions[i].PageCount * 0x1000UL;
                if (sp >= start && sp < end)
                {
                    *outBase = end;     // TOP  (high address)
                    *outLimit = start;  // BOTTOM (low address)
                    return;
                }
            }
            // Not found → leave 0/0; caller keeps its old path.
        }
    }
}
