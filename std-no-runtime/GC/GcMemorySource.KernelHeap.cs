// GcMemorySource backed by the kernel's KernelHeap allocator — CoreLib's
// kernel tier (Std.Kernel.props). Apps get a different backend
// (GcMemorySource.AppStatic.cs: pages committed on demand, given back). The
// heap is the kernel's: KernelServices.HeapAlloc, installed once it is up.

namespace SharpOS.Std.NoRuntime
{
    internal static unsafe class GcMemorySource
    {
        public static bool IsInitialized => KernelServices.HeapAlloc != null;

        public static void* AllocateBlock(uint size)
        {
            delegate*<uint, void*> alloc = KernelServices.HeapAlloc;
            return alloc == null ? null : alloc(size);
        }

        /// <summary>The kernel's heap keeps its segments (it grows by collecting first, GcHeap.CollectBeforeGrowing).</summary>
        public static bool CanRelease => false;

        public static void ReleaseBlock(void* block, uint size) { }
    }
}
