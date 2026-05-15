using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // Hosts the [RuntimeExport] symbol __chkstk. Body is overwritten at
    // boot time by ChkstkPatcher with a single 0xC3 (ret) byte.
    //
    // MSVC's libcmt __chkstk reads gs:[10h] (TEB.StackLimit) and, if the
    // probed page is below that limit, enters a page-by-page zero-write
    // extension loop ("guard page expansion"). On bare metal we set the
    // TEB.StackLimit once at boot in CoreClrProbe.SetupTebFacade; CoreCLR
    // descends into 0x1300+ byte frames well below that snapshot, which
    // would land in the extension loop и zero memory the kernel relies on.
    //
    // Standard MSVC __chkstk is net-zero on RSP — the CALLER emits the
    // actual `sub rsp, rax` AFTER the call. So a no-op `ret` is functionally
    // equivalent for the unikernel case where:
    //   - There are no guard pages (flat large kernel stack).
    //   - All in-frame addresses are already-mapped kernel memory.
    //
    // Pattern matches ByRefAssignRefStub / RethrowStub / ThrowExStub:
    // [RuntimeExport] symbol wins via /FORCE:MULTIPLE (OS.obj precedes
    // libcmt.lib in linker order), ILC-emitted body is overwritten at boot
    // by ChkstkPatcher with shellcode (here: single byte `0xC3`).
    internal static class ChkstkStub
    {
        [RuntimeExport("__chkstk")]
        [UnmanagedCallersOnly(EntryPoint = "__chkstk")]
        private static unsafe void Chkstk()
        {
            // Padding — never executes after patcher runs. Inflate via
            // explicit writes so ILC emits >= a few bytes for the body
            // (patcher only overwrites byte 0).
            ulong* p = (ulong*)0;
            // Volatile-looking work just so the body is not dead-code-eliminated.
            p[0] = 0; p[1] = 0; p[2] = 0; p[3] = 0;
            p[4] = 0; p[5] = 0; p[6] = 0; p[7] = 0;
            OS.Kernel.Panic.Fail("__chkstk: stub body executed — patcher did not run");
        }

        public static unsafe void* GetMethodAddress()
        {
            delegate* unmanaged<void> fn = &Chkstk;
            return (void*)fn;
        }
    }
}
