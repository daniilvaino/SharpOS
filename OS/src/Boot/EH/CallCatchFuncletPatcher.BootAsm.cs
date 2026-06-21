// step 118 Wave 2 — compile-time codegen migration of
// CallCatchFuncletPatcher. ~137 bytes (Iced picks `sub rsp, 0x48` in 4B
// imm8 form; legacy used 7B imm32 form — 3 bytes shorter, semantically
// identical). One MovHole for `head` (= ExInfoHead.GetHeadAddress()).
//
// Stub semantics — see CallCatchFuncletPatcher.cs for the full stack
// layout / handler ABI commentary. This body mirrors the original byte
// stream instruction-for-instruction; the only divergence is the
// `sub rsp, 0x48` encoding form.

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Boot.EH
{
    internal static unsafe partial class CallCatchFuncletPatcher
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst, void** head);

        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            // Prologue: spill 8 nonvols.
            a.push(r15); a.push(r14); a.push(r13); a.push(r12);
            a.push(rbx); a.push(rsi); a.push(rdi); a.push(rbp);
            a.sub(rsp, 0x48);

            // Save 4 args at [rsp+0x20 .. 0x38].
            a.mov(__qword_ptr[rsp + 0x20], rcx);   // exception
            a.mov(__qword_ptr[rsp + 0x28], rdx);   // handler IP
            a.mov(__qword_ptr[rsp + 0x30], r8);    // REGDISPLAY*
            a.mov(__qword_ptr[rsp + 0x38], r9);    // ExInfo*

            // Restore parent's nonvols from REGDISPLAY (R8 still has rd*).
            // Pattern: mov rax, [r8+pXxx]; mov xxx, [rax].
            a.mov(rax, __qword_ptr[r8 + 0x18]); a.mov(rbx, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x20]); a.mov(rbp, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x28]); a.mov(rsi, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x30]); a.mov(rdi, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x58]); a.mov(r12, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x60]); a.mov(r13, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x68]); a.mov(r14, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x70]); a.mov(r15, __qword_ptr[rax]);

            // Handler call (funclet ABI: rcx = establisher SP, rdx = exception).
            a.mov(rcx, __qword_ptr[r8 + 0x78]);
            a.mov(rdx, __qword_ptr[rsp + 0x20]);
            a.call(__qword_ptr[rsp + 0x28]);

            // Pop ExInfo head: s_head = exInfo->PrevExInfo.
            a.mov(r8, __qword_ptr[rsp + 0x38]);
            a.mov(r9, __qword_ptr[r8]);
            h.MovHole(a, r10, "head");           // mov r10, &s_head
            a.mov(__qword_ptr[r10], r9);

            // Non-local transfer: mov rsp, REGDISPLAY.SP; jmp rax.
            a.mov(r8, __qword_ptr[rsp + 0x30]);
            a.mov(rsp, __qword_ptr[r8 + 0x78]);
            a.jmp(rax);
        }
    }
}
