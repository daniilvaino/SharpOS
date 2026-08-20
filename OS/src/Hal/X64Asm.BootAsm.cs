// step 118 Wave 4 — compile-time codegen migration of X64Asm small
// inline-asm-style stubs. 10 stubs, all 0-hole. Deferred: CoopSwitch
// (forward jz to .skip_gs needs walker label support), Sti/Cli/Hlt
// (2 bytes each, raw stays trivial).

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Hal
{
    internal static unsafe partial class X64Asm
    {
        // sti ; ret — set RFLAGS.IF.
        [CompileTimeAsm]
        private static partial int EmitStiBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitStiBootAsm))]
        private static void EmitStiBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.sti();
            a.ret();
        }

        // cli ; ret — clear RFLAGS.IF.
        [CompileTimeAsm]
        private static partial int EmitCliBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitCliBootAsm))]
        private static void EmitCliBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.cli();
            a.ret();
        }

        // hlt ; ret — halt CPU until next interrupt.
        [CompileTimeAsm]
        private static partial int EmitHltBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitHltBootAsm))]
        private static void EmitHltBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.hlt();
            a.ret();
        }

        // xsetbv. ECX = selector, EDX:EAX = value.
        [CompileTimeAsm]
        private static partial int EmitXsetbvBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitXsetbvBootAsm))]
        private static void EmitXsetbvBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.mov(rax, rdx);          // EAX = low 32 of value
            a.shr(rdx, 32);           // EDX = high 32 of value
            a.xsetbv();
            a.ret();
        }

        // mov rax, cr4 ; ret
        [CompileTimeAsm]
        private static partial int EmitReadCr4BootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitReadCr4BootAsm))]
        private static void EmitReadCr4BootAsm_Body(Iced.Intel.Assembler a)
        {
            a.mov(rax, cr4);
            a.ret();
        }

        // gs prefix + mov rax, [rcx] ; ret  (read 8 bytes from gs:[offset])
        [CompileTimeAsm]
        private static partial int EmitReadGsQwordBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitReadGsQwordBootAsm))]
        private static void EmitReadGsQwordBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.db(0x65);                       // gs: segment override
            a.mov(rax, __qword_ptr[rcx]);
            a.ret();
        }

        // Write IA32_GS_BASE = rcx (Win64 arg1).
        [CompileTimeAsm]
        private static partial int EmitWriteGsBaseMsrBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitWriteGsBaseMsrBootAsm))]
        private static void EmitWriteGsBaseMsrBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.mov(rax, rcx);
            a.mov(rdx, rcx);
            a.shr(rdx, 32);
            // mov ecx, 0xC0000101 (IA32_GS_BASE) — walker can't parse uint
            // literal suffix; emit raw 5-byte mov-imm32 form.
            a.db(0xB9, 0x01, 0x01, 0x00, 0xC0);
            a.wrmsr();
            a.ret();
        }

        // Read IA32_GS_BASE → RAX.
        [CompileTimeAsm]
        private static partial int EmitReadGsBaseMsrBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitReadGsBaseMsrBootAsm))]
        private static void EmitReadGsBaseMsrBootAsm_Body(Iced.Intel.Assembler a)
        {
            // mov ecx, 0xC0000101 (IA32_GS_BASE) — same walker workaround.
            a.db(0xB9, 0x01, 0x01, 0x00, 0xC0);
            a.rdmsr();
            a.shl(rdx, 32);
            a.or(rax, rdx);
            a.ret();
        }

        // Read an arbitrary MSR. Win64: RCX = index (rdmsr reads ECX), result
        // in EDX:EAX which we join into RAX.
        [CompileTimeAsm]
        private static partial int EmitReadMsrBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitReadMsrBootAsm))]
        private static void EmitReadMsrBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.rdmsr();
            a.shl(rdx, 32);
            a.or(rax, rdx);
            a.ret();
        }

        // Write an arbitrary MSR. Win64: RCX = index, RDX = value. wrmsr wants
        // the value split across EDX:EAX, so the low half moves to EAX first —
        // doing it the other way round would clobber the source.
        [CompileTimeAsm]
        private static partial int EmitWriteMsrBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitWriteMsrBootAsm))]
        private static void EmitWriteMsrBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.mov(rax, rdx);
            a.shr(rdx, 32);
            a.wrmsr();
            a.ret();
        }

        // mov rax, r8 ; lock cmpxchg [rcx], rdx ; ret
        [CompileTimeAsm]
        private static partial int EmitCmpXchg64BootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitCmpXchg64BootAsm))]
        private static void EmitCmpXchg64BootAsm_Body(Iced.Intel.Assembler a)
        {
            a.mov(rax, r8);
            a.db(0xF0);                       // lock prefix
            a.cmpxchg(__qword_ptr[rcx], rdx);
            a.ret();
        }

        // mov eax, r8d ; lock cmpxchg [rcx], edx ; ret
        [CompileTimeAsm]
        private static partial int EmitCmpXchg32BootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitCmpXchg32BootAsm))]
        private static void EmitCmpXchg32BootAsm_Body(Iced.Intel.Assembler a)
        {
            a.mov(eax, r8d);
            a.db(0xF0);                       // lock prefix
            a.cmpxchg(__dword_ptr[rcx], edx);
            a.ret();
        }

        // xchg [rcx], rdx ; mov rax, rdx ; ret  (xchg mem is implicit-locked)
        [CompileTimeAsm]
        private static partial int EmitXchg64BootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitXchg64BootAsm))]
        private static void EmitXchg64BootAsm_Body(Iced.Intel.Assembler a)
        {
            a.xchg(__qword_ptr[rcx], rdx);
            a.mov(rax, rdx);
            a.ret();
        }

        [CompileTimeAsm]
        private static partial int EmitMemoryBarrierBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitMemoryBarrierBootAsm))]
        private static void EmitMemoryBarrierBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.mfence();
            a.ret();
        }

        // rdtsc ; shl rdx,32 ; or rax,rdx ; ret — the CPU's cycle counter.
        //
        // Paired with a known-rate timer this is the only way we have to learn
        // the ACTUAL clock the machine is running at, which is not the one on
        // the box: nothing in this system manages performance states, so the
        // firmware's parking choice stands.
        [CompileTimeAsm]
        private static partial int EmitReadTscBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitReadTscBootAsm))]
        private static void EmitReadTscBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.rdtsc();
            a.shl(rdx, 32);
            a.or(rax, rdx);
            a.ret();
        }

        // Read the SSE control word into EAX.
        //
        // The scratch slot is carved out with sub/add rather than written at
        // [rsp-8]: Win64 has NO red zone, so memory below RSP belongs to
        // nobody and an interrupt landing between the store and the load both
        // destroys the value and corrupts whatever the interrupt frame uses.
        [CompileTimeAsm]
        private static partial int EmitReadMxcsrBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitReadMxcsrBootAsm))]
        private static void EmitReadMxcsrBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.sub(rsp, 16);
            a.stmxcsr(__dword_ptr[rsp]);
            a.mov(eax, __dword_ptr[rsp]);
            a.add(rsp, 16);
            a.ret();
        }

        // Write it (RCX = value), same owned-scratch rule as above.
        [CompileTimeAsm]
        private static partial int EmitWriteMxcsrBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitWriteMxcsrBootAsm))]
        private static void EmitWriteMxcsrBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.sub(rsp, 16);
            a.mov(__dword_ptr[rsp], ecx);
            a.ldmxcsr(__dword_ptr[rsp]);
            a.add(rsp, 16);
            a.ret();
        }

        // fxsave [rcx] ; ret  (RCX = 512-byte 16-aligned buf).
        [CompileTimeAsm]
        private static partial int EmitFxsaveBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitFxsaveBootAsm))]
        private static void EmitFxsaveBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.fxsave(__qword_ptr[rcx]);
            a.ret();
        }

        // Sleep until the next interrupt.
        //
        // STI and HLT must be one stub, not two calls: on x86 the effect of
        // STI is delayed by exactly one instruction, so an interrupt that is
        // already pending is taken AFTER the HLT begins — the CPU wakes
        // immediately instead of sleeping through it. Split into two managed
        // calls, the window between them is real and the wake-up is lost until
        // the next tick.
        [CompileTimeAsm]
        private static partial int EmitStiHltBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitStiHltBootAsm))]
        private static void EmitStiHltBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.sti();
            a.hlt();
            a.ret();
        }

        // Counterpart of Fxsave: load FP/SIMD state from a 512-byte image.
        // RCX = buf (Win64 arg1). The image must come from a real FXSAVE —
        // FXRSTOR faults #GP on reserved MXCSR bits, so a hand-built one is
        // a trap.
        [CompileTimeAsm]
        private static partial int EmitFxrstorBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitFxrstorBootAsm))]
        private static void EmitFxrstorBootAsm_Body(Iced.Intel.Assembler a)
        {
            a.fxrstor(__qword_ptr[rcx]);
            a.ret();
        }

        // Phase E4 cooperative context switch + Phase E9.b gs-base swap.
        // RCX = curr ctx, RDX = next ctx. Saves 8 GPRs + FP of curr, swaps
        // RSP, restores 8 GPRs + FP of next, optionally loads new gs base
        // (IA32_GS_BASE MSR) from next.Teb if non-zero. Single forward jz
        // skips the wrmsr block when next.Teb == 0 (no per-thread TEB).
        [CompileTimeAsm]
        private static partial int EmitCoopSwitchBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitCoopSwitchBootAsm))]
        private static void EmitCoopSwitchBootAsm_Body(Iced.Intel.Assembler a)
        {
            var skipGs = a.CreateLabel();

            // Prologue: spill 8 callee-saved GPRs.
            a.push(rbx); a.push(rbp); a.push(rsi); a.push(rdi);
            a.push(r12); a.push(r13); a.push(r14); a.push(r15);

            // Save curr FP, swap RSP, restore next FP.
            a.fxsave(__qword_ptr[rcx + 0x10]);
            a.mov(__qword_ptr[rcx], rsp);
            a.mov(rsp, __qword_ptr[rdx]);
            a.fxrstor(__qword_ptr[rdx + 0x10]);

            // gs-base swap if next.Teb != 0
            a.mov(rax, __qword_ptr[rdx + 8]);
            a.test(rax, rax);
            a.jz(skipGs);
            a.mov(rdx, rax);
            a.shr(rdx, 32);
            // mov ecx, 0xC0000101 (IA32_GS_BASE) — walker uint-suffix workaround.
            a.db(0xB9, 0x01, 0x01, 0x00, 0xC0);
            a.wrmsr();

            a.Label(ref skipGs);

            // Epilogue: restore in reverse order.
            a.pop(r15); a.pop(r14); a.pop(r13); a.pop(r12);
            a.pop(rdi); a.pop(rsi); a.pop(rbp); a.pop(rbx);
            a.ret();
        }

        // Resume from Idt.InterruptFrame: restore 14 GPRs, lea rsp→&Rip,
        // restore Rcx last, iretq. RCX = frame*. See X64Asm.TryResumeFrame
        // header for field layout. Linear, no branches.
        [CompileTimeAsm]
        private static partial int EmitResumeBootAsm(byte* dst);

        [CompileTimeAsmBody(nameof(EmitResumeBootAsm))]
        private static void EmitResumeBootAsm_Body(Iced.Intel.Assembler a)
        {
            // Restore x87/SSE first, while RCX still points at the frame.
            // The common stub saved it at frame - FpAreaBytes; without this
            // the handler's own use of XMM leaks into the code we are about
            // to resume. See IdtTrampolines.EmitCommonStub_Body.
            // 520 spelled out, not IdtTrampolines.FpAreaBytes — the walker
            // takes literals only. Change one, change both.
            a.fxrstor(__zmmword_ptr[rcx - 520]);

            a.mov(rax, __qword_ptr[rcx +   8]);
            a.mov(rdx, __qword_ptr[rcx +  24]);
            a.mov(rbx, __qword_ptr[rcx +  32]);
            a.mov(rsi, __qword_ptr[rcx +  40]);
            a.mov(rdi, __qword_ptr[rcx +  48]);
            a.mov(rbp, __qword_ptr[rcx +  56]);
            a.mov(r8,  __qword_ptr[rcx +  64]);
            a.mov(r9,  __qword_ptr[rcx +  72]);
            a.mov(r10, __qword_ptr[rcx +  80]);
            a.mov(r11, __qword_ptr[rcx +  88]);
            a.mov(r12, __qword_ptr[rcx +  96]);
            a.mov(r13, __qword_ptr[rcx + 104]);
            a.mov(r14, __qword_ptr[rcx + 112]);
            a.mov(r15, __qword_ptr[rcx + 120]);
            a.lea(rsp, __qword_ptr[rcx + 0x90]);
            a.mov(rcx, __qword_ptr[rcx + 16]);
            a.iretq();
        }
    }
}
