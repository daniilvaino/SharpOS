using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.Boot.EH
{
    // Hosts the [RuntimeExport] symbol RhpCaptureContext. Patcher
    // overwrites the body with byte-array shellcode at boot time.
    //
    // Same pattern as ByRefAssignRefStub / PortIoStub. Body has enough
    // managed code to ensure it's at least ~100 bytes long, so the
    // 53-byte capture shellcode fits comfortably.
    //
    // Entry contract (Microsoft x64 ABI):
    //   RCX = PAL_LIMITED_CONTEXT* dest buffer
    //   stack[0] = return address (pushed by CALL)
    //
    // Stores into PAL_LIMITED_CONTEXT:
    //   IP   = return address (caller's IP at call site)
    //   Rsp  = caller's RSP (= our RSP + 8 after the CALL push)
    //   Rbp/Rdi/Rsi/Rax/Rbx/R12-R15 = current register values
    //   XMM6..XMM15 are NOT captured by this minimal stub (they are
    //   left at whatever the caller wrote — caller is expected to
    //   zero the buffer first if it cares). Step 5+ shellcode RhpThrowEx
    //   captures full XMM bank.
    internal static class CaptureContextStub
    {
        [RuntimeExport("RhpCaptureContext")]
        private static unsafe void CaptureContext(byte* ctx)
        {
            // Padding body — ILC compiles this to enough bytes that the
            // shellcode patcher's 53-byte overwrite has room. Never
            // executes when patched (shellcode ends with ret before
            // reaching here).
            if (ctx == null)
                OS.Kernel.Panic.Fail("RhpCaptureContext: null ctx");

            // Zero a few fields explicitly so each statement compiles to
            // its own mov instruction. ~7 bytes per write * 12 = 84 bytes
            // of padding work alone, well past the 53-byte shellcode.
            *(ulong*)(ctx + 0x00) = 0;
            *(ulong*)(ctx + 0x08) = 0;
            *(ulong*)(ctx + 0x10) = 0;
            *(ulong*)(ctx + 0x18) = 0;
            *(ulong*)(ctx + 0x20) = 0;
            *(ulong*)(ctx + 0x28) = 0;
            *(ulong*)(ctx + 0x30) = 0;
            *(ulong*)(ctx + 0x38) = 0;
            *(ulong*)(ctx + 0x40) = 0;
            *(ulong*)(ctx + 0x48) = 0;
            *(ulong*)(ctx + 0x50) = 0;

            OS.Kernel.Panic.Fail("RhpCaptureContext: stub not patched");
        }

        public static unsafe void* GetMethodAddress()
        {
            delegate*<byte*, void> fn = &CaptureContext;
            return (void*)fn;
        }
    }
}
