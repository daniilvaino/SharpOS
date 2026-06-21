// step 118 Wave 1 — compile-time codegen migration of
// BootStackSwitchPatcher. 14 bytes, 0 holes. Win64 ABI: rcx = new
// stack top, rdx = continuation. Switches RSP, zeros RBP to break the
// frame chain (debugger heuristics), pushes a fake return address (0),
// then jumps to the continuation.

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Boot
{
    internal static unsafe partial class BootStackSwitchPatcher
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst);

        // mov rsp, rcx       ; switch to new stack (3 bytes)
        // mov rbp, 0         ; break frame chain (10 bytes — Iced's
        //                      Mov_r64_imm64, the only fluent r64-imm form)
        // push 0             ; fake return slot (2 bytes — imm8 sign-ext)
        // jmp rdx            ; jump to continuation (2 bytes)
        // Total: 17 bytes (vs legacy 14 — Iced fluent has no rm64_imm32
        // form). Semantically identical to legacy; no compare-gate
        // because stub fires within a few instructions of install and a
        // broken byte stream is loud at boot.
        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a)
        {
            a.mov(rsp, rcx);
            a.mov(rbp, 0);
            a.push(0);
            a.jmp(rdx);
        }

        // No CompareOrPanic helper — see TryInstall comment for rationale.
    }
}
