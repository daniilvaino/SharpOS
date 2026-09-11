using System.Runtime;

namespace SharpOS.AppSdk
{
    // `throw;` for freestanding PE apps.
    //
    // The partner of ThrowExTrampoline next door, and a separate helper for a
    // real reason: `throw expr` starts a dispatch, while a bare `throw;` inside
    // a catch RESUMES the one already in flight — it must not lose the original
    // throw site, or the exception appears to come from the catch block that
    // rethrew it.
    //
    // Same mechanism as the throw bridge: keep a symbol so ILC has an address
    // to call, then overwrite the body at startup with an absolute tail jump to
    // the kernel's RhpRethrow. Absolute because the app is mapped far beyond a
    // 32-bit displacement from the kernel; a JMP rather than a CALL because the
    // kernel shellcode reads the throw-site return address from [rsp].
    internal static unsafe class RethrowTrampoline
    {
        [RuntimeExport("RhpRethrow")]
        private static void RhpRethrow(byte* exceptionAsBytes)
        {
            // Unpatched fallback. Must be at least 12 bytes so the patch fits;
            // the string load and halt loop are comfortably larger.
            _ = exceptionAsBytes;
            AppHost.WriteError("RhpRethrow: rethrow bridge not patched\r\n");
            for (; ; ) { }
        }

        private static void* GetMethodAddress()
        {
            delegate*<byte*, void> fn = &RhpRethrow;
            return (void*)fn;
        }

        public static void PatchToKernelRethrow(ulong rethrowAddress)
        {
            if (rethrowAddress == 0) return;

            byte* target = (byte*)GetMethodAddress();
            if (target == null) return;

            target[0] = 0x48;                               // mov rax, imm64
            target[1] = 0xB8;
            target[2] = (byte)(rethrowAddress);
            target[3] = (byte)(rethrowAddress >> 8);
            target[4] = (byte)(rethrowAddress >> 16);
            target[5] = (byte)(rethrowAddress >> 24);
            target[6] = (byte)(rethrowAddress >> 32);
            target[7] = (byte)(rethrowAddress >> 40);
            target[8] = (byte)(rethrowAddress >> 48);
            target[9] = (byte)(rethrowAddress >> 56);
            target[10] = 0xFF;                              // jmp rax
            target[11] = 0xE0;
        }
    }
}
