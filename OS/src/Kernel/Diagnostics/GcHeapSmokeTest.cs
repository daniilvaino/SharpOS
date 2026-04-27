using OS.Hal;
using OS.Kernel.Memory;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Diagnostics
{
    // Smoke test for the managed GC heap. Allocates a handful of `new`
    // objects (object, int[], string), registers one as a GC root, then
    // runs a Collect to verify mark/sweep mechanics on a tiny graph.
    //
    // The s_keep* fields are intentional roots — without them the test's
    // local refs would be discarded and the alloc count check would be
    // racy with conservative scan.
    //
    // Invoked from Phase 4.
    internal static unsafe class GcHeapSmokeTest
    {
        // Module-level statics so allocations have a stable home — the
        // conservative stack scan would otherwise be the only thing
        // keeping them alive, which is too noisy for a smoke test.
        private static object s_keep1;
        private static object s_keep2;
        private static object s_keep3;

        public static void Run()
        {
            Log.Write(LogLevel.Info, "---- gc heap test begin ----");

            Log.Write(LogLevel.Info, "gc: new object()...");
            s_keep1 = new object();
            Log.Write(LogLevel.Info, "gc: new object() ok");

            Log.Write(LogLevel.Info, "gc: new int[5]...");
            s_keep2 = new int[5];
            Log.Write(LogLevel.Info, "gc: new int[5] ok");

            Log.Write(LogLevel.Info, "gc: new string(x,3)...");
            s_keep3 = new string('x', 3);
            Log.Write(LogLevel.Info, "gc: new string(x,3) ok");

            Log.Begin(LogLevel.Info);
            Console.Write("gc final: count=");
            Console.WriteULongRaw(GcHeap.AllocCount);
            Console.Write(" bytes=");
            Console.WriteULongRaw(GcHeap.AllocBytes);
            Log.EndLine();

            GcRoots.CaptureStackTop();
            GcRoots.Register(ref s_keep1);

            Log.Begin(LogLevel.Info);
            Console.Write("roots: registered=");
            Console.WriteUIntRaw((uint)GcRoots.Count);
            Console.Write(" stackTop=0x");
            Console.WriteHexRaw((ulong)GcRoots.StackTop, 16);
            Log.EndLine();

            KernelGC.Collect();

            Log.Begin(LogLevel.Info);
            Console.Write("mark: marked=");
            Console.WriteUIntRaw(GcMark.LastMarkedCount);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("sweep: kept=");
            Console.WriteUIntRaw(GcSweep.LastKeptCount);
            Console.Write(" swept=");
            Console.WriteUIntRaw(GcSweep.LastSweptCount);
            Log.EndLine();

            Log.Write(LogLevel.Info, "---- gc heap test end ----");
        }
    }
}
