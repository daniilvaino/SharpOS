// step 118 Wave 1 — compile-time codegen migration of
// CaptureContextPatcher. Snapshots GPRs + IP + RSP into the
// PAL_LIMITED_CONTEXT pointed to by RCX, then returns. 53 bytes, 0 holes
// (all params ABI-driven through RCX).
//
// Iced body uses `__qword_ptr[rcx]` (no `+ 0`) for the first slot to
// match legacy's no-disp encoding `48 89 01` (3 bytes). The walker
// resolves bare `[rcx]` as base-only memory operand via the factory
// indexer.

using BootAsm;
using OS.Hal;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Boot.EH
{
    internal static unsafe partial class CaptureContextPatcher
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst);

        // mov rax, [rsp]               ; return address
        // mov [rcx], rax               ; ctx->IP
        // lea rax, [rsp+8]             ; caller's RSP
        // mov [rcx+0x08], rax          ; ctx->Rsp
        // mov [rcx+0x10], rbp / 0x18, rdi / 0x20, rsi
        // mov [rcx+0x28], rax          ; Rax slot (placeholder)
        // mov [rcx+0x30], rbx
        // mov [rcx+0x38], r12 / 0x40, r13 / 0x48, r14 / 0x50, r15
        // ret
        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a)
        {
            a.mov(rax, __qword_ptr[rsp]);
            a.mov(__qword_ptr[rcx], rax);
            a.lea(rax, __qword_ptr[rsp + 8]);
            a.mov(__qword_ptr[rcx + 0x08], rax);
            a.mov(__qword_ptr[rcx + 0x10], rbp);
            a.mov(__qword_ptr[rcx + 0x18], rdi);
            a.mov(__qword_ptr[rcx + 0x20], rsi);
            a.mov(__qword_ptr[rcx + 0x28], rax);
            a.mov(__qword_ptr[rcx + 0x30], rbx);
            a.mov(__qword_ptr[rcx + 0x38], r12);
            a.mov(__qword_ptr[rcx + 0x40], r13);
            a.mov(__qword_ptr[rcx + 0x48], r14);
            a.mov(__qword_ptr[rcx + 0x50], r15);
            a.ret();
        }

        // Same shape as M4/M5.1/Wave-1 compare helpers.
        private static void CompareOrPanic(string name, byte* compile, byte* legacy, int compileLen, int legacyLen)
        {
            if (compileLen != legacyLen)
            {
                Console.Write("[bootasm] ");
                Console.Write(name);
                Console.Write(" length mismatch: compile=0x");
                Console.WriteHex((ulong)compileLen);
                Console.Write(" legacy=0x");
                Console.WriteHex((ulong)legacyLen);
                Console.WriteLine("");
                OS.Kernel.Panic.Fail("bootasm compile/legacy length mismatch");
            }
            for (int i = 0; i < compileLen; i++)
            {
                if (compile[i] != legacy[i])
                {
                    Console.Write("[bootasm] ");
                    Console.Write(name);
                    Console.Write(" byte mismatch at offset 0x");
                    Console.WriteHex((ulong)i);
                    Console.Write(": compile=0x");
                    Console.WriteHex(compile[i]);
                    Console.Write(" legacy=0x");
                    Console.WriteHex(legacy[i]);
                    Console.WriteLine("");
                    OS.Kernel.Panic.Fail("bootasm compile/legacy byte mismatch");
                }
            }
        }
    }
}
