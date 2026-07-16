using System.Runtime;

namespace SharpOS.AppSdk
{
    // throw/catch for freestanding PE apps, by SHARING the kernel's managed EH
    // engine (RhpThrowEx -> DispatchEx) instead of carrying one.
    //
    // ILC lowers `throw expr` to a call to RhpThrowEx with the exception object
    // in RCX and the throw-site return address on the stack. The kernel's
    // RhpThrowEx (OS/src/Boot/EH/ThrowExStub.cs, patched at boot to shellcode)
    // captures that throw-site RSP/RIP, builds a PAL context + ExInfo, and drives
    // DispatchEx, which is now image-aware (step140): it walks the app's frames
    // via the app's .pdata (registered by PeLoader) and runs app catch/finally
    // funclets.
    //
    // The app provides its own RhpThrowEx symbol (satisfies ILC's `throw`
    // codegen) and, at startup, patches its body to tail-jump the kernel's
    // RhpThrowEx entry (handed over in AppServiceTable.RhpThrowExAddress). The
    // absolute form is required: the app image (0x400000) is further than an
    // int32 rel32 from the kernel entry. A tail JMP (not CALL) preserves the
    // throw-site return address the kernel shellcode reads from [rsp].
    internal static unsafe class ThrowExTrampoline
    {
        [RuntimeExport("RhpThrowEx")]
        private static void RhpThrowEx(byte* exceptionAsBytes)
        {
            // Unpatched fallback: only runs if PatchToKernelThrow never ran or
            // the kernel address was 0. Body must be >= 12 bytes so the
            // mov rax,imm64 + jmp rax patch fits; the string load + halt loop is
            // comfortably larger.
            _ = exceptionAsBytes;
            AppHost.WriteString("RhpThrowEx: throw bridge not patched\r\n");
            for (; ; ) { }
        }

        private static void* GetMethodAddress()
        {
            delegate*<byte*, void> fn = &RhpThrowEx;
            return (void*)fn;
        }

        // Overwrite the first 12 bytes of the stub with an absolute tail jump to
        // the kernel RhpThrowEx entry:  48 B8 <imm64> (mov rax, addr) FF E0 (jmp rax).
        // rax is scratch at the throw call-site; RCX (exception) and [rsp]
        // (throw-site return address) are preserved through the jump.
        public static void PatchToKernelThrow(ulong throwExAddress)
        {
            if (throwExAddress == 0) return;

            byte* target = (byte*)GetMethodAddress();
            if (target == null) return;

            target[0] = 0x48;                               // mov rax, imm64
            target[1] = 0xB8;
            target[2] = (byte)(throwExAddress);
            target[3] = (byte)(throwExAddress >> 8);
            target[4] = (byte)(throwExAddress >> 16);
            target[5] = (byte)(throwExAddress >> 24);
            target[6] = (byte)(throwExAddress >> 32);
            target[7] = (byte)(throwExAddress >> 40);
            target[8] = (byte)(throwExAddress >> 48);
            target[9] = (byte)(throwExAddress >> 56);
            target[10] = 0xFF;                              // jmp rax
            target[11] = 0xE0;
        }
    }
}
