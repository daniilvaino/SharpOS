// Public GC API. `GC.Collect()` runs a full mark + sweep cycle.
//
// Both the kernel and apps install a collector in s_collectHook, and Collect
// routes there: the kernel's KernelGC (precise GcInfo walk, or the
// register-spill trampoline GcStackSpill for a conservative pass), an app's
// AppGC (static roots + the kernel's precise stack walk, lent through the
// service table). The fallback below, used only before a hook is installed,
// is a best-effort conservative scan of the current stack.
//
// The order below matters:
//   1. Mark.Begin   — reset mark bits / counters
//   2. Roots.MarkAll — walk static roots + conservative stack scan
//   3. Sweep.Run    — flip unmarked to free-markers, joining runs of
//                     adjacent ones into a single block and linking each
//                     into the free list as the walk goes, so the space is
//                     immediately available to the next AllocateRaw.

namespace SharpOS.Std.NoRuntime
{
    public static unsafe class GC
    {
        // Kernel-registered collect override. The plain Collect() below walks
        // only static roots + a conservative scan of the CURRENT stack — it
        // CANNOT see a root that lives in a callee-saved register at the
        // GC.Collect callsite (e.g. a local `h` the JIT kept in rbx). The
        // kernel's KernelGC.Collect routes through the GcStackSpill trampoline
        // (pushes all registers to the stack first) / the precise walker, so it
        // sees register roots. The kernel installs it at boot; any BCL code that
        // calls System.GC.Collect() then gets the robust path. Layering-safe: a
        // raw function pointer, no std->OS reference. Null before install ->
        // fall back to the best-effort scan (apps without the trampoline).
        public static delegate*<void> s_collectHook;

        // Allocation-sampling hook: called with the requested size on every
        // AllocSampleEvery-th successful allocation, so a profiler can look at
        // the stack and see who asked. Null and zero by default — apps and
        // ordinary kernel runs pay one comparison per allocation.
        //
        // Triggered by allocation rather than by the timer on purpose: small
        // allocations are cheap, so a CPU profile of a machine that allocates
        // steadily while idle shows the idling, not the allocating.
        public static delegate*<uint, void> s_allocSampleHook;
        public static uint AllocSampleEvery;


        // When true, GcSweep.Run() is a no-op: unmarked objects are NOT
        // converted to free markers and the freelist is not rebuilt, so no
        // block is ever reused. A guard for objects reachable only from a
        // graph the kernel Mark phase does not scan (a foreign runtime's
        // allocations in this heap would look dead and be clobbered). Off:
        // CoreCLR's native blobs live in NativeArena, not here, and nothing
        // in the tree sets it to true.
        public static bool ReclamationDisabled;

        public static void Collect()
        {
            if (s_collectHook != null)
            {
                s_collectHook();
                return;
            }
            GcMark.Begin();
            GcRoots.MarkAll();
            GcSweep.Run();
        }
    }
}
