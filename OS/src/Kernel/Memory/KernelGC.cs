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
        public static void Collect()
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
