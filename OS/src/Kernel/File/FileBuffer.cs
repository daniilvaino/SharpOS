using OS.Kernel.Util;

namespace OS.Kernel.File
{
    internal readonly unsafe struct FileBuffer
    {
        public readonly void* Pointer;
        public readonly uint Length;

        public FileBuffer(void* pointer, uint length)
        {
            Pointer = pointer;
            Length = length;
        }

        public bool IsValid => Pointer != null && Length != 0;

        public MemoryBlock AsMemoryBlock()
        {
            return new MemoryBlock(Pointer, Length);
        }
    }
}
