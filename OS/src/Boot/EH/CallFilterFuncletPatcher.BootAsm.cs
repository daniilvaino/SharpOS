// step 118 Wave 2 — compile-time codegen migration of
// CallFilterFuncletPatcher. 0 holes — 3 args via Win64 ABI:
//   rcx = exception, rdx = filter IP, r8 = REGDISPLAY*.
// Returns RAX = bool result (filter match decision).
//
// Differences vs catch/finally:
//   - No write-back of nonvols (filters are predicates, no side effects).
//   - No ExInfo head pop (filter just decides match).
//   - Normal return; RAX preserved through epilogue.

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Boot.EH
{
    internal static unsafe partial class CallFilterFuncletPatcher
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst);

        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a)
        {
            // Prologue: spill 8 nonvols
            a.push(r15); a.push(r14); a.push(r13); a.push(r12);
            a.push(rbx); a.push(rsi); a.push(rdi); a.push(rbp);
            a.sub(rsp, 0x28);

            // Save filter IP
            a.mov(__qword_ptr[rsp + 0x20], rdx);

            // Restore parent's nonvols from REGDISPLAY (r8 has rd*)
            a.mov(rax, __qword_ptr[r8 + 0x18]); a.mov(rbx, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x20]); a.mov(rbp, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x28]); a.mov(rsi, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x30]); a.mov(rdi, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x58]); a.mov(r12, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x60]); a.mov(r13, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x68]); a.mov(r14, __qword_ptr[rax]);
            a.mov(rax, __qword_ptr[r8 + 0x70]); a.mov(r15, __qword_ptr[rax]);

            // Filter ABI: rdx = ex, rcx = establisher SP
            a.mov(rdx, rcx);                       // rdx = exception
            a.mov(rcx, __qword_ptr[r8 + 0x78]);    // rcx = REGDISPLAY.SP

            // Call filter
            a.call(__qword_ptr[rsp + 0x20]);

            // Epilogue (RAX = filter result, preserve through pops)
            a.add(rsp, 0x28);
            a.pop(rbp); a.pop(rdi); a.pop(rsi); a.pop(rbx);
            a.pop(r12); a.pop(r13); a.pop(r14); a.pop(r15);
            a.ret();
        }
    }
}
