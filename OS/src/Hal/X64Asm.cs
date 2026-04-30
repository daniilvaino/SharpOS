namespace OS.Hal
{
    // Inline-asm-style CPU instruction helpers via shellcode buffer pattern.
    //
    // ⚠️ NOT YET USABLE: KernelHeap allocations become non-executable after
    // pager init enforces W^X. Calling stubs placed в KernelHeap memory
    // triggers #PF on instruction fetch — verified в Phase 1 closure attempt
    // (recursive #PF при STI call from HwFaultBridge).
    //
    // To activate: route through proper EfiLoaderCode buffer (similar к
    // BootInfo.IdtExecBuffer pattern) или patch [RuntimeExport] method body
    // в .text section (similar к existing shellcode patchers).
    internal static unsafe class X64Asm
    {
        private const uint StubBufferSize = 64;
        private const uint StiOffset = 0;
        private const uint CliOffset = 16;
        private const uint HltOffset = 32;

        private static bool s_initialized;
        private static delegate* unmanaged<void> s_sti;
        private static delegate* unmanaged<void> s_cli;
        private static delegate* unmanaged<void> s_hlt;

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

            byte* stub = (byte*)global::OS.Kernel.Memory.KernelHeap.Alloc(StubBufferSize);
            if (stub == null) return false;

            OS.Kernel.Util.Memory.Zero(stub, StubBufferSize);

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
