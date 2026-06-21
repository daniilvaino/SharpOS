using OS.Hal;

namespace OS.Boot.EH
{
    // Phase 1 step 8 — patches CallFilterFuncletStub.RhpCallFilterFunclet
    // body with byte-array shellcode (~111 bytes).
    //
    // Differences vs catch/finally funclet:
    //   - 3 args (RCX=ex, RDX=filter IP, R8=REGDISPLAY*).
    //   - Filter called с RCX=establisher SP, RDX=exception.
    //   - Returns RAX = bool result (0/1) which we PRESERVE through epilogue.
    //   - Normal return; no non-local transfer.
    //   - No write-back of nonvols (filters are predicates, no side effects).
    //   - No ExInfo head pop (no throw consumption — filter just decides match).
    //
    // STACK LAYOUT after prologue (8 push'ей + sub rsp, 0x28):
    //
    //   Entry:                  RSP % 16 == 8.
    //   After 8 pushes (0x40):  RSP % 16 still == 8.
    //   After sub rsp, 0x28:    RSP % 16 == 0. ✓ aligned for next CALL.
    //
    //   [rsp + 0x00 .. 0x1F]    shadow space for filter call (0x20)
    //   [rsp + 0x20 .. 0x27]    saved filter IP (RDX entry value)
    //
    // Total bytes: 12 (push) + 7 (sub) + 5 (save filter IP) + 56 (restore nonvols)
    //              + 3 (rdx<-rcx) + 4 (rcx<-establisher) + 4 (call)
    //              + 7 (add rsp) + 12 (pops) + 1 (ret) = 111 bytes.
    internal static unsafe partial class CallFilterFuncletPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)CallFilterFuncletStub.GetMethodAddress();
            if (target == null) return false;

            // step 118 Wave 2 — compile-time codegen (BootAsm.Generator).
            // 0 holes (all args via Win64 ABI: rcx=ex, rdx=filterIP, r8=rd*).
            int p = Emit(target);

            if (target[0] != 0x41 || target[p - 1] != 0xC3)
                return false;

            Log.Begin(LogLevel.Info);
            Console.Write("call-filter-funclet shellcode: bytes=");
            Console.WriteUIntRaw((uint)p);
            Log.EndLine();

            s_installed = true;
            return true;
        }
    }
}
