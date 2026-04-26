namespace OS.Hal.Idt
{
    // x64 shellcode that bridges CPU exception entry → managed dispatcher.
    //
    // Layout in the IDT exec buffer (4 KB IDT + 4 KB exec):
    //   0..4095        IDT (256 × 16-byte gate descriptors)
    //   4096..4191     CommonStub (~96 bytes)
    //   4192..4192+N   PerVectorStub × 32 (16 bytes each = 512 bytes)
    //
    // We wire only vectors 0..31 — the CPU-reserved exception range. Higher
    // vectors (32..255) are left zero in the IDT; if hardware delivers one
    // it triple-faults (until SUPER-Phase 5 wires hardware IRQs through MSI).
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

    internal static unsafe class IdtTrampolines
    {
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
        public static void WriteCommonStub(byte* p, void* dispatcherPtr, out uint length)
        {
            int i = 0;

            // push r15..r8 (8 regs, 2 bytes each)
            p[i++] = 0x41; p[i++] = 0x57;   // push r15
            p[i++] = 0x41; p[i++] = 0x56;   // push r14
            p[i++] = 0x41; p[i++] = 0x55;   // push r13
            p[i++] = 0x41; p[i++] = 0x54;   // push r12
            p[i++] = 0x41; p[i++] = 0x53;   // push r11
            p[i++] = 0x41; p[i++] = 0x52;   // push r10
            p[i++] = 0x41; p[i++] = 0x51;   // push r9
            p[i++] = 0x41; p[i++] = 0x50;   // push r8

            // push rbp, rdi, rsi, rbx, rdx, rcx, rax (1 byte each)
            p[i++] = 0x55;                   // push rbp
            p[i++] = 0x57;                   // push rdi
            p[i++] = 0x56;                   // push rsi
            p[i++] = 0x53;                   // push rbx
            p[i++] = 0x52;                   // push rdx
            p[i++] = 0x51;                   // push rcx
            p[i++] = 0x50;                   // push rax

            // mov rax, cr2 ; push rax
            // Encoding: 0F 20 /r where reg field = CRn. ModRM D0 = 11 010 000:
            // mod=11, reg=010 (CR2), rm=000 (RAX). The earlier D8 was reg=011
            // (CR3) — wrong source; produced misleading dumps where "CR2" was
            // actually CR3 (kernel page-table physical address).
            p[i++] = 0x0F; p[i++] = 0x20; p[i++] = 0xD0;
            p[i++] = 0x50;

            // mov rcx, rsp     ; arg1 = frame*
            p[i++] = 0x48; p[i++] = 0x89; p[i++] = 0xE1;

            // sub rsp, 0x28
            p[i++] = 0x48; p[i++] = 0x83; p[i++] = 0xEC; p[i++] = 0x28;

            // mov rax, [rip + dispOffset]   — disp32 patched after we know offset
            p[i++] = 0x48; p[i++] = 0x8B; p[i++] = 0x05;
            int dispRelOffsetSlot = i;       // disp32 lives here
            p[i++] = 0x00; p[i++] = 0x00; p[i++] = 0x00; p[i++] = 0x00;

            // call rax
            p[i++] = 0xFF; p[i++] = 0xD0;

            // 1: hlt ; jmp 1b
            int haltLoopStart = i;
            p[i++] = 0xF4;
            p[i++] = 0xEB; p[i++] = 0xFE;    // jmp -2 (back to hlt)

            // 8-byte alignment for the qword pointer
            while ((i & 7) != 0)
                p[i++] = 0x90;               // nop

            int qwordSlot = i;
            // The disp32 in `mov rax, [rip+disp32]` is relative to the *next*
            // instruction (i.e. the byte right after the disp32 field).
            int dispRelEnd = dispRelOffsetSlot + 4;
            int rel32 = qwordSlot - dispRelEnd;
            p[dispRelOffsetSlot + 0] = (byte)(rel32 & 0xFF);
            p[dispRelOffsetSlot + 1] = (byte)((rel32 >> 8) & 0xFF);
            p[dispRelOffsetSlot + 2] = (byte)((rel32 >> 16) & 0xFF);
            p[dispRelOffsetSlot + 3] = (byte)((rel32 >> 24) & 0xFF);

            // Write the qword pointer itself
            ulong dispAddr = (ulong)dispatcherPtr;
            for (int k = 0; k < 8; k++)
                p[qwordSlot + k] = (byte)((dispAddr >> (k * 8)) & 0xFF);

            length = (uint)(qwordSlot + 8);
        }

        // Write a per-vector entry stub. Stub jumps via rel32 to commonStub.
        // `stubAddr` is the absolute address of this stub (needed to compute
        // the rel32 displacement). `commonStubAddr` is the absolute address
        // of the common stub.
        public static void WriteVectorStub(byte* stub, int vector, byte* commonStubAddr)
        {
            int i = 0;

            if (!VectorHasErrorCode(vector))
            {
                // push 0 (dummy error code)
                stub[i++] = 0x6A; stub[i++] = 0x00;
            }

            // push <vector>  (vectors 0..31 fit in imm8)
            stub[i++] = 0x6A; stub[i++] = (byte)vector;

            // jmp rel32 to common stub. rel32 is relative to the byte AFTER
            // the disp32 field (i.e. (stub + i + 5) is the rip when the jmp
            // takes effect).
            stub[i++] = 0xE9;
            byte* afterDisp = stub + i + 4;
            long delta = (long)commonStubAddr - (long)afterDisp;
            stub[i++] = (byte)(delta & 0xFF);
            stub[i++] = (byte)((delta >> 8) & 0xFF);
            stub[i++] = (byte)((delta >> 16) & 0xFF);
            stub[i++] = (byte)((delta >> 24) & 0xFF);

            // pad with NOPs to VectorStubSize
            while (i < (int)VectorStubSize)
                stub[i++] = 0x90;
        }
    }
}
