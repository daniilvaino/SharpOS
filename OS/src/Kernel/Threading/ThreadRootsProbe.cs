using OS.Hal;
using OS.Kernel.Memory;

namespace OS.Kernel.Threading
{
    // Does a collection preserve objects that only a PARKED thread holds?
    //
    // The worker allocates an array, keeps the only reference to it in a
    // local, and yields. While it is parked, the main thread collects. If the
    // collector cannot see the worker's stack, that array is unreachable to
    // the marker, gets swept, and its memory is handed out again — the worker
    // wakes up holding a pointer to something else.
    //
    // This failed before step153's F2: GcRoots tracked a single stack top and
    // the walker started from the calling frame, so every other thread's roots
    // were invisible. Cooperative scheduling made it survivable rather than
    // harmless — threads and collections rarely met, and when they did the
    // damage appeared later, somewhere else.
    //
    // The pattern is checked element by element rather than by identity: a
    // swept-then-reused block usually still looks like an array, so "the
    // reference is non-null" proves nothing. The contents are what a sweep
    // would destroy.
    internal static unsafe class ThreadRootsProbe
    {
        private const int Elements = 64;
        private const byte Seed = 0x5C;

        private static bool s_workerRan;
        private static bool s_survived;
        private static int s_firstBadIndex = -1;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WorkerEntry()
        {
            // The ONLY reference to this array. Not a field, not a static —
            // if it is reachable from anywhere but this frame, the probe
            // proves nothing.
            byte[] held = new byte[Elements];
            for (int i = 0; i < Elements; i++) held[i] = (byte)(Seed + i);

            // Park. The main thread collects while we are here.
            Scheduler.Yield();

            s_workerRan = true;
            s_survived = held.Length == Elements;
            for (int i = 0; i < Elements && s_survived; i++)
            {
                if (held[i] != (byte)(Seed + i))
                {
                    s_survived = false;
                    s_firstBadIndex = i;
                }
            }

            Scheduler.Exit();
        }

        public static void Run()
        {
            s_workerRan = false;
            s_survived = false;
            s_firstBadIndex = -1;

            delegate* unmanaged<void> entry = &WorkerEntry;
            Thread? worker = Scheduler.Spawn(entry, 16 * 4096);
            if (worker == null)
            {
                Console.WriteLine("[gcroots] SKIP could not spawn worker");
                return;
            }

            // Let the worker allocate and park.
            Scheduler.Yield();

            // Collect with the worker's array reachable from nothing but its
            // parked stack.
            KernelGC.Collect();

            // Then take the freed space back.
            //
            // Without this the probe cannot fail: sweeping marks a block free
            // but does not erase it, so a wrongly-collected array still reads
            // back correctly until something else is handed that memory. This
            // churn is what turns "was it collected" from an invisible event
            // into a visible one.
            for (int i = 0; i < 64; i++)
            {
                byte[] filler = new byte[Elements];
                for (int j = 0; j < Elements; j++) filler[j] = 0xFF;
                if (filler[0] != 0xFF) return;      // keeps the loop live
            }

            // Let the worker wake and check its own data.
            Scheduler.Yield();

            if (!s_workerRan)
            {
                Console.WriteLine("[gcroots] SKIP worker never resumed");
                return;
            }

            if (s_survived)
            {
                Console.WriteLine("[gcroots] PASS parked-thread roots survive collect");
                return;
            }

            Console.Write("[gcroots] FAIL parked-thread array corrupted at index ");
            Console.WriteInt(s_firstBadIndex);
            Console.WriteLine("");
        }
    }
}
