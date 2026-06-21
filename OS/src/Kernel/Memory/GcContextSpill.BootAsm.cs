// step 118 Wave 4 — compile-time codegen migration of GcContextSpill.
// 0 holes — all args via Win64 ABI: rcx = Context*, rdx = managed
// callback. Snapshot of caller's full register set into Context struct,
// then call the callback (which uses Context as starting point for
// precise GC stack walk via SehUnwind.VirtualUnwind).
//
// Context field offsets (cf. GcContextSpill.cs OffXxx constants):
//   +0x78  Rax  +0x80  Rcx  +0x88  Rdx  +0x90  Rbx
//   +0x98  Rsp  +0xA0  Rbp  +0xA8  Rsi  +0xB0  Rdi
//   +0xB8  R8   +0xC0  R9   +0xC8  R10  +0xD0  R11
//   +0xD8  R12  +0xE0  R13  +0xE8  R14  +0xF0  R15
//   +0xF8  Rip
// Caller-saved fields (Rax/Rcx/Rdx/R8..R11) stored as 0 — GcInfo at a
// call-site PC never references caller-saved regs.

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Memory
{
    internal static unsafe partial class GcContextSpill
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst);

        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a)
        {
            // xor eax, eax — common zero source for caller-saved fields.
            a.xor(eax, eax);

            // Caller-saved fields ← 0  (Rax, Rcx, Rdx, R8, R9, R10, R11)
            a.mov(__qword_ptr[rcx + 0x78], rax);  // Rax
            a.mov(__qword_ptr[rcx + 0x80], rax);  // Rcx
            a.mov(__qword_ptr[rcx + 0x88], rax);  // Rdx
            a.mov(__qword_ptr[rcx + 0xB8], rax);  // R8
            a.mov(__qword_ptr[rcx + 0xC0], rax);  // R9
            a.mov(__qword_ptr[rcx + 0xC8], rax);  // R10
            a.mov(__qword_ptr[rcx + 0xD0], rax);  // R11

            // Callee-saved fields ← caller's actual values
            a.mov(__qword_ptr[rcx + 0x90], rbx);  // Rbx
            a.mov(__qword_ptr[rcx + 0xA0], rbp);  // Rbp
            a.mov(__qword_ptr[rcx + 0xA8], rsi);  // Rsi
            a.mov(__qword_ptr[rcx + 0xB0], rdi);  // Rdi
            a.mov(__qword_ptr[rcx + 0xD8], r12);  // R12
            a.mov(__qword_ptr[rcx + 0xE0], r13);  // R13
            a.mov(__qword_ptr[rcx + 0xE8], r14);  // R14
            a.mov(__qword_ptr[rcx + 0xF0], r15);  // R15

            // ctx.Rip = [rsp] (return address)
            a.mov(rax, __qword_ptr[rsp]);
            a.mov(__qword_ptr[rcx + 0xF8], rax);

            // ctx.Rsp = rsp + 8 (caller's RSP at call site)
            a.lea(rax, __qword_ptr[rsp + 8]);
            a.mov(__qword_ptr[rcx + 0x98], rax);

            // call rdx (callback). 32 bytes shadow + 8 align = 0x28.
            a.sub(rsp, 0x28);
            a.call(rdx);
            a.add(rsp, 0x28);
            a.ret();
        }
    }
}
