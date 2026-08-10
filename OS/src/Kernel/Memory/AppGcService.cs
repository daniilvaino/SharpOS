namespace OS.Kernel.Memory
{
    // The stack-root walk, offered to a loaded app for its OWN collector.
    //
    // Why lend the walker instead of sharing the heap: an app is on its way to
    // being a process. Sharing a heap would mean one app's garbage becomes
    // everybody's pause, and a runaway app exhausts kernel memory — exactly the
    // isolation that SMP, preemption and async I/O will depend on. Sharing the
    // WALKER costs nothing: register spill, unwinding and GcInfo decoding are
    // expensive machinery that is already image-aware, and duplicating it into
    // every app would be two copies of the hardest code in the system.
    //
    // The app passes a callback; every live managed root the walk discovers is
    // handed to it, and it marks into its own heap. The kernel marks nothing
    // here and owns nothing afterwards.
    //
    // Called with the app's frames directly below ours on the same stack, which
    // is what makes this work at all: the walk starts here and unwinds straight
    // through the boundary into app code. That also fixes the boundary — a
    // collection can only happen while the app is inside a call to us, which is
    // precisely the safepoint discipline a preemptive scheduler will need.
    internal static unsafe class AppGcService
    {
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        public static void WalkRoots(nuint markCallback)
        {
            if (markCallback == 0) return;
            KernelGcPreciseWalk.RunFromCurrentFrame(
                (delegate* unmanaged<nuint, void>)markCallback);
        }
    }
}
