using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.Boot.EH
{
    // Hosts the [RuntimeExport] symbol RhpCallFinallyFunclet. Body is
    // overwritten at boot time by CallFinallyFuncletPatcher with byte-array
    // shellcode (~174 bytes).
    //
    // ILC's DispatchEx (managed) calls this between first-pass и catch
    // invocation, ONCE per finally clause covering the throw site between
    // throwing frame и handlingFrameSP.
    //
    // Microsoft x64 ABI:
    //   RCX = handler funclet IP (finally body)
    //   RDX = REGDISPLAY*
    //   [rsp] = return address
    //
    // Funclet ABI passed to handler (via shellcode):
    //   RCX = establisher SP (= REGDISPLAY.SP)
    //
    // Behavior:
    //   1. Spill our 8 nonvols.
    //   2. Restore parent's nonvols FROM REGDISPLAY (via pNonvol indirection).
    //   3. Call handler (RCX=establisher SP).
    //   4. Write back nonvols TO REGDISPLAY (so any modifications by finally
    //      persist for the catch / next finally / continuation).
    //   5. Standard epilogue (no non-local transfer — finallys return normally).
    internal static class CallFinallyFuncletStub
    {
        [RuntimeExport("RhpCallFinallyFunclet")]
        [UnmanagedCallersOnly(EntryPoint = "RhpCallFinallyFunclet")]
        private static unsafe void RhpCallFinallyFunclet(byte* handlerIp, RegDisplay* regDisplay)
        {
            // Padding body — never executes when patched.
            if (handlerIp == null || regDisplay == null)
                OS.Kernel.Panic.Fail("RhpCallFinallyFunclet: stub body executed (patcher did not run)");

            byte* p = handlerIp;
            ulong sink = 0;
            for (int i = 0; i < 32; i++) sink ^= p[i];
            if (sink == 0xDEADBEEF)
                OS.Kernel.Panic.Fail("RhpCallFinallyFunclet: stub body executed");
        }

        public static unsafe void* GetMethodAddress()
        {
            delegate* unmanaged<byte*, RegDisplay*, void> fn = &RhpCallFinallyFunclet;
            return (void*)fn;
        }
    }
}
