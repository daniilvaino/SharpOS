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
    internal static unsafe partial class X64Asm
    {
        private const uint StiOffset = 0;
        private const uint CliOffset = 16;
        private const uint HltOffset = 32;
        private const uint MinBufferSize = 64;

        // Xsetbv parametric stub at 0x60 (96..~111). Clear of the
        // wrmsr GS-base stub at 64..82 (CoreClrProbe) and the SehDispatch
        // capture block starting at 0x80=128.
        private const uint XsetbvOffset    = 0x60;
        private const uint XsetbvMinBuffer = 0x80;

        // ReadCr4 stub at 0x70 (112..115). Used to gate Xsetbv calls on
        // CR4.OSXSAVE (bit 18) — xsetbv #UDs if OSXSAVE=0. Buffer 1024.
        private const uint ReadCr4Offset    = 0x70;
        private const uint ReadCr4MinBuffer = 0x80;

        // Phase E2 TEB facade stubs — all live past SehDispatch capture
        // (0x80..~0x10C). 32-byte slots for readability.
        //   0x140: ReadGsQword(offset)  → gs:[offset] (parametric)
        //   0x160: WriteGsBaseMsr(value) → IA32_GS_BASE = value (parametric)
        //   0x180: ReadGsBaseMsr()       → IA32_GS_BASE (no args)
        private const uint ReadGsQwordOffset      = 0x140;
        private const uint WriteGsBaseMsrOffset   = 0x160;
        private const uint ReadGsBaseMsrOffset    = 0x180;
        private const uint TebStubsMinBuffer      = 0x1A0;

        // Phase E3 atomic primitives — 32-byte slots.
        //   0x1A0: CmpXchg64(loc, value, comparand) → old value
        //   0x1C0: Xchg64(loc, value)              → old value
        //   0x1E0: MemoryBarrier()                 → void (mfence)
        private const uint CmpXchg64Offset       = 0x1A0;
        private const uint Xchg64Offset          = 0x1C0;
        private const uint MemoryBarrierOffset   = 0x1E0;
        private const uint AtomicsStubsMinBuffer = 0x200;

        // Phase E4 cooperative-switch primitives.
        //   0x28C: Fxsave(buf)               → fxsave [rcx]; ret      (4 bytes)
        //   0x380: CoopSwitch(curr, next)    → save 8 GPRs + fxsave + RSP swap
        //                                      + fxrstor + pop 8 + ret (~50 bytes)
        // Buffer is 1024 bytes; free zones used: 0x28C..0x300 (~116 B) and
        // 0x380..0x400 (~128 B). If future phases overflow, bump
        // UefiBootInfoBuilder.AsmBufferSize.
        private const uint FxsaveOffset          = 0x28C;
        private const uint CoopSwitchOffset      = 0x380;
        private const uint CoopSwitchMinBuffer   = 0x400;

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
        private static bool s_xsetbvReady;
        private static delegate* unmanaged<uint, ulong, void> s_xsetbv;
        private static bool s_readCr4Ready;
        private static delegate* unmanaged<ulong> s_readCr4;
        private static bool s_readGsQwordReady;
        private static delegate* unmanaged<ulong, ulong> s_readGsQword;
        private static bool s_writeGsBaseMsrReady;
        private static delegate* unmanaged<ulong, void> s_writeGsBaseMsr;
        private static bool s_readGsBaseMsrReady;
        private static delegate* unmanaged<ulong> s_readGsBaseMsr;
        private static bool s_cmpXchg64Ready;
        private static delegate* unmanaged<ulong*, ulong, ulong, ulong> s_cmpXchg64;
        private static bool s_xchg64Ready;
        private static delegate* unmanaged<ulong*, ulong, ulong> s_xchg64;
        private static bool s_memoryBarrierReady;
        private static delegate* unmanaged<void> s_memoryBarrier;
        private static bool s_fxsaveReady;
        private static delegate* unmanaged<byte*, void> s_fxsave;
        private static bool s_coopSwitchReady;
        private static delegate* unmanaged<byte*, byte*, void> s_coopSwitch;
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

        // xsetbv — set extended control register XCR[selector] = value.
        // Win64 ABI: RCX = selector (uint), RDX = value (ulong).
        // Selector 0 == XCR0 (FP/SIMD enable mask). Bit 0 = x87, bit 1 = SSE,
        // bit 2 = AVX-YMM, bits 5-7 = AVX-512 ZMM (E0 PV2 lock keeps it at
        // 0x3 — FXSAVE-only safe; see docs/threading-architecture.md §5).
        // Returns false if exec buffer unavailable; true on issue (caller
        // observes effect via #UD on subsequent VEX or via cpuid OSXSAVE).
        public static bool Xsetbv(uint selector, ulong value)
        {
            if (s_execBuffer == null || s_execBufferSize < XsetbvMinBuffer)
                return false;
            if (!s_xsetbvReady)
            {
                EmitXsetbv((byte*)s_execBuffer + XsetbvOffset);
                s_xsetbv = (delegate* unmanaged<uint, ulong, void>)((byte*)s_execBuffer + XsetbvOffset);
                s_xsetbvReady = true;
            }
            s_xsetbv(selector, value);
            return true;
        }

        // step 118 Wave 4 — body now compile-time emitted via
        // EmitXsetbvBootAsm (BootAsm.Generator + Iced). See X64Asm.BootAsm.cs.
        // Original byte sequence preserved here as reference:
        //   48 89 D0        mov rax, rdx        ; EAX = low 32 of value
        //   48 C1 EA 20     shr rdx, 32         ; EDX = high 32 of value
        //   0F 01 D1        xsetbv              ; XCR[ECX] = EDX:EAX
        //   C3              ret
        private static void EmitXsetbv(byte* p) => EmitXsetbvBootAsm(p);

        // Read CR4. Use to gate features that depend on CR4 bits — e.g.
        // bit 18 (OSXSAVE) controls whether xsetbv is legal. Returns false
        // if exec buffer unavailable.
        public static bool TryReadCr4(out ulong cr4)
        {
            cr4 = 0;
            if (s_execBuffer == null || s_execBufferSize < ReadCr4MinBuffer)
                return false;
            if (!s_readCr4Ready)
            {
                byte* p = (byte*)s_execBuffer + ReadCr4Offset;
                EmitReadCr4BootAsm(p);            // mov rax, cr4 ; ret
                s_readCr4 = (delegate* unmanaged<ulong>)p;
                s_readCr4Ready = true;
            }
            cr4 = s_readCr4();
            return true;
        }

        // Read an 8-byte value at gs:[offset]. RCX=offset (Win64 arg1).
        // Phase E2 — TEB facade reads (e.g., gs:[0x30] for Self,
        // gs:[0x58] for TLS slot array). Returns 0 if exec buffer is
        // unavailable (callers either trust the env or check separately).
        public static ulong ReadGsQword(uint offset)
        {
            if (s_execBuffer == null || s_execBufferSize < TebStubsMinBuffer)
                return 0;
            if (!s_readGsQwordReady)
            {
                byte* p = (byte*)s_execBuffer + ReadGsQwordOffset;
                EmitReadGsQwordBootAsm(p);        // gs: mov rax, [rcx] ; ret
                s_readGsQword = (delegate* unmanaged<ulong, ulong>)p;
                s_readGsQwordReady = true;
            }
            return s_readGsQword(offset);
        }

        // Write IA32_GS_BASE (MSR 0xC0000101) — sets the gs base to `value`.
        // After this, gs:[N] resolves to (value + N). Reusable on every
        // cooperative context switch (E4) to swap TEBs.
        public static bool WriteGsBaseMsr(ulong value)
        {
            if (s_execBuffer == null || s_execBufferSize < TebStubsMinBuffer)
                return false;
            if (!s_writeGsBaseMsrReady)
            {
                byte* p = (byte*)s_execBuffer + WriteGsBaseMsrOffset;
                EmitWriteGsBaseMsrBootAsm(p);     // IA32_GS_BASE := rcx
                s_writeGsBaseMsr = (delegate* unmanaged<ulong, void>)p;
                s_writeGsBaseMsrReady = true;
            }
            s_writeGsBaseMsr(value);
            return true;
        }

        // Read IA32_GS_BASE — used to capture the current gs base before
        // a swap so the caller can restore it (probes, exception handlers
        // that need to save/restore TEB context).
        public static bool ReadGsBaseMsr(out ulong value)
        {
            value = 0;
            if (s_execBuffer == null || s_execBufferSize < TebStubsMinBuffer)
                return false;
            if (!s_readGsBaseMsrReady)
            {
                byte* p = (byte*)s_execBuffer + ReadGsBaseMsrOffset;
                EmitReadGsBaseMsrBootAsm(p);      // RAX := IA32_GS_BASE
                s_readGsBaseMsr = (delegate* unmanaged<ulong>)p;
                s_readGsBaseMsrReady = true;
            }
            value = s_readGsBaseMsr();
            return true;
        }

        // Phase E3 — atomic compare-exchange on a 64-bit memory location.
        // Win64 ABI:  RCX = location, RDX = value, R8 = comparand.
        // Semantics: if *location == comparand → *location = value, return
        // comparand. Else → return *location (unchanged). LOCK prefix on
        // cmpxchg makes the read-modify-write atomic across all cores.
        // Returns the value that WAS at *location before the call (matches
        // .NET Interlocked.CompareExchange convention).
        //
        //   4C 89 C0          mov rax, r8        ; RAX = comparand
        //   F0 48 0F B1 11    lock cmpxchg [rcx], rdx
        //   C3                ret                ; RAX = old *location
        public static ulong CmpXchg64(ulong* location, ulong value, ulong comparand)
        {
            if (s_execBuffer == null || s_execBufferSize < AtomicsStubsMinBuffer)
                return 0;
            if (!s_cmpXchg64Ready)
            {
                byte* p = (byte*)s_execBuffer + CmpXchg64Offset;
                EmitCmpXchg64BootAsm(p);          // mov rax,r8 ; lock cmpxchg [rcx],rdx
                s_cmpXchg64 = (delegate* unmanaged<ulong*, ulong, ulong, ulong>)p;
                s_cmpXchg64Ready = true;
            }
            return s_cmpXchg64(location, value, comparand);
        }

        // Phase E3 — atomic exchange. Win64 ABI: RCX = location, RDX = value.
        // XCHG with a memory operand is implicitly locked (no F0 prefix
        // needed). Sets *location = value, returns previous *location.
        //
        //   48 87 11    xchg [rcx], rdx        ; swap; RDX = old *location
        //   48 89 D0    mov  rax, rdx          ; RAX = old (return value)
        //   C3          ret
        public static ulong Xchg64(ulong* location, ulong value)
        {
            if (s_execBuffer == null || s_execBufferSize < AtomicsStubsMinBuffer)
                return 0;
            if (!s_xchg64Ready)
            {
                byte* p = (byte*)s_execBuffer + Xchg64Offset;
                EmitXchg64BootAsm(p);             // xchg [rcx],rdx ; mov rax,rdx
                s_xchg64 = (delegate* unmanaged<ulong*, ulong, ulong>)p;
                s_xchg64Ready = true;
            }
            return s_xchg64(location, value);
        }

        // Phase E3 — full memory barrier (mfence). Serialises all prior
        // loads + stores against all subsequent loads + stores. Required
        // between unrelated atomic ops where ordering matters but cmpxchg/
        // xchg's implicit lock isn't on the dependency.
        //
        //   0F AE F0    mfence
        //   C3          ret
        public static void MemoryBarrier()
        {
            if (s_execBuffer == null || s_execBufferSize < AtomicsStubsMinBuffer)
                return;
            if (!s_memoryBarrierReady)
            {
                byte* p = (byte*)s_execBuffer + MemoryBarrierOffset;
                EmitMemoryBarrierBootAsm(p);      // mfence ; ret
                s_memoryBarrier = (delegate* unmanaged<void>)p;
                s_memoryBarrierReady = true;
            }
            s_memoryBarrier();
        }

        // Phase E4 — write the current FP/SIMD state to a 512-byte
        // 16-byte aligned buffer. Used at thread spawn to give each new
        // Thread a valid initial FXSAVE image (so the first fxrstor on
        // switch-in doesn't load garbage). RCX = buf (Win64 arg1).
        //
        //   0F AE 01    fxsave [rcx]
        //   C3          ret
        public static void Fxsave(byte* buf)
        {
            if (buf == null) return;
            if (s_execBuffer == null || s_execBufferSize < CoopSwitchMinBuffer)
                return;
            if (!s_fxsaveReady)
            {
                byte* p = (byte*)s_execBuffer + FxsaveOffset;
                EmitFxsaveBootAsm(p);             // fxsave [rcx] ; ret
                s_fxsave = (delegate* unmanaged<byte*, void>)p;
                s_fxsaveReady = true;
            }
            s_fxsave(buf);
        }

        // Phase E4 cooperative context switch + Phase E9.b gs-base swap.
        // RCX = curr context block, RDX = next context block.
        // ContextBlock layout (16-byte aligned):
        //   +0x00 SavedRsp     (ulong) — updated here
        //   +0x08 Teb          (ulong) — IA32_GS_BASE to load post-switch;
        //                                0 = skip (no per-thread TEB)
        //   +0x10 FxsaveArea   (512 bytes, 16-byte aligned)
        //
        // Saves 8 callee-saved GPRs + FP state of CURR, swaps RSP from
        // CURR to NEXT, restores 8 callee-saved GPRs + FP state of NEXT,
        // optionally loads new gs base (IA32_GS_BASE MSR) from next.Teb,
        // returns into NEXT's resumption point.
        //
        //   53            push rbx
        //   55            push rbp
        //   56            push rsi
        //   57            push rdi
        //   41 54         push r12
        //   41 55         push r13
        //   41 56         push r14
        //   41 57         push r15
        //   0F AE 41 10   fxsave  [rcx + 0x10]
        //   48 89 21      mov     [rcx], rsp
        //   48 8B 22      mov     rsp, [rdx]
        //   0F AE 4A 10   fxrstor [rdx + 0x10]
        //   ; Phase E9.b: optional gs-base swap.
        //   48 8B 42 08   mov     rax, [rdx + 8]    ; rax = next.Teb
        //   48 85 C0      test    rax, rax
        //   74 0E         jz      .skip_gs          ; skip if 0
        //   48 89 C2      mov     rdx, rax          ; rdx = Teb (full)
        //   48 C1 EA 20   shr     rdx, 32           ; edx = high 32
        //   B9 01 01 00 C0  mov   ecx, 0xC0000101  ; IA32_GS_BASE MSR
        //   0F 30         wrmsr                    ; gs_base = edx:eax
        //   ; .skip_gs:
        //   41 5F         pop r15
        //   41 5E         pop r14
        //   41 5D         pop r13
        //   41 5C         pop r12
        //   5F            pop rdi
        //   5E            pop rsi
        //   5D            pop rbp
        //   5B            pop rbx
        //   C3            ret
        // Total: 39 + 23 = 62 bytes.
        public static bool CoopSwitch(byte* currCtx, byte* nextCtx)
        {
            if (currCtx == null || nextCtx == null) return false;
            if (s_execBuffer == null || s_execBufferSize < CoopSwitchMinBuffer)
                return false;
            if (!s_coopSwitchReady)
            {
                byte* p = (byte*)s_execBuffer + CoopSwitchOffset;
                // step 119 Wave 5 — compile-time codegen via BootAsm.Generator
                // (walker now supports labels for the `jz .skip_gs` forward
                // jump). See X64Asm.BootAsm.cs.
                EmitCoopSwitchBootAsm(p);
                s_coopSwitch = (delegate* unmanaged<byte*, byte*, void>)p;
                s_coopSwitchReady = true;
            }
            s_coopSwitch(currCtx, nextCtx);
            return true;
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

        // step 118 Wave 4 — body now compile-time emitted via
        // EmitResumeBootAsm (BootAsm.Generator + Iced). See X64Asm.BootAsm.cs.
        // 14× `mov reg, [rcx+disp]`, `lea rsp, [rcx+0x90]`, restore Rcx, iretq.
        // Iced picks disp8 for offsets <128 → resulting stub is shorter than
        // the original force-disp32 form; same semantics. RCX = frame*.
        private static void EmitResume(byte* p)
        {
            EmitResumeBootAsm(p);
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

            // step 119 Wave 5 — compile-time codegen via BootAsm.Generator.
            EmitStiBootAsm(stub + StiOffset);
            EmitCliBootAsm(stub + CliOffset);
            EmitHltBootAsm(stub + HltOffset);

            s_sti = (delegate* unmanaged<void>)(stub + StiOffset);
            s_cli = (delegate* unmanaged<void>)(stub + CliOffset);
            s_hlt = (delegate* unmanaged<void>)(stub + HltOffset);
            s_initialized = true;
            return true;
        }
    }
}
