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
    //
    // Every other thread's stack too, not only the caller's. An app has
    // threads of its own (Terminal.Gui keeps input and resize loops on them),
    // and an object only one of those holds is as live as any other; walking
    // just the caller frees it while its thread still uses it, and the
    // freed block ends up in the free list under a live writer (step169,
    // AotTests "other thread's stack roots survive collect"). The kernel's
    // own collector has walked parked threads since the scheduler existed.
    // Threads of other programs and of the kernel are walked as well — their
    // roots point outside this app's heap, and the app's marker drops those.
    internal static unsafe class AppGcService
    {
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        public static void WalkRoots(nuint markCallback)
        {
            if (markCallback == 0) return;
            var markRoot = (delegate* unmanaged<nuint, void>)markCallback;
            KernelGcPreciseWalk.RunFromCurrentFrame(markRoot);
            KernelGC.MarkOtherThreadStacks(markRoot);
            Report();
        }

        // The walk's own account of itself, said where the app's collector
        // says its numbers — the two halves of one collection belong on one
        // screen. Every counter here is a place the walk stops without
        // saying so: a frame cap, a frame whose PC falls outside any
        // interruptible range, a slot table wider than the buffer. A stack
        // that ended because it ran out and a stack that ended because we
        // gave up are indistinguishable without them.
        private static void Report()
        {
            Put("[appwalk] frames=");
            PutInt(KernelGcPreciseWalk.LastFramesWalked);
            Put(" roots=");
            PutInt(KernelGcPreciseWalk.LastRootsMarked);
            Put(" unresolved=");
            PutInt(KernelGcPreciseWalk.LastFramesUnresolved);
            Put(" outofrange=");
            PutInt(KernelGcPreciseWalk.LastFramesSkippedOutOfRange);
            Put(" slotoverflow=");
            PutInt(KernelGcPreciseWalk.LastFramesSlotOverflow);
            Put(" capped=");
            PutInt(KernelGcPreciseWalk.LastFrameCapHits);
            Put("\n");
        }

        // Digits straight to the channel: this runs inside the app's
        // collection, and a formatter that allocates would ask a heap in the
        // middle of the one question it must not ask.
        private static void PutInt(int value)
        {
            uint v = (uint)value;
            char* digits = stackalloc char[10];
            int count = 0;
            do
            {
                digits[count++] = (char)('0' + (int)(v % 10));
                v /= 10;
            }
            while (v != 0);

            while (count > 0)
                OS.Hal.Platform.WriteChar(digits[--count], OS.Hal.OutputChannel.Perf);
        }

        private static void Put(string text)
        {
            for (int i = 0; i < text.Length; i++)
                OS.Hal.Platform.WriteChar(text[i], OS.Hal.OutputChannel.Perf);
        }
    }
}
