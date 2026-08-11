namespace OS.Hal.Idt
{
    // x64 shellcode that bridges CPU exception entry → managed dispatcher.
    //
    // Layout in the IDT exec buffer (4 KB IDT + 4 KB exec):
    //   0..4095        IDT (256 × 16-byte gate descriptors)
    //   4096..4191     CommonStub (~96 bytes)
    //   4192..4192+N   PerVectorStub × 32 (16 bytes each = 512 bytes)
    //
    // Install() wires vectors 0..31 — the CPU-reserved exception range. The
    // rest of the table is inherited from the firmware and, after
    // ExitBootServices, selectively replaced by Idt.TryWireIrqVector for the
    // vectors we raise ourselves (the APIC timer and its spurious vector).
    // The stubs are identical: a vector above 31 never carries an error code,
    // which is exactly the no-error form below.
    //
    // ─────────────────────────────────────────────────────────────────────
    // Per-vector entry stub (16 bytes, padded with NOPs):
    //
    //   For vectors WITHOUT error code (0,1,2,3,4,5,6,7,9,15..18,20..31):
    //     6A 00              push 0          ; dummy error code
    //     6A NN              push <vec>      ; vector number
    //     E9 disp32          jmp commonstub  ; relative jump
    //     <NOP fill>
    //
    //   For vectors WITH error code (8,10,11,12,13,14,17,21):
    //     6A NN              push <vec>      ; vector number (no dummy)
    //     E9 disp32          jmp commonstub
    //     <NOP fill>
    //
    //   The dummy push for non-error vectors uniformizes the stack frame so
    //   the common stub can do a single uniform unwind.
    //
    // ─────────────────────────────────────────────────────────────────────
    // Common stub:
    //
    //   ; on entry, stack (low to high):
    //   ;   [vector] [errcode] [rip] [cs] [rflags] [rsp] [ss]
    //
    //     push   r15                    ; 41 57
    //     push   r14                    ; 41 56
    //     push   r13                    ; 41 55
    //     push   r12                    ; 41 54
    //     push   r11                    ; 41 53
    //     push   r10                    ; 41 52
    //     push   r9                     ; 41 51
    //     push   r8                     ; 41 50
    //     push   rbp                    ; 55
    //     push   rdi                    ; 57
    //     push   rsi                    ; 56
    //     push   rbx                    ; 53
    //     push   rdx                    ; 52
    //     push   rcx                    ; 51
    //     push   rax                    ; 50
    //     mov    rax, cr2               ; 0F 20 D0
    //     push   rax                    ; 50
    //     ; Stack now matches InterruptFrame layout exactly.
    //     mov    rcx, rsp               ; 48 89 E1   ; arg1 = frame*
    //     sub    rsp, 0x28              ; 48 83 EC 28 ; shadow + 8-byte align
    //     mov    rax, [rip + dispPtr]   ; 48 8B 05 ?? ?? ?? ??
    //     call   rax                    ; FF D0
    //   1: hlt                          ; F4
    //     jmp    1b                     ; EB FD
    //     dispPtr: .qword <addr of IdtDispatcher>
    //
    //   Dispatcher is `[UnmanagedCallersOnly]` so the [rip+ptr] indirect
    //   load is valid — the address at dispPtr was patched in by Install().
    //   Stack alignment at the call: 5 (CPU) + 1 (errcode) + 1 (vector) +
    //   15 (regs) + 1 (cr2) = 23 qwords from interrupt → caller-base + 8.
    //   `sub rsp, 0x28` adjusts by 5 qwords more → 28 qwords = 0 mod 16. ✓
    // ─────────────────────────────────────────────────────────────────────

    internal static unsafe partial class IdtTrampolines
    {
        // x87/SSE save area the common stub reserves BELOW the frame, so
        // handlers that return do not corrupt the interrupted code's XMM
        // registers. 512 bytes of FXSAVE image + 8 to land the area on a
        // 16-byte boundary (see EmitCommonStub_Body for the arithmetic).
        //
        // The area lives at frame - FpAreaBytes; X64Asm's resume stub restores
        // from that same offset. Both sides must agree — this constant is the
        // agreement.
        public const int FpAreaBytes = 520;

        public const uint IdtSize = 4096;                // 256 × 16
        public const uint CommonStubOffset = 4096;
        public const uint CommonStubMaxSize = 96;
        public const uint VectorStubsOffset = 4192;
        public const uint VectorStubSize = 16;
        public const uint VectorCount = 32;
        public const uint VectorStubsTotalSize = VectorCount * VectorStubSize;
        public const uint TotalBufferSize = VectorStubsOffset + VectorStubsTotalSize;

        // Bitmap: vectors 0..31, bit set = has error code pushed by CPU.
        // Per Intel SDM Vol. 3A § 6.15: #DF(8), #TS(10), #NP(11), #SS(12),
        //                              #GP(13), #PF(14), #AC(17), #SX(21).
        private const uint ErrorCodeMask =
            (1u << 8) | (1u << 10) | (1u << 11) | (1u << 12) |
            (1u << 13) | (1u << 14) | (1u << 17) | (1u << 21);

        public static bool VectorHasErrorCode(int vector)
        {
            if ((uint)vector >= 32) return false;
            return ((ErrorCodeMask >> vector) & 1u) != 0;
        }

        // dispatcherPtr is the address of the [UnmanagedCallersOnly] managed
        // dispatcher. We patch it into the rip-relative slot at end of common
        // stub so the indirect call lands there.
        //
        // step 119 Wave 5 — body now compile-time emitted via EmitCommonStub
        // (BootAsm.Generator + Iced). See IdtTrampolines.BootAsm.cs.
        public static void WriteCommonStub(byte* p, void* dispatcherPtr, out uint length)
        {
            length = (uint)EmitCommonStub(p, dispatcherPtr);
        }

        // Write a per-vector entry stub. Stub jumps via rel32 to commonStub.
        // `commonStubAddr` is the absolute address of the common stub.
        //
        // step 119 Wave 6 — compile-time codegen via BootAsm.Generator using
        // PushImm32Hole (vector #) + JmpRelHole (commonStub displacement).
        // Vector # encoded as imm32 (push imm32, 5 bytes) instead of the
        // imm8 form — 16-byte slot still has room (12 vs 9 bytes used).
        // Two body variants by error-code-mask: with-dummy-push vs without.
        public static void WriteVectorStub(byte* stub, int vector, byte* commonStubAddr)
        {
            if (VectorHasErrorCode(vector))
                EmitVectorStubWithErr(stub, (uint)vector, commonStubAddr);
            else
                EmitVectorStubNoErr(stub, (uint)vector, commonStubAddr);
        }
    }
}
