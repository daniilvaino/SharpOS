// GcMemorySource backed by the kernel's KernelHeap allocator.
// Linked into OS.csproj only. Apps get a different backend (static pool).

namespace SharpOS.Std.NoRuntime
{
    internal static unsafe class GcMemorySource
    {
        public static bool IsInitialized => OS.Kernel.Memory.KernelHeap.IsInitialized;

        public static void* AllocateBlock(uint size)
        {
            if (!OS.Kernel.Memory.KernelHeap.IsInitialized)
                return null;
            return OS.Kernel.Memory.KernelHeap.Alloc(size);
        }
    }
}
