// BootAsm.Generator pipeline canary. Each stub here exercises a
// specific walker capability so generator-side regressions are caught at
// OS-build time. Not a runtime probe — the canary is "OS compiles".
//
//   Ret           — M2 minimum: zero-arg method call.
//   GcStackSpill  — M3: register identifiers + integer literals +
//                   Assembler overload resolution. Pattern mirrors
//                   OS/src/Kernel/Memory/GcStackSpill.cs exactly so
//                   later compare-gate against legacy is byte-identity.

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Diagnostics
{
    internal static unsafe partial class BootAsmProbe
    {
        [CompileTimeAsm]
        public static partial int Ret(byte* dst);

        [CompileTimeAsmBody(nameof(Ret))]
        private static void Ret_Body(Iced.Intel.Assembler a) => a.ret();

        // GcStackSpill register-spill trampoline. Same bytes as the
        // hand-rolled WriteShellcode in Kernel/Memory/GcStackSpill.cs
        // (35 bytes). Win64 ABI: rcx = managed callback.
        [CompileTimeAsm]
        public static partial int GcStackSpill(byte* dst);

        [CompileTimeAsmBody(nameof(GcStackSpill))]
        private static void GcStackSpill_Body(Iced.Intel.Assembler a)
        {
            a.push(rbp); a.push(rbx); a.push(rdi); a.push(rsi);
            a.push(r12); a.push(r13); a.push(r14); a.push(r15);
            a.sub(rsp, 0x28);
            a.call(rcx);
            a.add(rsp, 0x28);
            a.pop(r15); a.pop(r14); a.pop(r13); a.pop(r12);
            a.pop(rsi); a.pop(rdi); a.pop(rbx); a.pop(rbp);
            a.ret();
        }

        // M5 canary: memory operands. Mirror the exact `mov [rcx+disp], rXX`
        // / `mov rXX, [rcx+disp]` / `lea rax, [rsp+8]` / `mov [rdx-8], rax`
        // patterns from SehDispatch.EmitCapture/Restore — any walker
        // regression on memory-operand arithmetic fails the OS build.
        [CompileTimeAsm]
        public static partial int MemOps(byte* dst);

        [CompileTimeAsmBody(nameof(MemOps))]
        private static void MemOps_Body(Iced.Intel.Assembler a)
        {
            a.mov(__qword_ptr[rcx + 0x88], rdx);   // base + positive disp, write
            a.mov(rax, __qword_ptr[rsp]);          // base only, read
            a.lea(rax, __qword_ptr[rsp + 8]);      // lea with small disp
            a.mov(__qword_ptr[rdx - 8], rax);      // negative disp (sign-extended disp8)
            a.mov(__dword_ptr[rcx + 0x44], eax);   // dword variant
            a.ret();
        }

        // M6 canary: holes. Body uses HoleCollector to emit forced-imm64
        // movs with sentinels; generator finds them post-Assemble, computes
        // byte offsets, zero-outs the sentinels in the template, and emits
        // patch lines `*(ulong*)(dst + off) = (ulong)(nuint)<paramName>;`
        // for each hole. The partial declaration parameter names MUST match
        // the string literals passed to MovHole/JmpHole.
        [CompileTimeAsm]
        public static partial int WriteHandler(byte* dst, void** handlerCalled,
                                                void** observedRcx,
                                                void** observedRdx,
                                                void** continuation);

        [CompileTimeAsmBody(nameof(WriteHandler))]
        private static void WriteHandler_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            h.MovHole(a, r10, "handlerCalled");  a.mov(__qword_ptr[r10], rcx);
            h.MovHole(a, r10, "observedRcx");    a.mov(__qword_ptr[r10], rcx);
            h.MovHole(a, r10, "observedRdx");    a.mov(__qword_ptr[r10], rdx);
            h.JmpHole(a, r11, "continuation");
        }
    }
}
