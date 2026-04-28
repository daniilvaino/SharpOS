using System.Runtime;
using System.Runtime.InteropServices;
using OS.Hal;

namespace OS.Boot.EH
{
    // Phase 1 step 5.5a — test harness for RhpCallCatchFunclet smoke.
    //
    // Two patched stubs:
    //   1. TestCatchHandler  — fake "catch funclet". Receives (RCX=SP, RDX=ex)
    //      from RhpCallCatchFunclet, writes flags + captures rcx/rdx into
    //      observable statics, returns continuation IP в RAX.
    //   2. TestContinuation  — fake "resume IP target". Entered via jmp rax
    //      (no return address). Writes flag + observed RSP, then tail-jumps
    //      to managed Probe5_5_PrintResults для readable output, then halt.
    //
    // Per sage 2's recommendation: shellcode stubs for both, не managed C#.
    // Reason — funclet ABI args are non-standard (RCX=SP not exception);
    // continuation entered via jmp without return address; using shellcode
    // avoids any unintended managed prologue/epilogue interaction.

    // ── Test catch handler ────────────────────────────────────────────
    //
    // Shellcode (54 bytes):
    //   mov r10, &s_handler_called            ; 49 BA <imm64>   (10)
    //   mov qword ptr [r10], 0xAAAA           ; 49 C7 02 AA AA 00 00  (7)
    //   mov r10, &s_observed_rcx              ; 49 BA <imm64>   (10)
    //   mov [r10], rcx                        ; 49 89 0A        (3)
    //   mov r10, &s_observed_rdx              ; 49 BA <imm64>   (10)
    //   mov [r10], rdx                        ; 49 89 12        (3)
    //   mov rax, &TestContinuation_entry      ; 48 B8 <imm64>   (10)
    //   ret                                   ; C3              (1)
    internal static class TestCatchHandlerStub
    {
        [RuntimeExport("RhpTest_5_5_CatchHandler")]
        [UnmanagedCallersOnly(EntryPoint = "RhpTest_5_5_CatchHandler")]
        private static unsafe byte* CatchHandler(ulong establisherSP, byte* exception)
        {
            // Padding body — must be > 54 bytes when patched.
            if (exception == null) OS.Kernel.Panic.Fail("test catch handler: null ex");
            ulong* slot = (ulong*)exception;
            slot[0] = 0; slot[1] = 0; slot[2] = 0; slot[3] = 0;
            slot[4] = 0; slot[5] = 0; slot[6] = 0; slot[7] = 0;
            slot[8] = 0; slot[9] = 0; slot[10] = 0; slot[11] = 0;
            OS.Kernel.Panic.Fail("test catch handler: stub not patched");
            return null;
        }

        public static unsafe void* GetMethodAddress()
        {
            delegate* unmanaged<ulong, byte*, byte*> fn = &CatchHandler;
            return (void*)fn;
        }
    }

    // ── Test continuation ─────────────────────────────────────────────
    //
    // Shellcode (42 bytes):
    //   mov r10, &s_continuation_called       ; 49 BA <imm64>   (10)
    //   mov qword ptr [r10], 0xBBBB           ; 49 C7 02 BB BB 00 00  (7)
    //   mov r10, &s_observed_rsp              ; 49 BA <imm64>   (10)
    //   mov [r10], rsp                        ; 49 89 22        (3)
    //   mov rax, &Probe5_5_PrintResults        ; 48 B8 <imm64>   (10)
    //   call rax                              ; FF D0           (2)
    //
    // NOTE: `call rax`, NOT `jmp rax`. PrintResults is managed function;
    // Win64 ABI assumes entry rsp%16==8 (CALL pushes 8-byte return addr).
    // If we used `jmp rax`, entry rsp%16==0 (REGDISPLAY.SP is 16-aligned),
    // PrintResults' prolog rolls misalignment forward, first CALL inside
    // hits movaps on misaligned stack → #GP. CALL fixes by pushing.
    // PrintResults halts in body — never reaches ret, so faked return
    // address never used.
    internal static class TestContinuationStub
    {
        [RuntimeExport("RhpTest_5_5_Continuation")]
        [UnmanagedCallersOnly(EntryPoint = "RhpTest_5_5_Continuation")]
        private static unsafe void Continuation()
        {
            // Padding — must be > 42 bytes when patched.
            // Placeholder writes to a known-valid pointer is unsafe здесь,
            // since this method может never be called normally. Just halt.
            OS.Kernel.Panic.Fail("test continuation: stub not patched");
            // Unreachable:
            for (int i = 0; i < 100; i++) { /* pad to ensure body size */ }
        }

        public static unsafe void* GetMethodAddress()
        {
            delegate* unmanaged<void> fn = &Continuation;
            return (void*)fn;
        }
    }

    // ── Patcher ───────────────────────────────────────────────────────
    //
    // Patches both shellcode stubs at install. Three placeholder addresses
    // patched in:
    //   - TestCatchHandler:  &s_handler_called, &s_observed_rcx,
    //                        &s_observed_rdx, &TestContinuation_entry.
    //   - TestContinuation:  &s_continuation_called, &s_observed_rsp,
    //                        &Probe5_5_PrintResults.
    internal static unsafe class Stub5_5_Patcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall(
            byte** addrHandlerCalled,
            byte** addrObservedRcx,
            byte** addrObservedRdx,
            byte** addrContinuationCalled,
            byte** addrObservedRsp,
            void* printResultsFn)
        {
            if (s_installed) return true;

            byte* handlerStub = (byte*)TestCatchHandlerStub.GetMethodAddress();
            byte* continuationStub = (byte*)TestContinuationStub.GetMethodAddress();
            if (handlerStub == null || continuationStub == null) return false;

            WriteHandler(handlerStub, addrHandlerCalled, addrObservedRcx, addrObservedRdx,
                         continuationStub);

            WriteContinuation(continuationStub, addrContinuationCalled, addrObservedRsp,
                              printResultsFn);

            // Sanity: first bytes look right.
            if (handlerStub[0] != 0x49 || continuationStub[0] != 0x49)
                return false;

            Log.Begin(LogLevel.Info);
            Console.Write("5.5a test harness: handler=0x");
            Console.WriteHexRaw((ulong)(nuint)handlerStub, 16);
            Console.Write(" continuation=0x");
            Console.WriteHexRaw((ulong)(nuint)continuationStub, 16);
            Log.EndLine();

            s_installed = true;
            return true;
        }

        private static void WriteHandler(byte* dst,
            byte** addrHandlerCalled, byte** addrObservedRcx, byte** addrObservedRdx,
            byte* continuationEntry)
        {
            int p = 0;

            // mov r10, &s_handler_called
            dst[p++] = 0x49; dst[p++] = 0xBA;
            WriteU64(dst, ref p, (ulong)(nuint)addrHandlerCalled);
            // mov qword ptr [r10], 0xAAAA  (49 C7 02 AA AA 00 00 — sign-extended imm32 to 64-bit)
            dst[p++] = 0x49; dst[p++] = 0xC7; dst[p++] = 0x02;
            dst[p++] = 0xAA; dst[p++] = 0xAA; dst[p++] = 0x00; dst[p++] = 0x00;

            // mov r10, &s_observed_rcx
            dst[p++] = 0x49; dst[p++] = 0xBA;
            WriteU64(dst, ref p, (ulong)(nuint)addrObservedRcx);
            // mov [r10], rcx
            dst[p++] = 0x49; dst[p++] = 0x89; dst[p++] = 0x0A;

            // mov r10, &s_observed_rdx
            dst[p++] = 0x49; dst[p++] = 0xBA;
            WriteU64(dst, ref p, (ulong)(nuint)addrObservedRdx);
            // mov [r10], rdx
            dst[p++] = 0x49; dst[p++] = 0x89; dst[p++] = 0x12;

            // mov rax, &TestContinuation_entry
            dst[p++] = 0x48; dst[p++] = 0xB8;
            WriteU64(dst, ref p, (ulong)(nuint)continuationEntry);
            // ret
            dst[p++] = 0xC3;
        }

        private static void WriteContinuation(byte* dst,
            byte** addrContinuationCalled, byte** addrObservedRsp,
            void* printResultsFn)
        {
            int p = 0;

            // mov r10, &s_continuation_called
            dst[p++] = 0x49; dst[p++] = 0xBA;
            WriteU64(dst, ref p, (ulong)(nuint)addrContinuationCalled);
            // mov qword ptr [r10], 0xBBBB
            dst[p++] = 0x49; dst[p++] = 0xC7; dst[p++] = 0x02;
            dst[p++] = 0xBB; dst[p++] = 0xBB; dst[p++] = 0x00; dst[p++] = 0x00;

            // mov r10, &s_observed_rsp
            dst[p++] = 0x49; dst[p++] = 0xBA;
            WriteU64(dst, ref p, (ulong)(nuint)addrObservedRsp);
            // mov [r10], rsp
            dst[p++] = 0x49; dst[p++] = 0x89; dst[p++] = 0x22;

            // mov rax, &Probe5_5_PrintResults
            dst[p++] = 0x48; dst[p++] = 0xB8;
            WriteU64(dst, ref p, (ulong)(nuint)printResultsFn);
            // call rax  (NOT jmp — see header comment about ABI alignment)
            dst[p++] = 0xFF; dst[p++] = 0xD0;
        }

        private static void WriteU64(byte* dst, ref int p, ulong val)
        {
            dst[p++] = (byte)(val);
            dst[p++] = (byte)(val >> 8);
            dst[p++] = (byte)(val >> 16);
            dst[p++] = (byte)(val >> 24);
            dst[p++] = (byte)(val >> 32);
            dst[p++] = (byte)(val >> 40);
            dst[p++] = (byte)(val >> 48);
            dst[p++] = (byte)(val >> 56);
        }
    }
}
