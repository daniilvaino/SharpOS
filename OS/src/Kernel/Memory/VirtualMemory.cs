using OS.Hal;
using OS.Kernel.Paging;

namespace OS.Kernel.Memory
{
    // SharpOS unified VM manager — true reserve≠commit (Phase 6.2 GC-arena).
    //
    // Demand-mapped: Reserve hands out a contiguous VA window slice with NO
    // physical backing; Commit pulls frames from PhysicalMemory and maps them
    // into the ACTIVE (firmware) PML4 via X64PageTable.MapKernel. This is what
    // CoreCLR's region GC expects — a large cheap VA reservation, lazily
    // committed per region. (Old path routed VirtualAlloc→kernel GcHeap which
    // commit-on-alloc'd everything → degenerate gc_heap → IsHeapPointer=false
    // → Monitor spin. This replaces it.)
    //
    // VA window: LOWER canonical half (bit 63 = 0). CoreCLR/GC is a user-mode
    // runtime — its write-barrier / card-table / JIT codegen assume heap
    // pointers live in the lower half (Linux/Windows ASLR never sets bit 63).
    // A higher-half window (0xFFFF9000…) gets 32-bit-truncated + sign-extended
    // by those paths → non-canonical → #GP. Lower half matches every real
    // CoreCLR deployment. PML4[160], clear of identity RAM (0..0x2000_0000),
    // kernel image (~0x1C00_0000), pager-test (PML4[64]), DirectMapBase.
    //
    // RetainVM=true semantics: Decommit/Release are no-ops (PhysicalMemory is
    // bump-only — no frame reclaim anyway; keeping mappings is correct &
    // simplest). Single-threaded: no locking.
    //
    // Demand-fault completion: the region GC touches its reserved RegionRange
    // (region metadata, card / brick / mark tables) at a commit granularity
    // that does NOT line up 1:1 with the Win32 VirtualCommit calls our shim
    // forwards — it leans on OS lazy/overcommit semantics for those spans. So
    // a NOT-present #PF whose CR2 lands inside [WindowBase, WindowBase+
    // WindowSize) is treated as a lazy commit: TryDemandCommit backs that one
    // page and the fault handler IRETQ-resumes the faulting instruction. This
    // is strictly scoped to the window VA range — any #PF outside it still
    // panics exactly as before (targeted, not a global #PF policy change). It
    // realizes the "lazily committed per region" design stated above.
    internal static unsafe class VirtualMemory
    {
        private const ulong PageSize   = 0x1000UL;
        private const ulong WindowBase = 0x0000500000000000UL;
        private const ulong WindowSize = 0x100000000UL;          // 4 GiB VA

        private static ulong s_cursor = WindowBase;
        private static ulong s_reservedBytes;
        private static ulong s_committedBytes;
        private static uint  s_commitFails;
        private static uint  s_faultCommits;

        public static ulong ReservedBytes  => s_reservedBytes;
        public static ulong CommittedBytes => s_committedBytes;
        public static uint  FaultCommits   => s_faultCommits;

        // True iff `va` lies inside the demand-mapped VM window.
        public static bool InWindow(ulong va)
            => va >= WindowBase && va < WindowBase + WindowSize;

        // #PF (not-present) demand path: back the single 4 KiB page covering
        // `faultVa` with a frame and map it RW (executable — NX is globally
        // off, so JIT-code pages in the window resolve here too). Returns
        // false if `faultVa` is outside the window, the page is already
        // mapped (→ genuine protection fault, let the panic path handle it),
        // or no frame is available (true OOM). Caller IRETQ-resumes on true.
        public static bool TryDemandCommit(ulong faultVa)
        {
            if (!InWindow(faultVa)) return false;
            ulong p = faultVa & ~(PageSize - 1);
            if (X64PageTable.TryQueryKernel(p, out _, out _))
                return false;                                  // already mapped

            ulong pa = global::OS.Kernel.PhysicalMemory.AllocPage();
            if (pa == 0) return false;                         // true OOM

            if (!X64PageTable.MapKernel(p, pa, PageFlags.Present | PageFlags.Writable))
            {
                // Single-threaded: the only MapKernel failure here is an
                // intermediate-table alloc failure (OOM) — already not mapped.
                if (!X64PageTable.TryQueryKernel(p, out _, out _)) return false;
            }
            X64PageTable.FlushTlbAll();
            s_committedBytes += PageSize;
            s_faultCommits++;
            return true;
        }

        private static ulong RoundUp(ulong v, ulong a) => (v + (a - 1)) & ~(a - 1);

        // Reserve `size` bytes of VA, `alignment`-aligned (>=4K). No backing.
        // Returns VA or 0 on window exhaustion / bad args.
        public static void* Reserve(ulong size, ulong alignment)
        {
            if (size == 0) return null;
            ulong align = alignment < PageSize ? PageSize : alignment;
            ulong va = RoundUp(s_cursor, align);
            ulong end = va + RoundUp(size, PageSize);
            if (end > WindowBase + WindowSize) return null;   // window exhausted
            s_cursor = end;
            s_reservedBytes += (end - va);
            return (void*)va;
        }

        // Commit [va, va+size): back each 4K page with a physical frame and
        // map it RW (+NX unless exec) into the active PML4. Idempotent —
        // already-mapped pages are skipped (GC re-commits sub-ranges).
        public static bool Commit(void* address, ulong size, bool exec)
        {
            if (address == null || size == 0) return false;
            ulong va  = (ulong)address & ~(PageSize - 1);
            ulong end = ((ulong)address + size + PageSize - 1) & ~(PageSize - 1);

            PageFlags flags = PageFlags.Present | PageFlags.Writable;
            if (!exec) flags |= PageFlags.NoExecute;

            for (ulong p = va; p < end; p += PageSize)
            {
                if (X64PageTable.TryQueryKernel(p, out _, out _))
                    continue;                                  // already committed

                ulong pa = global::OS.Kernel.PhysicalMemory.AllocPage();
                if (pa == 0) { s_commitFails++; X64PageTable.FlushTlbAll(); return false; }

                if (!X64PageTable.MapKernel(p, pa, flags))
                {
                    // Lost a race with an existing mapping — tolerate.
                    if (!X64PageTable.TryQueryKernel(p, out _, out _))
                    { s_commitFails++; X64PageTable.FlushTlbAll(); return false; }
                }
                s_committedBytes += PageSize;
            }
            X64PageTable.FlushTlbAll();
            return true;
        }

        // RetainVM=true: keep mappings (PhysicalMemory has no reclaim anyway).
        public static bool Decommit(void* address, ulong size) { _ = address; _ = size; return true; }
        public static bool Release(void* address, ulong size)  { _ = address; _ = size; return true; }

        // Map a specific VA→PA range (PE sections, MMIO). flags via exec.
        public static bool MapFixed(void* va, ulong pa, ulong size, bool exec)
        {
            if (va == null || size == 0) return false;
            ulong v   = (ulong)va & ~(PageSize - 1);
            ulong end = ((ulong)va + size + PageSize - 1) & ~(PageSize - 1);
            ulong phys = pa & ~(PageSize - 1);
            PageFlags flags = PageFlags.Present | PageFlags.Writable;
            if (!exec) flags |= PageFlags.NoExecute;
            for (ulong p = v; p < end; p += PageSize, phys += PageSize)
            {
                if (X64PageTable.TryQueryKernel(p, out _, out _)) continue;
                if (!X64PageTable.MapKernel(p, phys, flags))
                { X64PageTable.FlushTlbAll(); return false; }
            }
            X64PageTable.FlushTlbAll();
            return true;
        }

        // Change protection of an already-committed range.
        public static bool Protect(void* address, ulong size, bool exec, bool write)
        {
            if (address == null || size == 0) return false;
            ulong va  = (ulong)address & ~(PageSize - 1);
            ulong end = ((ulong)address + size + PageSize - 1) & ~(PageSize - 1);
            PageFlags pf = PageFlags.Present;
            if (write) pf |= PageFlags.Writable;
            if (!exec) pf |= PageFlags.NoExecute;
            for (ulong p = va; p < end; p += PageSize)
                X64PageTable.TrySetKernelFlagsEx(p, pf, PageFlags.Present, out _);
            X64PageTable.FlushTlbAll();
            return true;
        }

        // Boot self-test: reserve 2 pages, commit RW, write a pattern across
        // the page boundary, read it back. Proves Reserve/Commit/MapKernel +
        // TLB flush actually produce live, writable mappings before CoreCLR
        // depends on them. Returns true on success.
        public static bool SelfTest()
        {
            void* r = Reserve(2 * PageSize, PageSize);
            if (r == null) return false;
            if (!Commit(r, 2 * PageSize, exec: false)) return false;

            ulong* q = (ulong*)r;
            uint n = (uint)((2 * PageSize) / sizeof(ulong));
            for (uint i = 0; i < n; i++) q[i] = 0xA5A5_0000UL + i;
            for (uint i = 0; i < n; i++)
                if (q[i] != 0xA5A5_0000UL + i) return false;
            return true;
        }
    }
}
