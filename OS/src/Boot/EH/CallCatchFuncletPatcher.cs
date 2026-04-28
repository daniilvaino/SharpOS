using OS.Hal;

namespace OS.Boot.EH
{
    // Phase 1 step 5.5a — patches CallCatchFuncletStub.RhpCallCatchFunclet
    // body with byte-array shellcode (~140 bytes). Same patcher pattern as
    // ByRefAssignRefPatcher / PortIoPatcher / CaptureContextPatcher /
    // ThrowExPatcher.
    //
    // STACK LAYOUT after prologue (8 push'ей + sub rsp, 0x48):
    //
    //   Entry:                 RSP % 16 == 8 (Win64 ABI: caller's CALL pushed return addr).
    //   After 8 pushes (0x40): RSP % 16 still == 8.
    //   After sub rsp, 0x48:   RSP % 16 == 0. ✓ aligned for next CALL.
    //
    //   [rsp + 0x00 .. 0x1F]   shadow space for handler call (0x20)
    //   [rsp + 0x20 .. 0x27]   saved exception object   (RCX entry value)
    //   [rsp + 0x28 .. 0x2F]   saved handler IP         (RDX entry value)
    //   [rsp + 0x30 .. 0x37]   saved REGDISPLAY*        (R8  entry value)
    //   [rsp + 0x38 .. 0x3F]   saved ExInfo*            (R9  entry value)
    //   [rsp + 0x40 .. 0x47]   alignment pad
    //
    // SHELLCODE BYTES (annotated for review):
    //
    //   Prologue: spill 8 nonvols
    //     41 57          push r15
    //     41 56          push r14
    //     41 55          push r13
    //     41 54          push r12
    //     53             push rbx
    //     56             push rsi
    //     57             push rdi
    //     55             push rbp
    //     48 81 EC 48 00 00 00   sub rsp, 0x48
    //
    //   Save 4 args at [rsp+0x20..0x3F]
    //     48 89 4C 24 20    mov [rsp+0x20], rcx   ; exception
    //     48 89 54 24 28    mov [rsp+0x28], rdx   ; handler IP
    //     4C 89 44 24 30    mov [rsp+0x30], r8    ; REGDISPLAY*
    //     4C 89 4C 24 38    mov [rsp+0x38], r9    ; ExInfo*
    //
    //   Restore parent's nonvols from REGDISPLAY (R8 still has rd*)
    //     49 8B 40 18       mov rax, [r8+0x18]    ; pRbx
    //     48 8B 18          mov rbx, [rax]
    //     49 8B 40 20       mov rax, [r8+0x20]    ; pRbp
    //     48 8B 28          mov rbp, [rax]
    //     49 8B 40 28       mov rax, [r8+0x28]    ; pRsi
    //     48 8B 30          mov rsi, [rax]
    //     49 8B 40 30       mov rax, [r8+0x30]    ; pRdi
    //     48 8B 38          mov rdi, [rax]
    //     49 8B 40 58       mov rax, [r8+0x58]    ; pR12
    //     4C 8B 20          mov r12, [rax]
    //     49 8B 40 60       mov rax, [r8+0x60]    ; pR13
    //     4C 8B 28          mov r13, [rax]
    //     49 8B 40 68       mov rax, [r8+0x68]    ; pR14
    //     4C 8B 30          mov r14, [rax]
    //     49 8B 40 70       mov rax, [r8+0x70]    ; pR15
    //     4C 8B 38          mov r15, [rax]
    //
    //   Setup handler call args (funclet ABI: RCX = establisher SP, RDX = exception)
    //     49 8B 48 78       mov rcx, [r8+0x78]    ; arg1 = REGDISPLAY.SP
    //     48 8B 54 24 20    mov rdx, [rsp+0x20]   ; arg2 = exception
    //     FF 54 24 28       call qword ptr [rsp+0x28]   ; call handler
    //
    //   After call: RAX = resume IP. Volatile regs (R8/R9 etc) clobbered.
    //   Nonvols still match REGDISPLAY-restored values (handler ABI preserves them).
    //
    //   Pop ExInfo head: s_head = exInfo->PrevExInfo
    //     4C 8B 44 24 38    mov r8, [rsp+0x38]    ; reload ExInfo*
    //     4D 8B 08          mov r9, [r8]          ; r9 = exInfo->PrevExInfo (offset 0x00)
    //     49 BA <8 bytes>   mov r10, &s_head      ; placeholder patched at install
    //     4D 89 0A          mov [r10], r9         ; *s_head = prev
    //
    //   Non-local transfer: mov rsp, REGDISPLAY.SP; jmp rax
    //     4C 8B 44 24 30    mov r8, [rsp+0x30]    ; reload REGDISPLAY*
    //     49 8B 60 78       mov rsp, [r8+0x78]    ; rsp = REGDISPLAY.SP (DESTROYS frame!)
    //     FF E0             jmp rax               ; resume in parent method
    //
    // Total: 12 + 7 + 20 + 56 + 13 + 21 + 11 = 140 bytes.
    internal static unsafe class CallCatchFuncletPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)CallCatchFuncletStub.GetMethodAddress();
            if (target == null) return false;

            byte** headAddr = ExInfoHead.GetHeadAddress();

            int p = 0;

            // ── Prologue: spill 8 nonvols (12 bytes) ───────────────────
            target[p++] = 0x41; target[p++] = 0x57;          // push r15
            target[p++] = 0x41; target[p++] = 0x56;          // push r14
            target[p++] = 0x41; target[p++] = 0x55;          // push r13
            target[p++] = 0x41; target[p++] = 0x54;          // push r12
            target[p++] = 0x53;                              // push rbx
            target[p++] = 0x56;                              // push rsi
            target[p++] = 0x57;                              // push rdi
            target[p++] = 0x55;                              // push rbp

            // sub rsp, 0x48 (7 bytes)
            target[p++] = 0x48; target[p++] = 0x81; target[p++] = 0xEC;
            target[p++] = 0x48; target[p++] = 0x00; target[p++] = 0x00; target[p++] = 0x00;

            // ── Save 4 args (20 bytes) ─────────────────────────────────
            // mov [rsp+0x20], rcx  — exception
            target[p++] = 0x48; target[p++] = 0x89; target[p++] = 0x4C; target[p++] = 0x24; target[p++] = 0x20;
            // mov [rsp+0x28], rdx  — handler IP
            target[p++] = 0x48; target[p++] = 0x89; target[p++] = 0x54; target[p++] = 0x24; target[p++] = 0x28;
            // mov [rsp+0x30], r8   — REGDISPLAY*
            target[p++] = 0x4C; target[p++] = 0x89; target[p++] = 0x44; target[p++] = 0x24; target[p++] = 0x30;
            // mov [rsp+0x38], r9   — ExInfo*
            target[p++] = 0x4C; target[p++] = 0x89; target[p++] = 0x4C; target[p++] = 0x24; target[p++] = 0x38;

            // ── Restore parent's nonvols from REGDISPLAY (56 bytes) ────
            // R8 still has REGDISPLAY*; restore loop only clobbers RAX.
            // Each: mov rax, [r8+disp8]; mov reg, [rax].
            EmitNonvolRestore(target, ref p, regOpcode: 0x18, isExtended: false, disp: 0x18);  // pRbx → rbx
            EmitNonvolRestore(target, ref p, regOpcode: 0x28, isExtended: false, disp: 0x20);  // pRbp → rbp
            EmitNonvolRestore(target, ref p, regOpcode: 0x30, isExtended: false, disp: 0x28);  // pRsi → rsi
            EmitNonvolRestore(target, ref p, regOpcode: 0x38, isExtended: false, disp: 0x30);  // pRdi → rdi
            EmitNonvolRestore(target, ref p, regOpcode: 0x20, isExtended: true,  disp: 0x58);  // pR12 → r12
            EmitNonvolRestore(target, ref p, regOpcode: 0x28, isExtended: true,  disp: 0x60);  // pR13 → r13
            EmitNonvolRestore(target, ref p, regOpcode: 0x30, isExtended: true,  disp: 0x68);  // pR14 → r14
            EmitNonvolRestore(target, ref p, regOpcode: 0x38, isExtended: true,  disp: 0x70);  // pR15 → r15

            // ── Handler call setup + call (13 bytes) ───────────────────
            // mov rcx, [r8+0x78]  — arg1 = REGDISPLAY.SP (establisher SP)
            target[p++] = 0x49; target[p++] = 0x8B; target[p++] = 0x48; target[p++] = 0x78;
            // mov rdx, [rsp+0x20] — arg2 = exception
            target[p++] = 0x48; target[p++] = 0x8B; target[p++] = 0x54; target[p++] = 0x24; target[p++] = 0x20;
            // call qword ptr [rsp+0x28] — call handler
            target[p++] = 0xFF; target[p++] = 0x54; target[p++] = 0x24; target[p++] = 0x28;

            // ── Pop ExInfo head: s_head = exInfo->PrevExInfo (21 bytes) ─
            // mov r8, [rsp+0x38]  — reload ExInfo* (R8 was clobbered by call)
            target[p++] = 0x4C; target[p++] = 0x8B; target[p++] = 0x44; target[p++] = 0x24; target[p++] = 0x38;
            // mov r9, [r8]        — r9 = exInfo->PrevExInfo (offset 0x00)
            target[p++] = 0x4D; target[p++] = 0x8B; target[p++] = 0x08;
            // mov r10, &s_head    — placeholder (10 bytes)
            target[p++] = 0x49; target[p++] = 0xBA;
            WriteUInt64(target, ref p, (ulong)(nuint)headAddr);
            // mov [r10], r9       — *s_head = prev
            target[p++] = 0x4D; target[p++] = 0x89; target[p++] = 0x0A;

            // ── Non-local transfer (11 bytes) ──────────────────────────
            // mov r8, [rsp+0x30]  — reload REGDISPLAY*
            target[p++] = 0x4C; target[p++] = 0x8B; target[p++] = 0x44; target[p++] = 0x24; target[p++] = 0x30;
            // mov rsp, [r8+0x78]  — rsp = REGDISPLAY.SP (DESTROYS our frame!)
            target[p++] = 0x49; target[p++] = 0x8B; target[p++] = 0x60; target[p++] = 0x78;
            // jmp rax             — resume in parent method's continuation IP
            target[p++] = 0xFF; target[p++] = 0xE0;

            // Sanity check first byte (push r15) and last byte (jmp rax tail).
            if (target[0] != 0x41 || target[p - 1] != 0xE0)
                return false;

            Log.Begin(LogLevel.Info);
            Console.Write("call-catch-funclet shellcode: bytes=");
            Console.WriteUIntRaw((uint)p);
            Console.Write(" head=0x");
            Console.WriteHexRaw((ulong)(nuint)headAddr, 16);
            Log.EndLine();

            s_installed = true;
            return true;
        }

        // Emits a 7-byte sequence:
        //   mov rax, [r8+disp8]    ; 49 8B 40 disp
        //   mov reg, [rax]         ; 48/4C 8B regOpcode
        //
        // regOpcode is the ModR/M byte for `mov reg, [rax]` where:
        //   - mod=00, r/m=000 (RAX with no displacement)
        //   - reg field encodes destination
        // For low GPRs (rbx/rbp/rsi/rdi): regOpcode = 0x18/0x28/0x30/0x38, REX = 0x48
        // For r12-r15: regOpcode = 0x20/0x28/0x30/0x38, REX = 0x4C
        private static void EmitNonvolRestore(byte* dst, ref int p,
            byte regOpcode, bool isExtended, byte disp)
        {
            // mov rax, [r8+disp8]: 49 8B 40 <disp>
            dst[p++] = 0x49;
            dst[p++] = 0x8B;
            dst[p++] = 0x40;
            dst[p++] = disp;

            // mov reg, [rax]: REX 8B regOpcode
            dst[p++] = isExtended ? (byte)0x4C : (byte)0x48;
            dst[p++] = 0x8B;
            dst[p++] = regOpcode;
        }

        private static void WriteUInt64(byte* dst, ref int p, ulong val)
        {
            dst[p++] = (byte)(val);
            dst[p++] = (byte)(val >> 8);
            dst[p++] = (byte)(val >> 16);
            dst[p++] = (byte)(val >> 24);
            dst[p++] = (byte)(val >> 32);
            dst[p++] = (byte)(val >> 40);
            dst[p++] = (byte)(val >> 48);
            dst[p++] = (byte)(val >> 56);
        }
    }
}
