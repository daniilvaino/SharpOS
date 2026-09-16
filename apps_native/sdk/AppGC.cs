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
        private static uint s_stackRoots;
        private static uint s_afterStatics;

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
            uint afterStatics = GcMark.LastMarkedCount;

            s_stackRoots = 0;
            var walk = (delegate* unmanaged<nuint, void>)(nint)services->GcWalkRootsAddress;
            walk((nuint)(nint)(delegate* unmanaged<nuint, void>)&MarkRoot);
            s_afterStatics = afterStatics;

            GcSweep.Run();

            s_lastWalkOk = true;
            s_collections++;

            Report();
        }

        // Said at the moment it happens, not later from the idle loop: a
        // collection runs because an allocation just failed, and what comes
        // next is often the crash we are trying to explain. A report deferred
        // to the next loop iteration is a report that never arrives.
        //
        // Hence no string building — the allocator that failed is the same one
        // a string would ask. The line is assembled in a stack buffer and
        // handed to the diagnostic service as bytes.
        private static void Report()
        {
            byte* line = stackalloc byte[160];
            int n = 0;

            Put(line, ref n, "[appgc] collect #");
            PutUInt(line, ref n, s_collections);
            Put(line, ref n, " swept=");
            PutUInt(line, ref n, GcSweep.LastSweptCount);
            Put(line, ref n, " kept=");
            PutUInt(line, ref n, GcSweep.LastKeptCount);
            // Which half of root discovery produced the survivors. A
            // collection that keeps far less than the heap holds is either
            // missing static slots or getting nothing from the stack walk,
            // and one number cannot say which.
            Put(line, ref n, " slots=");
            PutUInt(line, ref n, (uint)GcRoots.Count);
            Put(line, ref n, " bystatics=");
            PutUInt(line, ref n, s_afterStatics);
            Put(line, ref n, " stackroots=");
            PutUInt(line, ref n, s_stackRoots);
            Put(line, ref n, " dropped=");
            PutUInt(line, ref n, GcMark.LastDroppedCount);
            Put(line, ref n, "\n");
            line[n] = 0;

            AppHost.WriteDiagnostic(line);
        }

        private static void Put(byte* buffer, ref int at, string text)
        {
            for (int i = 0; i < text.Length && at < 158; i++)
                buffer[at++] = (byte)text[i];
        }

        private static void PutUInt(byte* buffer, ref int at, uint value)
        {
            byte* digits = stackalloc byte[10];
            int count = 0;
            do
            {
                digits[count++] = (byte)('0' + (int)(value % 10));
                value /= 10;
            }
            while (value != 0);

            while (count > 0 && at < 158)
                buffer[at++] = digits[--count];
        }

        // Called back once per live root the kernel's walk discovers. The
        // pointer is checked by MarkFromRoot itself — out-of-heap values and
        // implausible method tables are dropped there, so a root belonging to
        // the kernel's heap simply does not match ours and is ignored.
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void MarkRoot(nuint value)
        {
            if (value == 0) return;
            s_stackRoots++;
            GcMark.MarkFromRoot((nint)value);
        }
    }
}
