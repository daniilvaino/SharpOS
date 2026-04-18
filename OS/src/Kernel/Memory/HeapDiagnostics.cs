using OS.Hal;

namespace OS.Kernel.Memory
{
    internal struct HeapSummary
    {
        public bool IsInitialized;
        public uint HeapPages;
        public uint BlockCount;
        public ulong UsedBytes;
        public ulong FreeBytes;
        public uint AllocCount;
        public uint FreeCount;
        public uint GrowCount;
        public uint CoalesceCount;
        public uint AllocFailures;
    }

    internal static unsafe class HeapDiagnostics
    {
        public static void DumpSummary()
        {
            KernelHeap.GetSummary(out HeapSummary summary);

            Log.Begin(LogLevel.Info);
            Console.Write("heap pages: ");
            Console.WriteUInt(summary.HeapPages);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("heap blocks: ");
            Console.WriteUInt(summary.BlockCount);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("heap used bytes: ");
            Console.WriteULong(summary.UsedBytes);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("heap free bytes: ");
            Console.WriteULong(summary.FreeBytes);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("heap ops alloc/free: ");
            Console.WriteUInt(summary.AllocCount);
            Console.Write("/");
            Console.WriteUInt(summary.FreeCount);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("heap grow/coalesce/fail: ");
            Console.WriteUInt(summary.GrowCount);
            Console.Write("/");
            Console.WriteUInt(summary.CoalesceCount);
            Console.Write("/");
            Console.WriteUInt(summary.AllocFailures);
            Log.EndLine();
        }

        public static void DumpBlocks()
        {
            Log.Write(LogLevel.Info, "heap blocks dump begin");

            HeapBlock* block = KernelHeap.FirstBlock;
            uint index = 0;
            while (block != null)
            {
                Log.Begin(LogLevel.Info);
                Console.Write("heap block ");
                Console.WriteUInt(index);
                Console.Write(": ");
                Console.Write("addr=0x");
                Console.WriteHex((ulong)block, 8);
                Console.Write(" size=");
                Console.WriteUInt(block->Size);
                Console.Write(" state=");
                Console.Write(block->IsFree != 0 ? "free" : "used");
                Log.EndLine();

                block = block->Next;
                index++;
            }

            Log.Write(LogLevel.Info, "heap blocks dump end");
        }
    }
}
