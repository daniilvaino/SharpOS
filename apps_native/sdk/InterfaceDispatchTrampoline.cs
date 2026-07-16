using System.Runtime;

namespace SharpOS.AppSdk
{
    // Interface dispatch for freestanding PE apps, by SHARING the kernel's
    // bridge rather than carrying our own resolver.
    //
    // ILC writes the address of RhpInitialDynamicInterfaceDispatch (below) into
    // every interface-dispatch cell's m_pStub. The kernel's equivalent stub
    // (OS/src/Boot/InterfaceDispatchStub.cs) gets its first bytes overwritten at
    // boot with a `jmp rel32` into a 195-byte shellcode bridge that reads the
    // cell from r10, calls the managed resolver, and tail-jumps to the target.
    // That resolver (OS/src/Kernel/Memory/InterfaceDispatchResolver.cs) is PURE
    // on major-9: it resolves entirely from the instance MethodTable + cell, so
    // it works unchanged on a foreign app MethodTable (same ILC8/major-9 layout)
    // loaded in the shared kernel address space.
    //
    // So the app does NOT re-implement any of that. It only:
    //   1. Provides its own RhpInitialDynamicInterfaceDispatch symbol (satisfies
    //      ILC's cell references; its inert body is the not-yet-patched fallback).
    //   2. At startup, overwrites that body with `mov rax,<bridge>; jmp rax` where
    //      <bridge> is the kernel's InterfaceDispatchBridge.ShellcodeStart handed
    //      over via AppServiceTable.InterfaceDispatchBridgeAddress.
    // The absolute (12-byte) form is required because the app image (linked
    // at /BASE:0x100000000, see FreestandingPe.props) and the kernel
    // exec-stub buffer are further than an int32 rel32 apart, so the
    // kernel's own 5-byte `jmp rel32` patch would not reach.
    internal static unsafe class InterfaceDispatchTrampoline
    {
        [RuntimeExport("RhpInitialDynamicInterfaceDispatch")]
        private static void RhpInitialDynamicInterfaceDispatch()
        {
            // Unpatched fallback: only runs if PatchToKernelBridge was never
            // called or failed (e.g. kernel bridge not installed). The body has
            // to be at least 12 bytes so the mov rax,imm64 + jmp rax patch fits;
            // the string load + call + halt loop is comfortably larger.
            AppHost.WriteString("RhpInitialDynamicInterfaceDispatch: bridge not patched\r\n");
            for (; ; ) { }
        }

        private static void* GetMethodAddress()
        {
            delegate*<void> fn = &RhpInitialDynamicInterfaceDispatch;
            return (void*)fn;
        }

        // Overwrite the first 12 bytes of the stub body with an absolute jump to
        // the kernel bridge:  48 B8 <imm64>   mov rax, bridge
        //                     FF E0            jmp rax
        // rax is scratch at the ILC dispatch call-site (the shellcode's own fast
        // path clobbers it first with `mov rax,[rcx]`), so trampolining through
        // it does not disturb the rcx=this / r10=cell contract.
        //
        // App image pages are mapped RWX by the kernel PeLoader, so the in-place
        // write succeeds. No pipeline serialization (matches every other patcher
        // in the tree; QEMU re-reads, hardware caveat tracked in limits doc).
        public static void PatchToKernelBridge(ulong bridgeAddress)
        {
            if (bridgeAddress == 0) return;

            byte* target = (byte*)GetMethodAddress();
            if (target == null) return;

            target[0] = 0x48;                               // mov rax, imm64
            target[1] = 0xB8;
            target[2] = (byte)(bridgeAddress);
            target[3] = (byte)(bridgeAddress >> 8);
            target[4] = (byte)(bridgeAddress >> 16);
            target[5] = (byte)(bridgeAddress >> 24);
            target[6] = (byte)(bridgeAddress >> 32);
            target[7] = (byte)(bridgeAddress >> 40);
            target[8] = (byte)(bridgeAddress >> 48);
            target[9] = (byte)(bridgeAddress >> 56);
            target[10] = 0xFF;                              // jmp rax
            target[11] = 0xE0;
        }
    }
}
