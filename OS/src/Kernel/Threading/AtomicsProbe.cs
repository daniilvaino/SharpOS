using OS.Hal;

namespace OS.Kernel.Threading
{
    // Phase E3 gate — verifies the X64Asm byte-shellcode atomics produce
    // the documented semantics:
    //
    //   CmpXchg64(loc, value, comparand):
    //     - When *loc == comparand: swap, return comparand (the OLD value)
    //     - When *loc != comparand: no swap, return *loc (the OLD value)
    //   Xchg64(loc, value):
    //     - Always swap, return OLD *loc
    //   MemoryBarrier():
    //     - mfence; just verify no crash
    //
    // Stack-resident ulong is fine for single-thread; multi-core stress
    // belongs in E4+ when the scheduler exists. The probe is a regression
    // oracle for the shellcode bytes — if a future edit miswires the
    // ModR/M or the lock prefix, this catches it on the next boot.
    internal static unsafe class AtomicsProbe
    {
        public static void Run()
        {
            Log.Write(LogLevel.Info, "atomics probe start");

            ulong slot = 0;
            ulong* p = &slot;

            // 1. CmpXchg64 success case: *p == 5, swap to 10.
            slot = 5;
            ulong old1 = X64Asm.CmpXchg64(p, value: 10, comparand: 5);
            bool ok1 = (old1 == 5) && (slot == 10);

            // 2. CmpXchg64 failure case: *p == 10 (not 5), no swap.
            ulong old2 = X64Asm.CmpXchg64(p, value: 20, comparand: 5);
            bool ok2 = (old2 == 10) && (slot == 10);

            // 3. Xchg64: *p == 10 → 100. Should return 10.
            ulong old3 = X64Asm.Xchg64(p, value: 100);
            bool ok3 = (old3 == 10) && (slot == 100);

            // 4. MemoryBarrier: just verify it doesn't crash. Hard to test
            //    ordering effects single-threaded; suffices as smoke.
            X64Asm.MemoryBarrier();
            bool ok4 = true;

            bool allOk = ok1 && ok2 && ok3 && ok4;
            Log.Begin(allOk ? LogLevel.Info : LogLevel.Warn);
            Console.Write("atomics probe: ");
            Console.Write(ok1 ? "CmpXchg-hit=ok "  : "CmpXchg-hit=FAIL ");
            Console.Write(ok2 ? "CmpXchg-miss=ok " : "CmpXchg-miss=FAIL ");
            Console.Write(ok3 ? "Xchg=ok "         : "Xchg=FAIL ");
            Console.Write(ok4 ? "MFence=ok"        : "MFence=FAIL");
            Log.EndLine();
        }
    }
}
