using OS.Hal;
using OS.Kernel.Memory;

namespace OS.Kernel.Diagnostics
{
    // Smoke test for KernelHeap (block allocator backing kernel-managed
    // raw allocations). Runs a few alloc/free patterns and dumps the
    // heap summary + block list for inspection.
    //
    // Invoked from Phase 1 right after KernelHeap.Init succeeds.
    internal static unsafe class KernelHeapSmokeTest
    {
        public static void Run()
        {
            void* alloc16 = KernelHeap.Alloc(16);
            PrintHeapAddress("heap alloc 16", alloc16);

            void* alloc64 = KernelHeap.Alloc(64);
            PrintHeapAddress("heap alloc 64", alloc64);

            void* alloc256 = KernelHeap.Alloc(256);
            PrintHeapAddress("heap alloc 256", alloc256);

            if (alloc16 != null)
            {
                KernelHeap.Free(alloc16);
                PrintHeapAddress("heap free", alloc16);
            }

            void* alloc8 = KernelHeap.Alloc(8);
            PrintHeapAddress("heap alloc 8", alloc8);

            HeapDiagnostics.DumpSummary();
            HeapDiagnostics.DumpBlocks();
        }

        private static void PrintHeapAddress(string label, void* pointer)
        {
            Log.Begin(LogLevel.Info);
            Console.Write(label);
            Console.Write(" -> ");

            if (pointer == null)
            {
                Console.Write("none");
            }
            else
            {
                Console.Write("0x");
                Console.WriteHex((ulong)pointer, 8);
            }
            Log.EndLine();
        }
    }
}
