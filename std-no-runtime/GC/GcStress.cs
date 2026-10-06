namespace SharpOS.Std.NoRuntime
{
    // GC stress: a collection before every N-th allocation, and the heap
    // walked end to end around every collection.
    //
    // A collector bug shows up only when a collection meets the code that
    // breaks it — a reference kept where the walk cannot see it, a frame the
    // decoder misreads. Ordinary runs collect rarely, so such a bug surfaces
    // late and far away (step189: four root bugs, each found one corrupted
    // block at a time). Collecting at every allocation makes every allocation
    // site a place the collector is checked.
    //
    // Before, not after, the allocation: the block being handed out is held
    // only by the allocator's own locals, which no walk reports.
    //
    // Same switch in the kernel and in apps; the host decides when it is on
    // and what a broken heap does (HeapBroken).
    public static unsafe class GcStress
    {
        /// <summary>Collect before every N-th allocation; 0 is off.</summary>
        public static uint Every;

        /// <summary>Collections this mode has run.</summary>
        public static ulong Collections;

        /// <summary>Walk the heap before each mark and after each sweep, stress or not.</summary>
        public static bool VerifyHeap;

        /// <summary>First block found broken, and when: 1 before a mark (the program broke it), 2 after a sweep (the collector did).</summary>
        public static nint FirstBroken;
        public static int BrokenPhase;

        /// <summary>Told about the first broken block. The kernel panics, an app ends.</summary>
        public static delegate*<nint, int, void> HeapBroken;

        private static uint s_countdown;
        private static bool s_collecting;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void BeforeAllocation()
        {
            if (s_collecting || GC.s_collectHook == null)
                return;
            if (GcHeap.s_allocationAllowed != null && !GcHeap.s_allocationAllowed())
                return;
            if (++s_countdown < Every)
                return;
            s_countdown = 0;

            s_collecting = true;
            GC.s_collectHook();
            Collections++;
            s_collecting = false;
        }

        internal static void Check(int phase)
        {
            if (FirstBroken != 0)
                return;
            nint broken = GcHeap.FindBrokenObject(GcMark.MethodTableLow, GcMark.MethodTableHigh);
            if (broken == 0)
                broken = GcHeap.FindBrokenFreeNode();
            if (broken == 0)
                return;
            FirstBroken = broken;
            BrokenPhase = phase;
            if (HeapBroken != null)
                HeapBroken(broken, phase);
        }
    }
}
