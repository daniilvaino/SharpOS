using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.Boot.EH
{
    // Hosts the [RuntimeExport] symbol RhpCallCatchFunclet. Body is
    // overwritten at boot time by CallCatchFuncletPatcher with byte-array
    // shellcode (~140 bytes). Managed body intentionally inflated с 50+
    // explicit memory writes + Panic.Fail to compile to >250 bytes —
    // shellcode patcher needs comfortable margin.
    //
    // Entry contract (Microsoft x64 ABI, called by managed dispatcher
    // when first-pass found a typed catch handler):
    //   RCX = exception object
    //   RDX = handler funclet IP
    //   R8  = REGDISPLAY*
    //   R9  = ExInfo*
    // [rsp] = return address (caller is dispatcher; never used because
    //         our shellcode does non-local mov rsp; jmp rax).
    //
    // Behavior:
    //   1. Spill caller's nonvols (8 pushes).
    //   2. Save 4 args on stack.
    //   3. Restore parent frame's nonvols from REGDISPLAY (indirect:
    //      mov rax, [r8+pX]; mov reg, [rax]).
    //   4. Setup handler call: RCX = REGDISPLAY.SP (establisher frame
    //      = parent's stack pointer at try entry), RDX = exception.
    //   5. CALL handler. Returns RAX = resume IP (continuation point
    //      after entire try/catch in parent method).
    //   6. Pop ExInfo head: s_head = exInfo->PrevExInfo (single-step
    //      baseline для 5.5a; multi-entry walk for collided unwind
    //      постпоненted to 5.7+).
    //   7. mov rsp, REGDISPLAY.SP; jmp rax — non-local transfer to
    //      parent method's continuation IP. Discards entire shellcode
    //      frame + caller's frame.
    //
    // SKIPPED in 5.5a (sage 1+2 confirmed safe for single-thread
    // freestanding):
    //   - Thread* fetch / TLS access.
    //   - m_threadAbortException check.
    //   - DoNotTriggerGc lock-and.
    //   - INLINE_THREAD_UNHIJACK.
    //   - RhpValidateExInfoPop (debug only).
    //   - ThreadAbort rethrow path.
    //   - XMM6-XMM15 restore (no SAVE_XMM128 codes empirically observed).
    //
    // For full stock-compatible version (5.5b+), add XMM restore + add
    // multi-entry head pop + add stack-walker hooks.
    internal static class CallCatchFuncletStub
    {
        [RuntimeExport("RhpCallCatchFunclet")]
        [UnmanagedCallersOnly(EntryPoint = "RhpCallCatchFunclet")]
        private static unsafe void CallCatchFunclet(
            byte* exception, byte* handlerIp, byte* regDisplay, byte* exInfo)
        {
            // Padding — never executes when patched. ~40 ulong writes
            // + Panic.Fail to ensure ILC emits > 200 bytes for shellcode
            // patcher's 140-byte overwrite + safety margin.
            if (regDisplay == null)
                OS.Kernel.Panic.Fail("RhpCallCatchFunclet: null rd (stub not patched)");

            ulong* slot = (ulong*)regDisplay;
            slot[0] = 0; slot[1] = 0; slot[2] = 0; slot[3] = 0;
            slot[4] = 0; slot[5] = 0; slot[6] = 0; slot[7] = 0;
            slot[8] = 0; slot[9] = 0; slot[10] = 0; slot[11] = 0;
            slot[12] = 0; slot[13] = 0; slot[14] = 0; slot[15] = 0;
            slot[16] = 0; slot[17] = 0; slot[18] = 0; slot[19] = 0;
            slot[20] = 0; slot[21] = 0; slot[22] = 0; slot[23] = 0;
            slot[24] = 0; slot[25] = 0; slot[26] = 0; slot[27] = 0;
            slot[28] = 0; slot[29] = 0; slot[30] = 0; slot[31] = 0;
            slot[32] = 0; slot[33] = 0; slot[34] = 0; slot[35] = 0;
            slot[36] = 0; slot[37] = 0; slot[38] = 0; slot[39] = 0;

            OS.Kernel.Panic.Fail("RhpCallCatchFunclet: stub body executed — patcher did not run");
        }

        public static unsafe void* GetMethodAddress()
        {
            delegate* unmanaged<byte*, byte*, byte*, byte*, void> fn = &CallCatchFunclet;
            return (void*)fn;
        }
    }
}
