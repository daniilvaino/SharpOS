// step 115 follow-up #6: Iced-driven CR3 read/write stubs for
// Cr3Accessor. EARLIEST emitter in boot order — Phase3 Pager.Init runs
// before Phase4 Probe_IcedEncode, so the first `new Iced.Intel.Assembler()`
// here also triggers the first AssemblerRegisters cctor materialisation.
// If Phase2 cctor materialization missed those (Iced statics are lazy
// rather than preinit-tabled), the HW fault is loud and obvious in the
// Phase3 boot log; fallback would be an eager Iced touch at end of Phase2
// before Pager.Init.

using OS.Hal;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Paging
{
    internal static unsafe partial class Cr3Accessor
    {
        private sealed class Cr3StubBufWriter : Iced.Intel.CodeWriter
        {
            private readonly byte* _p;
            private readonly int _cap;
            private int _i;
            public Cr3StubBufWriter(byte* p, int capacity) { _p = p; _cap = capacity; _i = 0; }
            public int Count => _i;
            public override void WriteByte(byte value)
            {
                if (_i < _cap) _p[_i++] = value;
            }
        }

        //   mov rax, cr3      ; 0F 20 D8
        //   ret               ; C3
        // 4 bytes.
        private static int EmitReadStubIced(byte* p, int cap)
        {
            var a = new Iced.Intel.Assembler(64);
            a.mov(rax, cr3);
            a.ret();

            var w = new Cr3StubBufWriter(p, cap);
            a.Assemble(w, 0);
            return w.Count;
        }

        // Windows x64 ABI: rcx in = new CR3 value.
        //   mov rax, rcx      ; 48 89 C8
        //   mov cr3, rax      ; 0F 22 D8
        //   ret               ; C3
        // 7 bytes.
        private static int EmitWriteStubIced(byte* p, int cap)
        {
            var a = new Iced.Intel.Assembler(64);
            a.mov(rax, rcx);
            a.mov(cr3, rax);
            a.ret();

            var w = new Cr3StubBufWriter(p, cap);
            a.Assemble(w, 0);
            return w.Count;
        }

        // Same shape as the other Iced compare helpers. Length and
        // per-byte mismatches both panic — a wrong CR3 stub corrupts the
        // entire kernel memory map, so loud fail at Phase3 is the right
        // tradeoff vs a silent invalid TLB flush.
        private static void CompareOrPanic(string name, byte* iced, byte* legacy, int icedLen, int legacyLen)
        {
            if (icedLen != legacyLen)
            {
                Console.Write("[cr3] ");
                Console.Write(name);
                Console.Write(" length mismatch: iced=0x");
                Console.WriteHex((ulong)icedLen);
                Console.Write(" legacy=0x");
                Console.WriteHex((ulong)legacyLen);
                Console.WriteLine("");
                OS.Kernel.Panic.Fail("cr3 stub iced/legacy length mismatch");
            }
            for (int i = 0; i < icedLen; i++)
            {
                if (iced[i] != legacy[i])
                {
                    Console.Write("[cr3] ");
                    Console.Write(name);
                    Console.Write(" byte mismatch at offset 0x");
                    Console.WriteHex((ulong)i);
                    Console.Write(": iced=0x");
                    Console.WriteHex(iced[i]);
                    Console.Write(" legacy=0x");
                    Console.WriteHex(legacy[i]);
                    Console.WriteLine("");
                    OS.Kernel.Panic.Fail("cr3 stub iced/legacy byte mismatch");
                }
            }
        }
    }
}
