using OS.Hal;
using System.Threading;

namespace OS.Kernel.Threading
{
    // Hands std's Interlocked the real instructions.
    //
    // The managed fallback in AtomicBackend reads, compares and writes as three
    // statements, which a timer tick can land in the middle of. X64Asm has had
    // the genuine article since Phase E3 — this is only the wiring, and it must
    // run before anything that locks.
    internal static unsafe class AtomicBackendInstaller
    {
        public static void Install()
        {
            AtomicBackend.Install(&CompareExchange32, &CompareExchange64, &Barrier);
        }

        private static uint CompareExchange32(uint* location, uint value, uint comparand)
            => X64Asm.CmpXchg32(location, value, comparand);

        private static ulong CompareExchange64(ulong* location, ulong value, ulong comparand)
            => X64Asm.CmpXchg64(location, value, comparand);

        private static void Barrier() => X64Asm.MemoryBarrier();
    }
}
