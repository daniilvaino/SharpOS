using System.Runtime;

namespace OS.Boot
{
    // Kernel-only stub for interface-dispatch helpers emitted by ILC.
    // In the real runtime this is a hand-written asm trampoline from
    // src/coreclr/nativeaot/Runtime/amd64/StubDispatch.asm that does
    //   cmp  byte ptr [rcx], 0          ; null-check `this`
    //   jmp  RhpInterfaceDispatchSlow   ; → RhpCidResolve → UniversalTransition
    // and resolves the interface target by walking the object's MethodTable.
    //
    // We have none of that machinery yet (no cache, no cid-resolve, no
    // universal transition). So this stub just panics with a clear tag —
    // way better than the old `while(true)` silent halt. When/if ILC emits
    // a call here, we want to know about it loudly.
    //
    // Apps don't currently need this symbol (their generated code never
    // goes through shared-generic interface dispatch); keeping the stub
    // kernel-only avoids forcing a second copy into the apps build.
    internal static class InterfaceDispatchStub
    {
        [RuntimeExport("RhpInitialDynamicInterfaceDispatch")]
        private static void RhpInitialDynamicInterfaceDispatch()
        {
            OS.Kernel.Panic.Fail("RhpInitialDynamicInterfaceDispatch (shared-generic interface dispatch not implemented)");
        }
    }
}
