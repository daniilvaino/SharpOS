using OS.Hal;

namespace OS.Boot.EH
{
    // Phase 1 step 8 — patches CallFilterFuncletStub.RhpCallFilterFunclet
    // body with byte-array shellcode (~111 bytes).
    //
    // Differences vs catch/finally funclet:
    //   - 3 args (RCX=ex, RDX=filter IP, R8=REGDISPLAY*).
    //   - Filter called с RCX=establisher SP, RDX=exception.
    //   - Returns RAX = bool result (0/1) which we PRESERVE through epilogue.
    //   - Normal return; no non-local transfer.
    //   - No write-back of nonvols (filters are predicates, no side effects).
    //   - No ExInfo head pop (no throw consumption — filter just decides match).
    //
    // STACK LAYOUT after prologue (8 push'ей + sub rsp, 0x28):
    //
    //   Entry:                  RSP % 16 == 8.
    //   After 8 pushes (0x40):  RSP % 16 still == 8.
    //   After sub rsp, 0x28:    RSP % 16 == 0. ✓ aligned for next CALL.
    //
    //   [rsp + 0x00 .. 0x1F]    shadow space for filter call (0x20)
    //   [rsp + 0x20 .. 0x27]    saved filter IP (RDX entry value)
    //
    // Total bytes: 12 (push) + 7 (sub) + 5 (save filter IP) + 56 (restore nonvols)
    //              + 3 (rdx<-rcx) + 4 (rcx<-establisher) + 4 (call)
    //              + 7 (add rsp) + 12 (pops) + 1 (ret) = 111 bytes.
    internal static unsafe class CallFilterFuncletPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)CallFilterFuncletStub.GetMethodAddress();
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

            // sub rsp, 0x28 (7 bytes — imm32 form for consistency)
            target[p++] = 0x48; target[p++] = 0x81; target[p++] = 0xEC;
            target[p++] = 0x28; target[p++] = 0x00; target[p++] = 0x00; target[p++] = 0x00;

            // ── Save filter IP (5 bytes) ───────────────────────────────
            // mov [rsp+0x20], rdx
            target[p++] = 0x48; target[p++] = 0x89; target[p++] = 0x54; target[p++] = 0x24; target[p++] = 0x20;

            // ── Restore parent's nonvols from REGDISPLAY (R8 has it) (56 bytes) ─
            EmitNonvolRestore(target, ref p, regOpcode: 0x18, isExtended: false, disp: 0x18);  // pRbx → rbx
            EmitNonvolRestore(target, ref p, regOpcode: 0x28, isExtended: false, disp: 0x20);  // pRbp → rbp
            EmitNonvolRestore(target, ref p, regOpcode: 0x30, isExtended: false, disp: 0x28);  // pRsi → rsi
            EmitNonvolRestore(target, ref p, regOpcode: 0x38, isExtended: false, disp: 0x30);  // pRdi → rdi
            EmitNonvolRestore(target, ref p, regOpcode: 0x20, isExtended: true,  disp: 0x58);  // pR12 → r12
            EmitNonvolRestore(target, ref p, regOpcode: 0x28, isExtended: true,  disp: 0x60);  // pR13 → r13
            EmitNonvolRestore(target, ref p, regOpcode: 0x30, isExtended: true,  disp: 0x68);  // pR14 → r14
            EmitNonvolRestore(target, ref p, regOpcode: 0x38, isExtended: true,  disp: 0x70);  // pR15 → r15

            // ── Filter ABI setup (7 bytes) ─────────────────────────────
            // mov rdx, rcx        — rdx = exception (2nd arg)
            target[p++] = 0x48; target[p++] = 0x89; target[p++] = 0xCA;
            // mov rcx, [r8+0x78]  — rcx = REGDISPLAY.SP (establisher, 1st arg)
            target[p++] = 0x49; target[p++] = 0x8B; target[p++] = 0x48; target[p++] = 0x78;

            // ── Call filter (4 bytes) ──────────────────────────────────
            // call qword ptr [rsp+0x20]
            target[p++] = 0xFF; target[p++] = 0x54; target[p++] = 0x24; target[p++] = 0x20;

            // ── Epilogue (RAX = filter result, preserve through pops) ──
            // add rsp, 0x28 (7 bytes — imm32 form)
            target[p++] = 0x48; target[p++] = 0x81; target[p++] = 0xC4;
            target[p++] = 0x28; target[p++] = 0x00; target[p++] = 0x00; target[p++] = 0x00;

            // pop rbp (1)
            target[p++] = 0x5D;
            // pop rdi (1)
            target[p++] = 0x5F;
            // pop rsi (1)
            target[p++] = 0x5E;
            // pop rbx (1)
            target[p++] = 0x5B;
            // pop r12 (2)
            target[p++] = 0x41; target[p++] = 0x5C;
            // pop r13 (2)
            target[p++] = 0x41; target[p++] = 0x5D;
            // pop r14 (2)
            target[p++] = 0x41; target[p++] = 0x5E;
            // pop r15 (2)
            target[p++] = 0x41; target[p++] = 0x5F;
            // ret (1)
            target[p++] = 0xC3;

            // Sanity check first byte (push r15) and last byte (ret).
            if (target[0] != 0x41 || target[p - 1] != 0xC3)
                return false;

            Log.Begin(LogLevel.Info);
            Console.Write("call-filter-funclet shellcode: bytes=");
            Console.WriteUIntRaw((uint)p);
            Log.EndLine();

            s_installed = true;
            return true;
        }

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
    }
}
