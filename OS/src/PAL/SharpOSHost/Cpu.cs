using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel;

namespace OS.PAL.SharpOSHost
{
    // L3 CPU primitives — Win32 names → SharpOSHost mapping:
    //   FlushInstructionCache    → SharpOSHost_FlushICache   (JIT writes code → instructions must be visible)
    //   FlushProcessWriteBuffers → SharpOSHost_MemoryBarrier (full mfence; GC barrier sync)
    //
    // Phase 6.1.a: JIT path uses FlushInstructionCache после code emission.
    // GC uses FlushProcessWriteBuffers для cross-CPU coherence (single
    // thread case — может стать no-op, но для correctness keep mfence).
    //
    // Implementation = direct intrinsics:
    //   FlushICache → clflush loop OR wbinvd OR sfence (depending on arch)
    //   MemoryBarrier → mfence (or System.Threading.Interlocked.MemoryBarrier)
    internal static unsafe class SharpOSHostCpu
    {
        [RuntimeExport("SharpOSHost_FlushICache")]
        public static void FlushICache(void* address, ulong size)
        {
            Panic.Fail("SharpOSHost_FlushICache not implemented (Phase 6.1.a)");
        }

        [RuntimeExport("SharpOSHost_MemoryBarrier")]
        public static void MemoryBarrier()
        {
            Panic.Fail("SharpOSHost_MemoryBarrier not implemented (Phase 6.1.a)");
        }

        // The cache entries of GetLogicalProcessorInformation. The fork lays
        // them out as SYSTEM_LOGICAL_PROCESSOR_INFORMATION; which caches there
        // are is decided here (OS.Hal.CpuCaches). Each record is a Windows
        // CACHE_DESCRIPTOR, 12 bytes: Level, Associativity, LineSize (u16),
        // Size (u32), Type (u32). Returns the number of records written.
        [RuntimeExport("SharpOSHost_GetCacheDescriptors")]
        public static int GetCacheDescriptors(byte* records, int capacity)
        {
            if (records == null || capacity <= 0)
                return 0;

            OS.Hal.CacheInfo* caches = stackalloc OS.Hal.CacheInfo[OS.Hal.CpuCaches.Max];
            int count = OS.Hal.CpuCaches.Read(caches, OS.Hal.CpuCaches.Max);
            if (count > capacity)
                count = capacity;

            for (int i = 0; i < count; i++)
            {
                byte* r = records + i * 12;
                r[0] = caches[i].Level;
                r[1] = caches[i].Ways;
                *(ushort*)(r + 2) = caches[i].LineSize;
                *(uint*)(r + 4) = caches[i].Size;
                *(uint*)(r + 8) = (uint)caches[i].Kind;
            }
            return count;
        }
    }
}
