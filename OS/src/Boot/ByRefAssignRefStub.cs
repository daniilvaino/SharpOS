using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.Boot
{
    // Managed entry-point for ILC's RhpByRefAssignRef helper (used in
    // ref-struct copies when the struct holds object references).
    //
    // The helper has a non-standard calling convention — on AMD64:
    //   rdi = destination address (ref-field being written)
    //   rsi = source address      (data to load)
    //   clobbers rcx
    //   on exit: rdi and rsi are each incremented by 8
    //
    // Managed C# bodies are compiled to Win64 ABI (rcx/rdx for args),
    // so we can't implement this as a regular [RuntimeExport] method —
    // ILC-emitted callers put args in rdi/rsi instead. Same trick as
    // InterfaceDispatchStub: keep a symbol with the right name +
    // [RuntimeExport] + [UnmanagedCallersOnly], let ILC reserve the
    // address, then overwrite the first 13 bytes of the body at kernel
    // boot with hand-written shellcode. The fallback Panic.Fail only
    // fires if patching fails.
    //
    // Our GC is non-moving mark-sweep with no card tables / generations,
    // so the minimal valid shellcode is a straight copy + increment —
    // no barrier work needed.
    internal static class ByRefAssignRefStub
    {
        [RuntimeExport("RhpByRefAssignRef")]
        [UnmanagedCallersOnly(EntryPoint = "RhpByRefAssignRef")]
        private static void RhpByRefAssignRef()
        {
            OS.Kernel.Panic.Fail("RhpByRefAssignRef (stub not patched / patch failed)");
        }

        public static unsafe void* GetMethodAddress()
        {
            delegate* unmanaged<void> fn = &RhpByRefAssignRef;
            return (void*)fn;
        }
    }
}
