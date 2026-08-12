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
        // Stop-the-world, single-CPU edition.
        //
        // On one core "stopping the world" is not a suspension protocol: only
        // one thread can be running, so every OTHER thread is already parked
        // with a stable context — which is exactly what the parked-stack walk
        // needs. The only thread that can move mid-collection is the collector
        // itself, and that is what suppression prevents.
        //
        // Nothing here waits for anything, so this cannot deadlock. SMP is a
        // different problem entirely: there the other cores really are running
        // and have to be stopped, which needs safepoints or an IPI.
        public static void CollectPrecise()
        {
            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                CollectPreciseCore();
            }
            finally
            {
                OS.Kernel.Threading.Preemption.Allow();
            }
        }

        private static void CollectPreciseCore()
        {
            GcMark.Begin();
            GcRoots.MarkStaticRootsOnly();
            KernelGcPreciseWalk.RunFromCurrentFrame();
            MarkOtherThreadStacks();
            GcSweep.Run();
        }

        // Roots on the stacks of threads that are not running.
        //
        // The running thread is already covered by RunFromCurrentFrame above.
        // Every other live thread — runnable but not scheduled, sleeping on
        // the timer queue, blocked on an event — has a stack full of locals
        // that reference live objects, and none of it was visible to the
        // collector before this. Cooperative scheduling made the hole
        // survivable rather than harmless: threads and collections simply
        // rarely met.
        //
        // Safe to walk while they are parked precisely because they are
        // parked: their saved context is stable until they are switched back
        // in, which cannot happen from inside a collection on this CPU.
        // Preemption changes that, and this is the piece it will need.
        private static void MarkOtherThreadStacks()
        {
            if (!KernelGcPreciseWalk.IsAvailable) return;

            OS.Kernel.Threading.Thread? current = OS.Kernel.Threading.Scheduler.Current;
            OS.Kernel.Threading.Thread? t = OS.Kernel.Threading.Scheduler.AllThreads;

            while (t != null)
            {
                if (t != current &&
                    t.State != OS.Kernel.Threading.ThreadState.Exited &&
                    t.ContextBlock != null)
                {
                    KernelGcPreciseWalk.RunFromParkedThread(t.ContextBlock, null);

                    // A preempted thread is parked inside the interrupt
                    // handler, and the walk above stops at the entry stub.
                    // Continue on the far side of it.
                    if (t.PreemptedFrame != null)
                        KernelGcPreciseWalk.RunFromInterruptFrame(t.PreemptedFrame, null);
                }

                t = t.AllNext;
            }
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
