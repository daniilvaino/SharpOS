// step 117 — compile-time codegen migration #1 (BootAsm.Generator).
//
// The [CompileTimeAsm] declaration `Emit(byte* dst)` and its paired
// [CompileTimeAsmBody] are isolated in this partial file so the
// `using static Iced.Intel.AssemblerRegisters` import (which dumps
// `rax`/`rcx`/…/`__qword_ptr`/… into the namespace) doesn't shadow
// anything in GcStackSpill's existing hot path.
//
// At OS-build time BootAsm.Generator finds `Emit_Body`, walks its
// statements, dispatches each `a.method(args)` reflectively against an
// in-generator Iced.Intel.Assembler, captures bytes via a CodeWriter,
// emits a `static ReadOnlySpan<byte> Emit_Template => new byte[] {…}`
// + a `partial int Emit(byte* dst)` impl that CopyTo's + returns length.
// Result: 35-byte RVA blob in .rdata, zero runtime Iced, zero allocator.
//
// CompareOrPanic at runtime byte-compares compile-time Emit() output
// against legacy EmitLegacy() until M4 acceptance.

using BootAsm;
using OS.Hal;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Memory
{
    internal static unsafe partial class GcStackSpill
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst);

        // 35 bytes. Same instruction sequence as the hand-rolled
        // WriteShellcode (now EmitLegacy in the sibling file). Walker
        // resolves `rbp/rbx/.../r15/rsp/rcx` via reflection on
        // Iced.Intel.AssemblerRegisters; `0x28` parsed as int literal;
        // overload `sub(AssemblerRegister64, int)` etc picked by
        // parameter-count + type compatibility.
        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a)
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

        // Same shape as the runtime Iced compare helpers from step 116.
        // Length and per-byte mismatches both panic — a wrong spill stub
        // would corrupt register state during conservative GC marking.
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
