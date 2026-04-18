namespace OS.Kernel.Memory
{
    internal unsafe struct HeapBlock
    {
        public uint Size;
        public uint IsFree;
        public HeapBlock* Next;
        public HeapBlock* Prev;
    }

    internal static unsafe class HeapBlockOps
    {
        public const uint MinimumSplitPayload = 16;

        public static uint HeaderSize => (uint)sizeof(HeapBlock);

        public static byte* GetPayload(HeapBlock* block)
        {
            return ((byte*)block) + HeaderSize;
        }

        public static HeapBlock* FromPayload(void* payload)
        {
            return (HeapBlock*)(((byte*)payload) - HeaderSize);
        }

        public static bool CanSplit(HeapBlock* block, uint requestedSize)
        {
            if (block == null)
                return false;

            if (block->Size <= requestedSize)
                return false;

            uint remainder = block->Size - requestedSize;
            return remainder > HeaderSize + MinimumSplitPayload;
        }

        public static HeapBlock* Split(HeapBlock* block, uint requestedSize)
        {
            uint remainder = block->Size - requestedSize;
            HeapBlock* splitBlock = (HeapBlock*)(GetPayload(block) + requestedSize);

            splitBlock->Size = remainder - HeaderSize;
            splitBlock->IsFree = 1;
            splitBlock->Prev = block;
            splitBlock->Next = block->Next;

            if (splitBlock->Next != null)
                splitBlock->Next->Prev = splitBlock;

            block->Size = requestedSize;
            block->Next = splitBlock;
            return splitBlock;
        }

        public static bool AreAdjacent(HeapBlock* left, HeapBlock* right)
        {
            if (left == null || right == null)
                return false;

            return (byte*)right == GetPayload(left) + left->Size;
        }
    }
}
