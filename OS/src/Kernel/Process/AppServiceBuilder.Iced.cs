// step 115 follow-up #4: Iced-driven thunk emitters for AppServiceBuilder.
// Two thunk shapes (Win64 one-arg / SysV one-arg) emitted via Iced; the
// legacy byte-streams live next to these (see AppServiceBuilder.cs) and
// each TryWrite call parallel-emits both + byte-compares them via
// CompareOrPanic. Isolated in its own partial file so the `using static
// Iced.Intel.AssemblerRegisters` import doesn't shadow anything in
// AppServiceBuilder's hot path. After several green boots the legacy
// emitters + the compare gate get pulled out and only the Iced halves
// remain.

using OS.Hal;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Process
{
    internal static unsafe partial class AppServiceBuilder
    {
        private sealed class ThunkBufWriter : Iced.Intel.CodeWriter
        {
            private readonly byte* _p;
            private readonly int _cap;
            private int _i;
            public ThunkBufWriter(byte* p, int capacity) { _p = p; _cap = capacity; _i = 0; }
            public int Count => _i;
            public override void WriteByte(byte value)
            {
                if (_i < _cap) _p[_i++] = value;
            }
        }

        // Windows x64 ABI: arg0 already in rcx; just call the target.
        //   mov  rax, target          ; 48 B8 + imm64    (10)
        //   sub  rsp, 0x28            ; 48 83 EC 28      (4)
        //   call rax                  ; FF D0            (2)
        //   add  rsp, 0x28            ; 48 83 C4 28      (4)
        //   ret                       ; C3               (1)
        // 21 bytes. mov(rax, ulong) in Iced binds to Mov_r64_imm64
        // verbatim (no imm32 fold), so the immediate slot is byte-stable
        // regardless of target value.
        private static int EmitWin64OneArgThunkIced(byte* p, int cap, ulong target)
        {
            var a = new Iced.Intel.Assembler(64);
            a.mov(rax, target);
            a.sub(rsp, 0x28);
            a.call(rax);
            a.add(rsp, 0x28);
            a.ret();

            var w = new ThunkBufWriter(p, cap);
            a.Assemble(w, 0);
            return w.Count;
        }

        // System V AMD64 ABI: arg0 comes in rdi; translate to rcx before
        // calling the (Win64-shaped) target.
        //   mov  rcx, rdi             ; 48 89 F9         (3)
        //   mov  rax, target          ; 48 B8 + imm64    (10)
        //   sub  rsp, 0x28            ; 48 83 EC 28      (4)
        //   call rax                  ; FF D0            (2)
        //   add  rsp, 0x28            ; 48 83 C4 28      (4)
        //   ret                       ; C3               (1)
        // 24 bytes.
        private static int EmitSystemVOneArgThunkIced(byte* p, int cap, ulong target)
        {
            var a = new Iced.Intel.Assembler(64);
            a.mov(rcx, rdi);
            a.mov(rax, target);
            a.sub(rsp, 0x28);
            a.call(rax);
            a.add(rsp, 0x28);
            a.ret();

            var w = new ThunkBufWriter(p, cap);
            a.Assemble(w, 0);
            return w.Count;
        }

        // Same shape as SehDispatch / JumpStub compare helpers. Length and
        // per-byte mismatches both panic with full diagnostics on the
        // serial console — any drift in a thunk that wraps a managed fn
        // ptr would jump into garbage at the first guest call, so loud
        // failure at boot is the right tradeoff.
        private static void CompareOrPanic(string name, byte* iced, byte* legacy, int icedLen, int legacyLen)
        {
            if (icedLen != legacyLen)
            {
                Console.Write("[thunk] ");
                Console.Write(name);
                Console.Write(" length mismatch: iced=0x");
                Console.WriteHex((ulong)icedLen);
                Console.Write(" legacy=0x");
                Console.WriteHex((ulong)legacyLen);
                Console.WriteLine("");
                OS.Kernel.Panic.Fail("thunk iced/legacy length mismatch");
            }
            for (int i = 0; i < icedLen; i++)
            {
                if (iced[i] != legacy[i])
                {
                    Console.Write("[thunk] ");
                    Console.Write(name);
                    Console.Write(" byte mismatch at offset 0x");
                    Console.WriteHex((ulong)i);
                    Console.Write(": iced=0x");
                    Console.WriteHex(iced[i]);
                    Console.Write(" legacy=0x");
                    Console.WriteHex(legacy[i]);
                    Console.WriteLine("");
                    OS.Kernel.Panic.Fail("thunk iced/legacy byte mismatch");
                }
            }
        }
    }
}
