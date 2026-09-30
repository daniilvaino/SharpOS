// Where atomic operations actually come from.
//
// The Interlocked in Threading.cs reads, compares and writes as three separate
// managed statements. That was honest while the whole system was cooperative:
// nothing could run between the read and the write, so nothing could be lost.
// Preemption ended that. A timer tick now lands anywhere, including between
// those two statements, and the classic lost update follows — which is a
// particularly nasty defect to own, because the primitive everyone reaches for
// to be safe is the one that is not.
//
// Neither tier can express `lock cmpxchg` in C#, and both already know how to
// get machine instructions: the kernel has X64Asm's emitted stubs, an app
// writes its own shellcode the way it does for the other helpers ILC demands.
// So this is the seam between them — the same shape as ThreadBackend, for the
// same reason.
//
// When no backend is installed the managed fallback stands. It is correct on a
// single thread and wrong the moment there are two, so Interlocked reports
// whether it is genuinely atomic rather than leaving callers to assume.

namespace System.Threading
{
    public static unsafe class AtomicBackend
    {
        private static delegate*<uint*, uint, uint, uint> s_cas32;
        private static delegate*<ulong*, ulong, ulong, ulong> s_cas64;
        private static delegate*<void> s_barrier;

        /// <summary>
        /// Whether compare-and-swap is a real instruction here. False means the
        /// managed fallback is in use: single-threaded correctness only.
        /// </summary>
        public static bool IsAtomic => s_cas32 != null && s_cas64 != null;

        public static void Install(
            delegate*<uint*, uint, uint, uint> compareExchange32,
            delegate*<ulong*, ulong, ulong, ulong> compareExchange64,
            delegate*<void> memoryBarrier)
        {
            s_cas32 = compareExchange32;
            s_cas64 = compareExchange64;
            s_barrier = memoryBarrier;
        }

        internal static uint CompareExchange32(uint* location, uint value, uint comparand)
        {
            if (s_cas32 != null) return s_cas32(location, value, comparand);

            uint original = *location;
            if (original == comparand) *location = value;
            return original;
        }

        internal static ulong CompareExchange64(ulong* location, ulong value, ulong comparand)
        {
            if (s_cas64 != null) return s_cas64(location, value, comparand);

            ulong original = *location;
            if (original == comparand) *location = value;
            return original;
        }

        internal static void Barrier()
        {
            if (s_barrier != null) s_barrier();
        }
    }
}
