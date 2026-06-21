// step 117 M5.1 — compile-time codegen migration of the SehDispatch
// EmitCapture / EmitRestore shellcode emitters via BootAsm.Generator.
// At OS-build time the walker drives Iced.Intel.Assembler against the
// statement bodies below, captures bytes, and the generator emits two
// static ReadOnlySpan<byte> templates (138 + 137 bytes) in .rdata. At
// runtime EnsureShellcode does a single Span.CopyTo per emitter — no
// Iced, no allocator. The runtime Iced path from step 116
// (SehDispatch.Iced.cs) is now superseded; left as dead code until the
// step 118 cleanup pass.
//
// Compare-gate at runtime keeps byte-identity check against the legacy
// hand-rolled emitters (still in SehDispatch.cs) until the M5.1
// acceptance gate clears on multiple boots / both hypervisors.

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.PAL.SharpOSHost
{
    internal static unsafe partial class SehDispatch
    {
        // Capture-context shellcode (138 bytes). Same instruction stream
        // that runtime EmitCaptureIced produced in step 116 — byte-for-byte
        // identical with the legacy EmitCapture hand-rolled stream.
        // Win64 entry: rcx = Context*.
        [CompileTimeAsm]
        internal static partial int EmitCaptureCompileTime(byte* dst);

        [CompileTimeAsmBody(nameof(EmitCaptureCompileTime))]
        private static void EmitCaptureCompileTime_Body(Iced.Intel.Assembler a)
        {
            // ContextFlags = 0x100003 = AMD64 | CONTROL | INTEGER.
            a.mov(__dword_ptr[rcx + 0x30], 0x100003);

            // GP regs into ctx (rsp omitted — recomputed below).
            a.mov(__qword_ptr[rcx + 0x78], rax);
            a.mov(__qword_ptr[rcx + 0x80], rcx);
            a.mov(__qword_ptr[rcx + 0x88], rdx);
            a.mov(__qword_ptr[rcx + 0x90], rbx);
            a.mov(__qword_ptr[rcx + 0xA0], rbp);
            a.mov(__qword_ptr[rcx + 0xA8], rsi);
            a.mov(__qword_ptr[rcx + 0xB0], rdi);
            a.mov(__qword_ptr[rcx + 0xB8], r8);
            a.mov(__qword_ptr[rcx + 0xC0], r9);
            a.mov(__qword_ptr[rcx + 0xC8], r10);
            a.mov(__qword_ptr[rcx + 0xD0], r11);
            a.mov(__qword_ptr[rcx + 0xD8], r12);
            a.mov(__qword_ptr[rcx + 0xE0], r13);
            a.mov(__qword_ptr[rcx + 0xE8], r14);
            a.mov(__qword_ptr[rcx + 0xF0], r15);

            // Rip = saved return address.
            a.mov(rax, __qword_ptr[rsp]);
            a.mov(__qword_ptr[rcx + 0xF8], rax);
            // Rsp = caller's Rsp (this frame's Rsp + 8, past the return slot).
            a.lea(rax, __qword_ptr[rsp + 8]);
            a.mov(__qword_ptr[rcx + 0x98], rax);

            // EFlags through pushfq trick.
            a.pushfq();
            a.pop(rax);
            a.mov(__dword_ptr[rcx + 0x44], eax);

            a.ret();
        }

        // Restore-context shellcode (137 bytes). Same as runtime
        // EmitRestoreIced. Win64 entry: rcx = Context*.
        [CompileTimeAsm]
        internal static partial int EmitRestoreCompileTime(byte* dst);

        [CompileTimeAsmBody(nameof(EmitRestoreCompileTime))]
        private static void EmitRestoreCompileTime_Body(Iced.Intel.Assembler a)
        {
            a.mov(rax, __qword_ptr[rcx + 0xF8]);
            a.mov(rdx, __qword_ptr[rcx + 0x98]);
            // [rdx-8] = Rip — disp8 with sign-extend.
            a.mov(__qword_ptr[rdx - 8], rax);

            // EFlags via push/popfq.
            a.mov(eax, __dword_ptr[rcx + 0x44]);
            a.push(rax);
            a.popfq();

            // Restore GP regs (NOT rcx — still our ctx ptr; NOT rsp/rip).
            a.mov(rbx, __qword_ptr[rcx + 0x90]);
            a.mov(rbp, __qword_ptr[rcx + 0xA0]);
            a.mov(rsi, __qword_ptr[rcx + 0xA8]);
            a.mov(rdi, __qword_ptr[rcx + 0xB0]);
            a.mov(r8,  __qword_ptr[rcx + 0xB8]);
            a.mov(r9,  __qword_ptr[rcx + 0xC0]);
            a.mov(r10, __qword_ptr[rcx + 0xC8]);
            a.mov(r11, __qword_ptr[rcx + 0xD0]);
            a.mov(r12, __qword_ptr[rcx + 0xD8]);
            a.mov(r13, __qword_ptr[rcx + 0xE0]);
            a.mov(r14, __qword_ptr[rcx + 0xE8]);
            a.mov(r15, __qword_ptr[rcx + 0xF0]);
            a.mov(rdx, __qword_ptr[rcx + 0x88]);
            a.mov(rax, __qword_ptr[rcx + 0x78]);

            // Switch RSP last (so the new stack now holds pre-placed Rip).
            a.mov(rsp, __qword_ptr[rcx + 0x98]);
            // sub rsp, 8 — back to the slot holding Rip.
            a.sub(rsp, 8);
            // Finally restore Rcx.
            a.mov(rcx, __qword_ptr[rcx + 0x80]);
            // ret — pops [rsp] (= pre-placed target Rip) into Rip.
            a.ret();
        }
    }
}
