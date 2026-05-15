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

        // IRETQ-resume stub. Lives at 0x300 — clear of X64Asm 0..63, the
        // CoreClrProbe GS-base wrmsr at 64, and the SehDispatch capture
        // (0x80) / restore (0x200, ~0x28C) blocks. Buffer is 1024.
        private const uint ResumeOffset      = 0x300;
        private const uint ResumeMinBuffer   = 0x380;

        private static bool s_initialized;
        private static void* s_execBuffer;
        private static uint s_execBufferSize;
        private static delegate* unmanaged<void> s_sti;
        private static delegate* unmanaged<void> s_cli;
        private static delegate* unmanaged<void> s_hlt;
        private static bool s_resumeReady;
        private static delegate* unmanaged<void*, void> s_resume;

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

        // Restore all GPRs from an Idt.InterruptFrame and IRETQ back to the
        // faulting instruction (RIP/CS/RFLAGS/RSP/SS popped from the frame —
        // RFLAGS.IF is thus restored to its pre-fault value, unlike the
        // STI-bypass catch path). `frame` points at the InterruptFrame the
        // common stub built; its fields are sequential ulongs:
        //   Cr2=0 Rax=8 Rcx=16 Rdx=24 Rbx=32 Rsi=40 Rdi=48 Rbp=56
        //   R8=64 R9=72 R10=80 R11=88 R12=96 R13=104 R14=112 R15=120
        //   Vector=128 Err=136 Rip=144 Cs=152 Rflags=160 Rsp=168 Ss=176
        // We lea rsp→&Rip so the trailing IRETQ consumes Rip..Ss in order.
        // Never returns on success (iretq). Returns false only if the exec
        // buffer is unavailable (caller falls back to the panic path).
        public static bool TryResumeFrame(void* frame)
        {
            if (s_execBuffer == null || s_execBufferSize < ResumeMinBuffer)
                return false;
            if (!s_resumeReady)
                EmitResume((byte*)s_execBuffer + ResumeOffset);
            s_resume(frame);          // iretq — does not return
            return true;              // unreachable
        }

        // Emit: 14× `mov <gpr>,[rcx+disp32]`, `lea rsp,[rcx+0x90]`,
        // `mov rcx,[rcx+0x10]`, `iretq`. RCX = frame* (Win64 arg0).
        private static void EmitResume(byte* p)
        {
            int i = 0;

            // mov reg,[rcx+disp32]: REX(0x48 / 0x4C) 8B modrm(10 reg 001) disp32
            void Mov(byte rex, byte modrm, uint disp)
            {
                p[i++] = rex; p[i++] = 0x8B; p[i++] = modrm;
                p[i++] = (byte)(disp & 0xFF);
                p[i++] = (byte)((disp >> 8) & 0xFF);
                p[i++] = (byte)((disp >> 16) & 0xFF);
                p[i++] = (byte)((disp >> 24) & 0xFF);
            }

            Mov(0x48, 0x81,   8);   // mov rax,[rcx+8]
            Mov(0x48, 0x91,  24);   // mov rdx,[rcx+24]
            Mov(0x48, 0x99,  32);   // mov rbx,[rcx+32]
            Mov(0x48, 0xB1,  40);   // mov rsi,[rcx+40]
            Mov(0x48, 0xB9,  48);   // mov rdi,[rcx+48]
            Mov(0x48, 0xA9,  56);   // mov rbp,[rcx+56]
            Mov(0x4C, 0x81,  64);   // mov r8 ,[rcx+64]
            Mov(0x4C, 0x89,  72);   // mov r9 ,[rcx+72]
            Mov(0x4C, 0x91,  80);   // mov r10,[rcx+80]
            Mov(0x4C, 0x99,  88);   // mov r11,[rcx+88]
            Mov(0x4C, 0xA1,  96);   // mov r12,[rcx+96]
            Mov(0x4C, 0xA9, 104);   // mov r13,[rcx+104]
            Mov(0x4C, 0xB1, 112);   // mov r14,[rcx+112]
            Mov(0x4C, 0xB9, 120);   // mov r15,[rcx+120]

            // lea rsp,[rcx+0x90]  (REX.W 8D modrm=10 100 001 disp32=144)
            p[i++] = 0x48; p[i++] = 0x8D; p[i++] = 0xA1;
            p[i++] = 0x90; p[i++] = 0x00; p[i++] = 0x00; p[i++] = 0x00;

            // mov rcx,[rcx+0x10]  (last use of frame base → restore Rcx)
            Mov(0x48, 0x89, 16);

            // iretq  (REX.W CF)
            p[i++] = 0x48; p[i++] = 0xCF;

            s_resume = (delegate* unmanaged<void*, void>)p;
            s_resumeReady = true;
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
