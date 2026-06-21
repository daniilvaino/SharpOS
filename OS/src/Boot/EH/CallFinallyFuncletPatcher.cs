using OS.Hal;

namespace OS.Boot.EH
{
    // Phase 1 step 7 — patches CallFinallyFuncletStub.RhpCallFinallyFunclet
    // body with byte-array shellcode (~174 bytes). Same patcher pattern as
    // CallCatchFuncletPatcher.
    //
    // Differences vs catch funclet:
    //   - 2 args (RCX=handler IP, RDX=REGDISPLAY*) vs 4 (catch has exception + ExInfo*).
    //   - Handler called с RCX=establisher SP only (no exception arg).
    //   - Handler returns NORMALLY; no `mov rsp; jmp rax` non-local transfer.
    //   - After handler returns, нонволы могут быть changed by finally — write
    //     them BACK в REGDISPLAY pointers так что catch / next finally /
    //     continuation видит updated values.
    //   - No ExInfo head pop — throw not consumed by finally.
    //
    // STACK LAYOUT after prologue (8 push'ей + sub rsp, 0x38):
    //
    //   Entry:                  RSP % 16 == 8.
    //   After 8 pushes (0x40):  RSP % 16 still == 8.
    //   After sub rsp, 0x38:    RSP % 16 == 0. ✓ aligned for next CALL.
    //
    //   [rsp + 0x00 .. 0x1F]    shadow space for handler call (0x20)
    //   [rsp + 0x20 .. 0x27]    saved handler IP    (RCX entry value)
    //   [rsp + 0x28 .. 0x2F]    saved REGDISPLAY*   (RDX entry value)
    //   [rsp + 0x30 .. 0x37]    alignment pad
    //
    // Total bytes: 12 (push) + 7 (sub) + 10 (save args) + 56 (restore nonvols)
    //              + 4 (rcx setup) + 4 (call) + 5 (reload rdx) + 56 (writeback)
    //              + 7 (add rsp) + 12 (pops) + 1 (ret) = 174 bytes.
    internal static unsafe partial class CallFinallyFuncletPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)CallFinallyFuncletStub.GetMethodAddress();
            if (target == null) return false;

            // step 118 Wave 2 — compile-time codegen (BootAsm.Generator).
            // 0 holes (all args via REGDISPLAY pointer in rdx).
            int p = Emit(target);

            if (target[0] != 0x41 || target[p - 1] != 0xC3)
                return false;

            Log.Begin(LogLevel.Info);
            Console.Write("call-finally-funclet shellcode: bytes=");
            Console.WriteUIntRaw((uint)p);
            Log.EndLine();

            s_installed = true;
            return true;
        }
    }
}
