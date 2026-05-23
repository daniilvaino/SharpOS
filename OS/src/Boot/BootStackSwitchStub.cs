using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.Boot
{
    // Managed entry-point for the boot-stack switch helper. The Win64
    // ABI managed body here is never executed in the normal flow —
    // BootStackSwitchPatcher overwrites the first 14 bytes with hand-
    // coded shellcode that:
    //
    //   1. switches RSP to the new stack top         (mov rsp, rcx)
    //   2. clears RBP                                (mov rbp, 0)
    //   3. pushes a fake return-address 0            (push 0)
    //      — emulates the 8-byte CALL push so the callee sees the
    //        Win64 ABI convention "RSP mod 16 == 8" on entry, and
    //        `ret` would land in 0 (panic — but we never return)
    //   4. jumps to the continuation                 (jmp rdx)
    //
    // The continuation receives no args; argv is passed via static
    // fields (Platform.s_bootInfo is already such a static, set by
    // Platform.Init before this call).
    //
    // Same patcher pattern as ByRefAssignRefStub. Fallback Panic.Fail
    // fires only if patching never ran.
    internal static unsafe class BootStackSwitchStub
    {
        [RuntimeExport("BootStackSwitchRaw")]
        [UnmanagedCallersOnly(EntryPoint = "BootStackSwitchRaw")]
        private static void BootStackSwitchRaw(byte* newRspTop, delegate* unmanaged<void> cont)
        {
            OS.Kernel.Panic.Fail("BootStackSwitchRaw (stub not patched / patch failed)");
        }

        public static void* GetMethodAddress()
        {
            delegate* unmanaged<byte*, delegate* unmanaged<void>, void> fn = &BootStackSwitchRaw;
            return (void*)fn;
        }
    }
}
