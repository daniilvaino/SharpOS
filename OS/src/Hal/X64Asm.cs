namespace OS.Hal
{
    // Inline-asm-style CPU instruction helpers via shellcode buffer pattern.
    // Same approach как Cr3Accessor — write tiny instruction + ret bytes,
    // expose via delegate* unmanaged.
    //
    // Storage: external EfiLoaderCode buffer (passed via SetExecBuffer
    // from BootInfo.AsmExecBuffer at boot). KernelHeap-backed allocations
    // are NX after pager init enforces W^X — instruction fetch from such
    // address triggers #PF. EfiLoaderCode pages stay R+X.
    //
    // Used for CPU-level operations не expressible in managed C#:
    //   STI/CLI — RFLAGS.IF manipulation (e.g., re-enable interrupts after
    //             HW fault catch path bypasses IRETQ).
    //   HLT — halt CPU to wait for next interrupt.
    internal static unsafe class X64Asm
    {
        private const uint StiOffset = 0;
        private const uint CliOffset = 16;
        private const uint HltOffset = 32;
        private const uint MinBufferSize = 64;

        private static bool s_initialized;
        private static void* s_execBuffer;
        private static uint s_execBufferSize;
        private static delegate* unmanaged<void> s_sti;
        private static delegate* unmanaged<void> s_cli;
        private static delegate* unmanaged<void> s_hlt;

        public static bool IsAvailable => s_initialized;

        public static void SetExecBuffer(void* buffer, uint size)
        {
            s_execBuffer = buffer;
            s_execBufferSize = size;
        }

        public static void Sti()
        {
            if (!s_initialized && !TryInitialize())
                return;
            s_sti();
        }

        public static void Cli()
        {
            if (!s_initialized && !TryInitialize())
                return;
            s_cli();
        }

        public static void Hlt()
        {
            if (!s_initialized && !TryInitialize())
                return;
            s_hlt();
        }

        private static bool TryInitialize()
        {
            if (s_initialized) return true;
            if (s_execBuffer == null || s_execBufferSize < MinBufferSize)
                return false;

            byte* stub = (byte*)s_execBuffer;

            // Zero only the slots we use (avoid touching unrelated memory).
            for (int i = 0; i < (int)MinBufferSize; i++) stub[i] = 0;

            // STI; RET — sets RFLAGS.IF, returns.
            stub[StiOffset + 0] = 0xFB;
            stub[StiOffset + 1] = 0xC3;

            // CLI; RET — clears RFLAGS.IF, returns.
            stub[CliOffset + 0] = 0xFA;
            stub[CliOffset + 1] = 0xC3;

            // HLT; RET — halts CPU until next interrupt, returns.
            stub[HltOffset + 0] = 0xF4;
            stub[HltOffset + 1] = 0xC3;

            s_sti = (delegate* unmanaged<void>)(stub + StiOffset);
            s_cli = (delegate* unmanaged<void>)(stub + CliOffset);
            s_hlt = (delegate* unmanaged<void>)(stub + HltOffset);
            s_initialized = true;
            return true;
        }
    }
}
