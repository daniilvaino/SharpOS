// step 118 Wave 2 — compile-time codegen migration of
// CallFinallyFuncletPatcher. 0 holes — all args (handler IP via rcx,
// REGDISPLAY* via rdx) pass through Win64 ABI registers.
//
// Differences vs CallCatchFunclet:
//   - 2 args instead of 4 (no exception, no ExInfo*).
//   - Handler returns NORMALLY — no non-local `jmp rax` transfer.
//   - Writes nonvols BACK into REGDISPLAY after handler (finally may
//     mutate locals; later catch / next finally / continuation must see
//     updated values).
//   - No ExInfo head pop — finally doesn't consume the throw.

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Boot.EH
{
    internal static unsafe partial class CallFinallyFuncletPatcher
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst);

        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a)
        {
            // Prologue: spill 8 nonvols
            a.push(r15); a.push(r14); a.push(r13); a.push(r12);
            a.push(rbx); a.push(rsi); a.push(rdi); a.push(rbp);
            a.sub(rsp, 0x38);

            // Save 2 args
            a.mov(__qword_ptr[rsp + 0x20], rcx);   // handler IP
            a.mov(__qword_ptr[rsp + 0x28], rdx);   // REGDISPLAY*

            // Restore parent's nonvols from REGDISPLAY (rdx still has rd*)
            a.mov(rax, __qword_ptr[rdx + 0x18]); a.mov(rbx, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[rdx + 0x20]); a.mov(rbp, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[rdx + 0x28]); a.mov(rsi, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[rdx + 0x30]); a.mov(rdi, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[rdx + 0x58]); a.mov(r12, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[rdx + 0x60]); a.mov(r13, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[rdx + 0x68]); a.mov(r14, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[rdx + 0x70]); a.mov(r15, __qword_ptr[rax]);

            // Funclet ABI: RCX = REGDISPLAY.SP
            a.mov(rcx, __qword_ptr[rdx + 0x78]);

            // Call handler
            a.call(__qword_ptr[rsp + 0x20]);

            // Reload REGDISPLAY* (clobbered by call)
            a.mov(rdx, __qword_ptr[rsp + 0x28]);

            // Write back nonvols TO REGDISPLAY
            a.mov(rax, __qword_ptr[rdx + 0x18]); a.mov(__qword_ptr[rax], rbx);
            a.mov(rax, __qword_ptr[rdx + 0x20]); a.mov(__qword_ptr[rax], rbp);
            a.mov(rax, __qword_ptr[rdx + 0x28]); a.mov(__qword_ptr[rax], rsi);
            a.mov(rax, __qword_ptr[rdx + 0x30]); a.mov(__qword_ptr[rax], rdi);
            a.mov(rax, __qword_ptr[rdx + 0x58]); a.mov(__qword_ptr[rax], r12);
            a.mov(rax, __qword_ptr[rdx + 0x60]); a.mov(__qword_ptr[rax], r13);
            a.mov(rax, __qword_ptr[rdx + 0x68]); a.mov(__qword_ptr[rax], r14);
            a.mov(rax, __qword_ptr[rdx + 0x70]); a.mov(__qword_ptr[rax], r15);

            // Epilogue: restore own nonvols + ret
            a.add(rsp, 0x38);
            a.pop(rbp); a.pop(rdi); a.pop(rsi); a.pop(rbx);
            a.pop(r12); a.pop(r13); a.pop(r14); a.pop(r15);
            a.ret();
        }
    }
}
