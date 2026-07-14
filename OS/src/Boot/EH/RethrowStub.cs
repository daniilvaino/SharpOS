using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.Boot.EH
{
    // Hosts the [RuntimeExport] symbol RhpRethrow. Body is overwritten
    // at boot time by RethrowPatcher with byte-array shellcode (~186
    // bytes, similar to RhpThrowEx but with kind = Throw|Rethrow flag).
    //
    // ILC emits `call RhpRethrow` для IL `throw;` instructions inside
    // catch handlers. Entry contract (Microsoft x64 ABI):
    //   RCX = exception object (rethrown from catch parameter)
    //   [rsp] = return address
    //
    // Behavior identical to RhpThrowEx except:
    //   - ExInfo.Kind = (Throw | Rethrow) = 0x05.
    //   - Dispatcher detects Rethrow flag, uses PREV ExInfo's ExContext
    //     (= original throw-site PAL) и starts FindFirstPassHandler от
    //     прежнего idxCurClause + 1, чтобы catch не зацепился сам за себя.
    //
    // Removed legacy halt-stub из ExceptionEngine.cs (was managed
    // [RuntimeExport]). Single definition теперь здесь.
    internal static class RethrowStub
    {
        [RuntimeExport("RhpRethrow")]
        private static unsafe void RhpRethrow(byte* exceptionAsBytes)
        {
            // Padding body — never executes when patched.
            if (exceptionAsBytes == null)
                OS.Kernel.Panic.Fail("RhpRethrow: null exception (stub not patched)");

            ulong* slot = (ulong*)exceptionAsBytes;
            slot[0] = 0; slot[1] = 0; slot[2] = 0; slot[3] = 0;
            slot[4] = 0; slot[5] = 0; slot[6] = 0; slot[7] = 0;
            slot[8] = 0; slot[9] = 0; slot[10] = 0; slot[11] = 0;
            slot[12] = 0; slot[13] = 0; slot[14] = 0; slot[15] = 0;
            slot[16] = 0; slot[17] = 0; slot[18] = 0; slot[19] = 0;
            slot[20] = 0; slot[21] = 0; slot[22] = 0; slot[23] = 0;
            slot[24] = 0; slot[25] = 0; slot[26] = 0; slot[27] = 0;
            slot[28] = 0; slot[29] = 0; slot[30] = 0; slot[31] = 0;

            OS.Kernel.Panic.Fail("RhpRethrow: stub body executed — patcher did not run");
        }

        public static unsafe void* GetMethodAddress()
        {
            delegate*<byte*, void> fn = &RhpRethrow;
            return (void*)fn;
        }
    }
}
