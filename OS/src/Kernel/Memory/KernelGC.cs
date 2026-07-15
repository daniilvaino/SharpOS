using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Memory
{
    // Kernel-side wrapper for GC.Collect. Differs from std's public
    // SharpOS.Std.NoRuntime.GC.Collect by routing MarkAll through the
    // register-spill trampoline (GcStackSpill), so callee-saved register
    // roots are visible to the conservative stack scan.
    //
    // If the trampoline wasn't installed (early boot, exec buffer missing)
    // we fall back to the plain MarkAll — less precise, still functional.
    internal static unsafe class KernelGC
    {
        // Default Collect: use the precise walker (step 110) when its
        // infrastructure is up — .pdata mounted, ExecStubBuffer claimed
        // GcContextSpill at offset 512. Falls back to the legacy
        // conservative path (GcStackSpill register-push + ScanStack
        // bottom-up dereference) for very early boot when those aren't
        // wired yet. The conservative path remains compiled in because
        // KernelHeapSmokeTest and the unit GcStressTest invoke Collect
        // from CaptureStackTop-bounded callers where it's intentionally
        // safe.
        public static void Collect()
        {
            if (KernelGcPreciseWalk.IsAvailable)
            {
                CollectPrecise();
                return;
            }

            GcMark.Begin();
            if (GcStackSpill.IsInitialized)
            {
                delegate* unmanaged<void> markFn = &GcRoots.MarkAllUnmanaged;
                GcStackSpill.Invoke(markFn);
            }
            else
            {
                GcRoots.MarkAll();
            }
            GcSweep.Run();
        }

        // Step 110 Part 8 — precise alternative to Collect(). Replaces the
        // conservative ScanStack (which dereferences any stack qword that
        // happens to fall inside a GcHeap segment, with the wild-walker
        // bit-flip risk documented in §10 of kernel-limits doc) with a
        // per-frame walk driven by NativeAOT's precise GcInfo blobs.
        //
        // Pipeline (Parts 1-7):
        //   1. GcContextSpill shellcode captures current GP regs + RSP + RIP
        //   2. KernelGcPreciseWalk loops frames via SehUnwind.VirtualUnwind
        //   3. Per frame: CoffGcInfoDecoder gives live tracked + untracked
        //      slot indices; CoffGcInfoResolver gives each slot's pointer
        //      value; MarkFromRoot processes them with its existing range
        //      and MT sanity checks.
        //
        // Safe to call from any context (no CaptureStackTop dance needed)
        // because precise enumeration never deref's stack words it doesn't
        // already know are managed slots.
        public static void CollectPrecise()
        {
            GcMark.Begin();
            GcRoots.MarkStaticRootsOnly();
            KernelGcPreciseWalk.RunFromCurrentFrame();
            GcSweep.Run();
        }

        // Conservative-only collect: ALWAYS spill every register to the stack
        // (GcStackSpill) and scan conservatively — never the precise walker.
        // The precise walker enumerates only the slots ILC's GcInfo marks live,
        // and it does not reliably recover a root that lives in a callee-saved
        // register at the collect callsite (write-barrier probe: local `h` kept
        // in rbx got swept). Conservative over-marks (may retain a few dead
        // objects) but NEVER under-marks — the safe choice for System.GC.
        // Collect() from BCL code, where a swept live local is a correctness
        // bug. Routed here via GC.s_collectHook (installed in BootSequence).
        public static void CollectConservative()
        {
            GcMark.Begin();
            if (GcStackSpill.IsInitialized)
            {
                delegate* unmanaged<void> markFn = &GcRoots.MarkAllUnmanaged;
                GcStackSpill.Invoke(markFn);
            }
            else
            {
                GcRoots.MarkAll();
            }
            GcSweep.Run();
        }
    }
}
