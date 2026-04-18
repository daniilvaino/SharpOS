namespace OS.Kernel.Memory
{
    // Register-spill trampoline for the conservative GC stack scanner.
    //
    // Problem: ILC may keep live managed references in callee-saved
    // registers (RBX, RBP, RDI, RSI, R12..R15) across calls. Our
    // ScanStack reads memory only, so those roots would be invisible.
    //
    // Solution: before running MarkAll, call into a small shellcode
    // stub that pushes all callee-saved regs onto the stack, calls
    // back into managed code, then pops them. While MarkAll runs,
    // those register values are present in the stack region that
    // ScanStack walks, so any root living in a callee-saved reg is
    // picked up by the conservative sweep.
    //
    // Memory layout: the shellcode is installed at offset 64 of the
    // shared ExecStubBuffer (Cr3Accessor uses 0..63).
    internal static unsafe class GcStackSpill
    {
        private const uint StubOffset = 64;
        private const uint StubSize = 64;

        private static bool s_initialized;
        private static delegate* unmanaged<delegate* unmanaged<void>, void> s_invoke;

        public static bool IsInitialized => s_initialized;

        public static bool TryInitialize(void* execBuffer, uint execBufferSize)
        {
            if (s_initialized)
                return true;

            if (execBuffer == null || execBufferSize < StubOffset + StubSize)
                return false;

            byte* stub = (byte*)execBuffer + StubOffset;
            WriteShellcode(stub);

            s_invoke = (delegate* unmanaged<delegate* unmanaged<void>, void>)stub;
            s_initialized = true;
            return true;
        }

        public static void Invoke(delegate* unmanaged<void> callback)
        {
            if (!s_initialized || s_invoke == null)
                return;

            s_invoke(callback);
        }

        // Win64 ABI shellcode with full callee-saved spill:
        //   push rbp / rbx / rdi / rsi / r12 / r13 / r14 / r15
        //   sub rsp, 0x28        ; 32 bytes shadow + 8 bytes alignment
        //   call rcx             ; rcx = callback (first arg)
        //   add rsp, 0x28
        //   pop r15..rbp (reverse order)
        //   ret
        //
        // 35 bytes total. At entry RSP is 8 mod 16; 8 pushes (keeps mod 16)
        // + 0x28 sub gives 0 mod 16 at call, satisfying Win64 alignment.
        //
        // The 8 pushes put all callee-saved registers onto the stack BEFORE
        // the managed callback runs. Since our conservative stack scanner
        // only reads memory, pushing these regs makes any managed ref they
        // happen to hold visible to the scan — closing the "root lost in
        // RBX/R12" hole of pure stack-only scanning.
        private static void WriteShellcode(byte* p)
        {
            int i = 0;
            p[i++] = 0x55;                                  // push rbp
            p[i++] = 0x53;                                  // push rbx
            p[i++] = 0x57;                                  // push rdi
            p[i++] = 0x56;                                  // push rsi
            p[i++] = 0x41; p[i++] = 0x54;                   // push r12
            p[i++] = 0x41; p[i++] = 0x55;                   // push r13
            p[i++] = 0x41; p[i++] = 0x56;                   // push r14
            p[i++] = 0x41; p[i++] = 0x57;                   // push r15
            p[i++] = 0x48; p[i++] = 0x83; p[i++] = 0xEC; p[i++] = 0x28; // sub rsp, 0x28
            p[i++] = 0xFF; p[i++] = 0xD1;                   // call rcx
            p[i++] = 0x48; p[i++] = 0x83; p[i++] = 0xC4; p[i++] = 0x28; // add rsp, 0x28
            p[i++] = 0x41; p[i++] = 0x5F;                   // pop r15
            p[i++] = 0x41; p[i++] = 0x5E;                   // pop r14
            p[i++] = 0x41; p[i++] = 0x5D;                   // pop r13
            p[i++] = 0x41; p[i++] = 0x5C;                   // pop r12
            p[i++] = 0x5E;                                  // pop rsi
            p[i++] = 0x5F;                                  // pop rdi
            p[i++] = 0x5B;                                  // pop rbx
            p[i++] = 0x5D;                                  // pop rbp
            p[i++] = 0xC3;                                  // ret
        }
    }
}
