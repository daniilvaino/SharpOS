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
        // HeapDiagnostics специально использует Console.*Raw варианты.
        // Причина: Console.WriteUInt/Hex в обычном режиме идёт через
        // NumberFormatting → FastAllocateString → KernelHeap.Alloc.
        // Это создаёт новый блок в том же linked-list, который мы сейчас
        // итерируем в DumpBlocks. Итерация бы не останавливалась.
        // Для DumpSummary строгого требования нет (не итерирует), но
        // используем Raw для консистентности и честной "снимок не
        // перевирается после вывода".

        public static void DumpSummary()
        {
            KernelHeap.GetSummary(out HeapSummary summary);

            Log.Begin(LogLevel.Info);
            Console.Write("heap pages: ");
            Console.WriteUIntRaw(summary.HeapPages);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("heap blocks: ");
            Console.WriteUIntRaw(summary.BlockCount);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("heap used bytes: ");
            Console.WriteULongRaw(summary.UsedBytes);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("heap free bytes: ");
            Console.WriteULongRaw(summary.FreeBytes);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("heap ops alloc/free: ");
            Console.WriteUIntRaw(summary.AllocCount);
            Console.Write("/");
            Console.WriteUIntRaw(summary.FreeCount);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("heap grow/coalesce/fail: ");
            Console.WriteUIntRaw(summary.GrowCount);
            Console.Write("/");
            Console.WriteUIntRaw(summary.CoalesceCount);
            Console.Write("/");
            Console.WriteUIntRaw(summary.AllocFailures);
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
                Console.WriteUIntRaw(index);
                Console.Write(": ");
                Console.Write("addr=0x");
                Console.WriteHexRaw((ulong)block, 8);
                Console.Write(" size=");
                Console.WriteUIntRaw(block->Size);
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
