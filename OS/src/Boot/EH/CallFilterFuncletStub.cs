using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.Boot.EH
{
    // Hosts the [RuntimeExport] symbol RhpCallFilterFunclet. Body is
    // overwritten at boot time by CallFilterFuncletPatcher with byte-array
    // shellcode (~111 bytes).
    //
    // ILC's DispatchEx (managed) calls this from FindFirstPassHandler when
    // it encounters a Filter clause covering the current code offset:
    // shellcode invokes the user predicate; predicate returns 0 or 1; FFPH
    // treats nonzero как "match" и picks the clause's catch handler.
    //
    // Microsoft x64 ABI:
    //   RCX = exception object
    //   RDX = filter funclet IP (predicate body)
    //   R8  = REGDISPLAY*
    //   [rsp] = return address
    //
    // Funclet ABI passed to filter:
    //   RCX = establisher SP (= REGDISPLAY.SP)
    //   RDX = exception object
    //   Returns: RAX = 0 (no match) or 1 (match) — bool result.
    //
    // No nonvol write-back: filter funclets shouldn't have side effects
    // (CLR semantics — filters are predicates). Matches stock NativeAOT.
    internal static class CallFilterFuncletStub
    {
        [RuntimeExport("RhpCallFilterFunclet")]
        private static unsafe int RhpCallFilterFunclet(byte* exceptionPtr, byte* filterIp, RegDisplay* regDisplay)
        {
            // Padding body — never executes when patched.
            if (exceptionPtr == null || filterIp == null || regDisplay == null)
                OS.Kernel.Panic.Fail("RhpCallFilterFunclet: stub body executed (patcher did not run)");

            byte* p = filterIp;
            ulong sink = 0;
            for (int i = 0; i < 32; i++) sink ^= p[i];
            if (sink == 0xDEADBEEF)
                OS.Kernel.Panic.Fail("RhpCallFilterFunclet: stub body executed");
            return 0;
        }

        public static unsafe void* GetMethodAddress()
        {
            delegate*<byte*, byte*, RegDisplay*, int> fn = &RhpCallFilterFunclet;
            return (void*)fn;
        }
    }
}
