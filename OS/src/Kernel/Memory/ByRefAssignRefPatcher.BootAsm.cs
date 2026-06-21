// step 118 Wave 1 — compile-time codegen migration of
// ByRefAssignRefPatcher. 15 bytes, 0 holes. Non-standard calling
// convention (ILC byref-assign helper): rdi = dst slot, rsi = src slot.
// Copies one qword from src to dst, then increments both pointers by 8.
// For our non-moving GC there's no write-barrier work to do — this is
// effectively a memcpy(qword) + post-increment.
//
// Compare-gate kept in TryInstall: this stub fires on every managed
// byref assignment, so a broken byte stream would corrupt the GC heap
// silently rather than panic at boot.

using BootAsm;
using OS.Hal;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Memory
{
    internal static unsafe partial class ByRefAssignRefPatcher
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst);

        // mov rcx, [rsi]    ; 48 8B 0E
        // mov [rdi], rcx    ; 48 89 0F
        // add rdi, 8        ; 48 83 C7 08
        // add rsi, 8        ; 48 83 C6 08
        // ret               ; C3
        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a)
        {
            a.mov(rcx, __qword_ptr[rsi]);
            a.mov(__qword_ptr[rdi], rcx);
            a.add(rdi, 8);
            a.add(rsi, 8);
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
