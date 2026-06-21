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
    internal static unsafe partial class CallCatchFuncletPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)CallCatchFuncletStub.GetMethodAddress();
            if (target == null) return false;

            byte** headAddr = ExInfoHead.GetHeadAddress();

            // step 118 Wave 2 — compile-time codegen (BootAsm.Generator).
            // Emit() writes the ~137-byte template with one MovHole patched
            // at install time with &s_head. No compare-gate: Iced picks
            // shorter sub-rsp form (imm8 4B vs imm32 7B) so bytes differ
            // by encoding, not semantics. Runtime EH probes / first throw
            // is the validation oracle.
            int p = Emit(target, (void**)headAddr);

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
    }
}
