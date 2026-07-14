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
    }
}
