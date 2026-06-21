// step 118 Wave 1 — compile-time codegen migration of PortIoPatcher.
// Two shellcodes (Inb / Outb) wrap MSVC-style port I/O at the managed
// method-body level: PortIoStub.Inb/Outb have Panic.Fail placeholders
// that the patcher overwrites at boot with these byte streams. Same
// pattern as ChkstkPatcher (Wave 1 #1) but two stubs and real Iced
// register operands (dx, cx, al, dl, eax).
//
// `@in`/`@out` in the body — Iced has `@`-prefixed method names because
// `in`/`out` are C# reserved keywords. Walker strips the leading `@`
// before reflection lookup (so the method `in` is found on Assembler).

using BootAsm;
using OS.Hal;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Hal
{
    internal static unsafe partial class PortIoPatcher
    {
        [CompileTimeAsm]
        public static partial int EmitInb(byte* dst);

        // byte Inb(ushort port) — Win64 ABI: port in CX, result in AL.
        //   mov dx, cx        ; load port number into DX (16-bit)
        //   in  al, dx        ; read byte from port DX into AL
        //   movzx eax, al     ; zero-extend for clean Win64 return in EAX/RAX
        //   ret
        [CompileTimeAsmBody(nameof(EmitInb))]
        private static void EmitInb_Body(Iced.Intel.Assembler a)
        {
            a.mov(dx, cx);
            a.@in(al, dx);
            a.movzx(eax, al);
            a.ret();
        }

        [CompileTimeAsm]
        public static partial int EmitOutb(byte* dst);

        // void Outb(ushort port, byte value) — Win64 ABI: port CX, value DL.
        //   mov al, dl        ; rescue value (DL gets clobbered next)
        //   mov dx, cx        ; load port number into DX
        //   out dx, al        ; write AL to port DX
        //   ret
        [CompileTimeAsmBody(nameof(EmitOutb))]
        private static void EmitOutb_Body(Iced.Intel.Assembler a)
        {
            a.mov(al, dl);
            a.mov(dx, cx);
            a.@out(dx, al);
            a.ret();
        }

        // Same shape as M4/M5.1 compare helpers.
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
