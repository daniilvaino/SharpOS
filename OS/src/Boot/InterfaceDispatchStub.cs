using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.Boot
{
    // Managed entry-point for interface dispatch. ILC writes the address of
    // this method into every interface-dispatch cell's m_pStub field in the
    // image. On first call for each cell, control jumps here.
    //
    // Strategy: the first 5 bytes of this method body are OVERWRITTEN at
    // kernel boot by a `jmp rel32` to a hand-crafted x64 shellcode living
    // in the exec stub buffer. That shellcode reads r10 (indirection cell),
    // spills args, calls a managed resolver, restores args, and tail-jmps
    // to the resolved target. We can't do that from a managed wrapper
    // directly because its prolog scratches r10 before our body runs.
    //
    // While still un-patched (or if patching failed), this body serves as a
    // noisy fallback: Panic.Fail with a tag, orderly shutdown. The body has
    // to be at least 5 bytes so the JMP fits — Panic.Fail(string) is
    // plenty. Kernel-only; apps don't hit this path yet.
    internal static class InterfaceDispatchStub
    {
        [RuntimeExport("RhpInitialDynamicInterfaceDispatch")]
        private static void RhpInitialDynamicInterfaceDispatch()
        {
            OS.Kernel.Panic.Fail("RhpInitialDynamicInterfaceDispatch (stub not patched / patch failed)");
        }

        // Exposed for the boot-time patcher to know where to write the JMP.
        public static unsafe void* GetMethodAddress()
        {
            delegate*<void> fn = &RhpInitialDynamicInterfaceDispatch;
            return (void*)fn;
        }
    }
}
