// step 118 Wave 1 — compile-time codegen migration of ChkstkPatcher
// via BootAsm.Generator. Body method below executes at OS-build time
// against Iced.Intel.Assembler; the generator emits a 1-byte template
// (`0xC3` = ret) + a partial impl that CopyTo's the template into dst.
// Patcher uses this at boot to overwrite the first byte of the managed
// __chkstk body, turning the long `__chkstk` walk into a net-zero ret.

using BootAsm;
using OS.Hal;

namespace OS.PAL.SharpOSHost
{
    internal static unsafe partial class ChkstkPatcher
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst);

        // Simplest possible body — single `ret`. Walker dispatches the
        // zero-arg method call reflectively, Iced emits `0xC3`.
        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a) => a.ret();

        // Same shape as the M4/M5.1 compare helpers.
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
