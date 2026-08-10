using SharpOS.Std.NoRuntime;

namespace SharpOS.AppSdk
{
    // The app's own precise collector.
    //
    // Apps have their own heap (GcMemorySource.AppStatic) and, until now, no
    // collector at all: allocation bumped a cursor through a fixed pool and
    // nothing was ever reclaimed. Anything allocating per frame — an emulator,
    // a game loop — ran until the pool was gone.
    //
    // What is OURS: the heap, the mark bits, the sweep. An app is on its way to
    // being a process, and a process does not share a heap with the kernel:
    // one app's garbage would become everyone's pause, and a runaway app would
    // exhaust kernel memory.
    //
    // What is BORROWED: the stack-root walk. Register spill, PE unwinding and
    // GcInfo decoding are the hardest code in the system and are already
    // image-aware, so the kernel lends it through the service table
    // (GcWalkRootsAddress) and we hand it a callback that marks into our heap.
    //
    // Precise, never conservative: guessing that a stack word "looks like a
    // pointer" retains garbage by accident and is a poor foundation for the
    // preemptive, multi-core world this is heading towards.
    internal static unsafe class AppGC
    {
        private static bool s_installed;
        private static uint s_collections;
        private static bool s_lastWalkOk;

        public static uint Collections => s_collections;
        public static bool LastWalkOk => s_lastWalkOk;

        /// <summary>True when the kernel offered a root walker to borrow.</summary>
        public static bool IsAvailable
        {
            get
            {
                AppServiceTable* services = AppRuntime.Services;
                return services != null && services->GcWalkRootsAddress != 0;
            }
        }

        /// <summary>
        /// Route System.GC.Collect() here. Safe to call more than once.
        /// </summary>
        public static void Install()
        {
            if (s_installed) return;
            s_installed = true;
            GC.s_collectHook = &Collect;
        }

        /// <summary>
        /// Mark from static roots and the live stack, then sweep our heap.
        ///
        /// Refuses to run without the walker rather than falling back to
        /// something approximate: a collection that cannot see the stack frees
        /// live objects, and the damage surfaces far from here.
        /// </summary>
        public static void Collect()
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null || services->GcWalkRootsAddress == 0)
            {
                s_lastWalkOk = false;
                return;
            }

            GcMark.Begin();
            GcRoots.MarkStaticRootsOnly();

            var walk = (delegate* unmanaged<nuint, void>)(nint)services->GcWalkRootsAddress;
            walk((nuint)(nint)(delegate* unmanaged<nuint, void>)&MarkRoot);

            GcSweep.Run();

            s_lastWalkOk = true;
            s_collections++;
        }

        // Called back once per live root the kernel's walk discovers. The
        // pointer is checked by MarkFromRoot itself — out-of-heap values and
        // implausible method tables are dropped there, so a root belonging to
        // the kernel's heap simply does not match ours and is ignored.
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void MarkRoot(nuint value)
        {
            if (value == 0) return;
            GcMark.MarkFromRoot((nint)value);
        }
    }
}
