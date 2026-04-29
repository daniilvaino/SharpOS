using OS.Hal;

namespace OS.Boot.EH
{
    // Phase 1 step 7 — patches CallFinallyFuncletStub.RhpCallFinallyFunclet
    // body with byte-array shellcode (~174 bytes). Same patcher pattern as
    // CallCatchFuncletPatcher.
    //
    // Differences vs catch funclet:
    //   - 2 args (RCX=handler IP, RDX=REGDISPLAY*) vs 4 (catch has exception + ExInfo*).
    //   - Handler called с RCX=establisher SP only (no exception arg).
    //   - Handler returns NORMALLY; no `mov rsp; jmp rax` non-local transfer.
    //   - After handler returns, нонволы могут быть changed by finally — write
    //     them BACK в REGDISPLAY pointers так что catch / next finally /
    //     continuation видит updated values.
    //   - No ExInfo head pop — throw not consumed by finally.
    //
    // STACK LAYOUT after prologue (8 push'ей + sub rsp, 0x38):
    //
    //   Entry:                  RSP % 16 == 8.
    //   After 8 pushes (0x40):  RSP % 16 still == 8.
    //   After sub rsp, 0x38:    RSP % 16 == 0. ✓ aligned for next CALL.
    //
    //   [rsp + 0x00 .. 0x1F]    shadow space for handler call (0x20)
    //   [rsp + 0x20 .. 0x27]    saved handler IP    (RCX entry value)
    //   [rsp + 0x28 .. 0x2F]    saved REGDISPLAY*   (RDX entry value)
    //   [rsp + 0x30 .. 0x37]    alignment pad
    //
    // Total bytes: 12 (push) + 7 (sub) + 10 (save args) + 56 (restore nonvols)
    //              + 4 (rcx setup) + 4 (call) + 5 (reload rdx) + 56 (writeback)
    //              + 7 (add rsp) + 12 (pops) + 1 (ret) = 174 bytes.
    internal static unsafe class CallFinallyFuncletPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)CallFinallyFuncletStub.GetMethodAddress();
            if (target == null) return false;

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

            // sub rsp, 0x38 (7 bytes — using imm32 form for consistency)
            target[p++] = 0x48; target[p++] = 0x81; target[p++] = 0xEC;
            target[p++] = 0x38; target[p++] = 0x00; target[p++] = 0x00; target[p++] = 0x00;

            // ── Save 2 args (10 bytes) ─────────────────────────────────
            // mov [rsp+0x20], rcx  — handler IP
            target[p++] = 0x48; target[p++] = 0x89; target[p++] = 0x4C; target[p++] = 0x24; target[p++] = 0x20;
            // mov [rsp+0x28], rdx  — REGDISPLAY*
            target[p++] = 0x48; target[p++] = 0x89; target[p++] = 0x54; target[p++] = 0x24; target[p++] = 0x28;

            // ── Restore parent's nonvols from REGDISPLAY (56 bytes) ────
            // RDX still has REGDISPLAY*; restore via mov rax, [rdx+disp]; mov reg, [rax].
            EmitNonvolRestore(target, ref p, regOpcode: 0x18, isExtended: false, disp: 0x18);  // pRbx → rbx
            EmitNonvolRestore(target, ref p, regOpcode: 0x28, isExtended: false, disp: 0x20);  // pRbp → rbp
            EmitNonvolRestore(target, ref p, regOpcode: 0x30, isExtended: false, disp: 0x28);  // pRsi → rsi
            EmitNonvolRestore(target, ref p, regOpcode: 0x38, isExtended: false, disp: 0x30);  // pRdi → rdi
            EmitNonvolRestore(target, ref p, regOpcode: 0x20, isExtended: true,  disp: 0x58);  // pR12 → r12
            EmitNonvolRestore(target, ref p, regOpcode: 0x28, isExtended: true,  disp: 0x60);  // pR13 → r13
            EmitNonvolRestore(target, ref p, regOpcode: 0x30, isExtended: true,  disp: 0x68);  // pR14 → r14
            EmitNonvolRestore(target, ref p, regOpcode: 0x38, isExtended: true,  disp: 0x70);  // pR15 → r15

            // ── Funclet ABI: RCX = REGDISPLAY.SP (4 bytes) ─────────────
            // mov rcx, [rdx+0x78]
            target[p++] = 0x48; target[p++] = 0x8B; target[p++] = 0x4A; target[p++] = 0x78;

            // ── Call handler (4 bytes) ────────────────────────────────
            // call qword ptr [rsp+0x20]
            target[p++] = 0xFF; target[p++] = 0x54; target[p++] = 0x24; target[p++] = 0x20;

            // ── Reload REGDISPLAY* (clobbered by call) (5 bytes) ──────
            // mov rdx, [rsp+0x28]
            target[p++] = 0x48; target[p++] = 0x8B; target[p++] = 0x54; target[p++] = 0x24; target[p++] = 0x28;

            // ── Write back nonvols TO REGDISPLAY (56 bytes) ───────────
            // Each: mov rax, [rdx+disp]; mov [rax], reg.
            EmitNonvolWriteback(target, ref p, regOpcode: 0x18, isExtended: false, disp: 0x18);  // rbx → *pRbx
            EmitNonvolWriteback(target, ref p, regOpcode: 0x28, isExtended: false, disp: 0x20);  // rbp → *pRbp
            EmitNonvolWriteback(target, ref p, regOpcode: 0x30, isExtended: false, disp: 0x28);  // rsi → *pRsi
            EmitNonvolWriteback(target, ref p, regOpcode: 0x38, isExtended: false, disp: 0x30);  // rdi → *pRdi
            EmitNonvolWriteback(target, ref p, regOpcode: 0x20, isExtended: true,  disp: 0x58);  // r12 → *pR12
            EmitNonvolWriteback(target, ref p, regOpcode: 0x28, isExtended: true,  disp: 0x60);  // r13 → *pR13
            EmitNonvolWriteback(target, ref p, regOpcode: 0x30, isExtended: true,  disp: 0x68);  // r14 → *pR14
            EmitNonvolWriteback(target, ref p, regOpcode: 0x38, isExtended: true,  disp: 0x70);  // r15 → *pR15

            // ── Epilogue: restore own nonvols + ret (20 bytes) ────────
            // add rsp, 0x38 (7 bytes — imm32 form for consistency)
            target[p++] = 0x48; target[p++] = 0x81; target[p++] = 0xC4;
            target[p++] = 0x38; target[p++] = 0x00; target[p++] = 0x00; target[p++] = 0x00;

            // pop rbp (1 byte)
            target[p++] = 0x5D;
            // pop rdi (1 byte)
            target[p++] = 0x5F;
            // pop rsi (1 byte)
            target[p++] = 0x5E;
            // pop rbx (1 byte)
            target[p++] = 0x5B;
            // pop r12 (2 bytes)
            target[p++] = 0x41; target[p++] = 0x5C;
            // pop r13 (2 bytes)
            target[p++] = 0x41; target[p++] = 0x5D;
            // pop r14 (2 bytes)
            target[p++] = 0x41; target[p++] = 0x5E;
            // pop r15 (2 bytes)
            target[p++] = 0x41; target[p++] = 0x5F;
            // ret (1 byte)
            target[p++] = 0xC3;

            // Sanity check first byte (push r15) and last byte (ret).
            if (target[0] != 0x41 || target[p - 1] != 0xC3)
                return false;

            Log.Begin(LogLevel.Info);
            Console.Write("call-finally-funclet shellcode: bytes=");
            Console.WriteUIntRaw((uint)p);
            Log.EndLine();

            s_installed = true;
            return true;
        }

        // mov rax, [rdx+disp8]; mov reg, [rax]  (7 bytes)
        // For low GPRs (rbx/rbp/rsi/rdi): regOpcode = 0x18/0x28/0x30/0x38, REX = 0x48
        // For r12-r15: regOpcode = 0x20/0x28/0x30/0x38, REX = 0x4C
        private static void EmitNonvolRestore(byte* dst, ref int p,
            byte regOpcode, bool isExtended, byte disp)
        {
            // mov rax, [rdx+disp8]: 48 8B 42 <disp>
            dst[p++] = 0x48;
            dst[p++] = 0x8B;
            dst[p++] = 0x42;
            dst[p++] = disp;

            // mov reg, [rax]: REX 8B regOpcode
            dst[p++] = isExtended ? (byte)0x4C : (byte)0x48;
            dst[p++] = 0x8B;
            dst[p++] = regOpcode;
        }

        // mov rax, [rdx+disp8]; mov [rax], reg  (7 bytes)
        // Mirror of restore, but stores reg into *rax.
        private static void EmitNonvolWriteback(byte* dst, ref int p,
            byte regOpcode, bool isExtended, byte disp)
        {
            // mov rax, [rdx+disp8]: 48 8B 42 <disp>
            dst[p++] = 0x48;
            dst[p++] = 0x8B;
            dst[p++] = 0x42;
            dst[p++] = disp;

            // mov [rax], reg: REX 89 regOpcode
            dst[p++] = isExtended ? (byte)0x4C : (byte)0x48;
            dst[p++] = 0x89;
            dst[p++] = regOpcode;
        }
    }
}
