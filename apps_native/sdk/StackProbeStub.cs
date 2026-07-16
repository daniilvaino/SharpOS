// RhpStackProbe for freestanding PE apps (step142). ILC emits a call to
// this helper in the prologue of any method whose frame crosses a page
// (ManagedDoom's renderer/table-init frames do); the real implementation
// lives in the NativeAOT runtime lib we exclude, and DropResilient stopped
// masking the missing symbol.
//
// On SharpOS the probe is semantically a no-op: app stacks are fully mapped
// up front by ProcessImageBuilder — there are no guard pages to grow, so
// touching pages in order buys nothing. Same reasoning as the kernel's
// __chkstk (ChkstkStub + 0xC3 patch, see memory/limits docs).
//
// The helper's contract is register-preserving (upstream StackProbe.asm
// clobbers only r11, rax holds the low frame address), so the ILC-emitted
// managed body CANNOT be trusted to run — it has its own prologue. Instead
// AppRuntime.Initialize overwrites byte 0 with `ret` (0xC3) BEFORE anything
// with a big frame runs: the call then returns immediately with all
// registers intact. Pattern mirrors ChkstkStub / InterfaceDispatchTrampoline
// (app image pages are mapped RWX by the PeLoader).

using System.Runtime;

namespace SharpOS.AppSdk
{
    internal static unsafe class StackProbeStub
    {
        [RuntimeExport("RhpStackProbe")]
        private static void RhpStackProbe()
        {
            // Never executes once PatchToRet has run. Inflate the body so
            // there is guaranteed room for the 1-byte patch, and make the
            // unpatched case loudly fatal rather than silently corrupting.
            AppHost.WriteString("RhpStackProbe: stub body executed — patch did not run\n");
            for (; ; ) { }
        }

        private static void* GetMethodAddress()
        {
            delegate*<void> fn = &RhpStackProbe;
            return (void*)fn;
        }

        public static void PatchToRet()
        {
            byte* target = (byte*)GetMethodAddress();
            if (target == null) return;
            target[0] = 0xC3; // ret
        }
    }
}
