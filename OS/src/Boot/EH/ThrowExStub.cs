using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.Boot.EH
{
    // Hosts the [RuntimeExport] symbol RhpThrowEx. The body below is
    // overwritten at boot time by ThrowExPatcher with byte-array
    // shellcode that builds PAL_LIMITED_CONTEXT + ExInfo on stack and
    // tail-calls into managed RhpTest_ThrowIngress (Phase 1 step 5.1).
    //
    // Same patcher pattern as ByRefAssignRefStub / PortIoStub /
    // CaptureContextStub. Body is intentionally inflated with explicit
    // memory writes and Panic.Fail calls so it compiles to ~250+ bytes —
    // the shellcode is ~186 bytes, plus margin.
    //
    // Entry contract (Microsoft x64 ABI for ILC's `throw` codegen):
    //   RCX = exception object
    //   stack[0] = return address (pushed by CALL from throw site)
    internal static class ThrowExStub
    {
        [RuntimeExport("RhpThrowEx")]
        private static unsafe void RhpThrowEx(byte* exceptionAsBytes)
        {
            // Padding body — never executes when patched. Bunch of explicit
            // writes + Panic.Fail to force ILC to emit > 200 bytes.
            if (exceptionAsBytes == null)
                OS.Kernel.Panic.Fail("RhpThrowEx: null exception (stub not patched)");

            ulong* slot = (ulong*)exceptionAsBytes;
            slot[0] = 0;
            slot[1] = 0;
            slot[2] = 0;
            slot[3] = 0;
            slot[4] = 0;
            slot[5] = 0;
            slot[6] = 0;
            slot[7] = 0;
            slot[8] = 0;
            slot[9] = 0;
            slot[10] = 0;
            slot[11] = 0;
            slot[12] = 0;
            slot[13] = 0;
            slot[14] = 0;
            slot[15] = 0;
            slot[16] = 0;
            slot[17] = 0;
            slot[18] = 0;
            slot[19] = 0;
            slot[20] = 0;
            slot[21] = 0;
            slot[22] = 0;
            slot[23] = 0;
            slot[24] = 0;
            slot[25] = 0;
            slot[26] = 0;
            slot[27] = 0;
            slot[28] = 0;
            slot[29] = 0;
            slot[30] = 0;
            slot[31] = 0;

            OS.Kernel.Panic.Fail("RhpThrowEx: stub body executed — patcher did not run");
        }

        public static unsafe void* GetMethodAddress()
        {
            delegate*<byte*, void> fn = &RhpThrowEx;
            return (void*)fn;
        }
    }
}
