// step 115 follow-up #2: Iced-driven replacement for the JumpStub byte
// emitter. The legacy hand-rolled byte stream still lives next to this
// (see JumpStub.cs::EmitStubLegacy) and TryInitialize emits both in
// parallel + byte-compares them via CompareOrPanic. Isolated in its own
// partial file so the `using static Iced.Intel.AssemblerRegisters` import
// (which dumps `rax`/`rcx`/…/`cr3`/`__qword_ptr`/… into the namespace)
// doesn't shadow anything in JumpStub's hot path. After several green
// boots the legacy emitter + the compare gate get pulled out and only
// the Iced half remains.

using OS.Hal;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Exec
{
    internal static unsafe partial class JumpStub
    {
        private sealed class StubBufWriter : Iced.Intel.CodeWriter
        {
            private readonly byte* _p;
            private readonly int _cap;
            private int _i;
            public StubBufWriter(byte* p, int capacity) { _p = p; _cap = capacity; _i = 0; }
            public int Count => _i;
            public override void WriteByte(byte value)
            {
                if (_i < _cap) _p[_i++] = value;
            }
        }

        // Windows x64 ABI entry:
        //   rcx = entry address, rdx = app stack top,
        //   r8  = startup block pointer, r9 = pager CR3
        // Switches to pager CR3, runs entry on app stack, restores
        // kernel CR3/state. Original CR3 lives on the kernel stack
        // (callee-preserved regs aren't safe — untrusted app code may
        // clobber non-volatiles).
        //
        //   mov  r11, rsp                 ; snapshot kernel rsp
        //   push r12 / push r13           ; preserve callee-saved
        //   mov  r12, r11                 ; r12 = saved kernel rsp anchor
        //   mov  r13, rdi                 ; preserve rdi
        //   pushfq; cli                   ; save RFLAGS + mask IRQs
        //   mov  rax, cr3 / push rax      ; save kernel CR3
        //   mov  cr3, r9                  ; activate pager CR3
        //   mov  rsp, rdx                 ; switch to app stack
        //   sub  rsp, 0x20                ; Win64 shadow space
        //   mov  rdi, r8                  ; rdi = startup block (Linux-ish ABI for app)
        //   call rcx                      ; run app entry
        //   mov  r10, rax                 ; stash return value
        //   mov  rax, [r12-0x20]          ; reload kernel CR3 from saved slot
        //   mov  cr3, rax
        //   lea  rsp, [r12-0x18]          ; restore kernel rsp (past pushed RFLAGS)
        //   popfq                         ; restore RFLAGS (re-enables IRQs)
        //   mov  rdi, r13                 ; restore rdi
        //   pop  r13 / pop r12
        //   mov  rax, r10                 ; return value back to rax
        //   ret
        private static int EmitStubIced(byte* p, int cap)
        {
            var a = new Iced.Intel.Assembler(64);

            a.mov(r11, rsp);
            a.push(r12);
            a.push(r13);
            a.mov(r12, r11);
            a.mov(r13, rdi);
            a.pushfq();
            a.cli();
            a.mov(rax, cr3);
            a.push(rax);
            a.mov(cr3, r9);
            a.mov(rsp, rdx);
            a.sub(rsp, 0x20);
            a.mov(rdi, r8);
            a.call(rcx);
            a.mov(r10, rax);
            a.mov(rax, __qword_ptr[r12 - 0x20]);
            a.mov(cr3, rax);
            a.lea(rsp, __qword_ptr[r12 - 0x18]);
            a.popfq();
            a.mov(rdi, r13);
            a.pop(r13);
            a.pop(r12);
            a.mov(rax, r10);
            a.ret();

            var w = new StubBufWriter(p, cap);
            a.Assemble(w, 0);
            return w.Count;
        }

        // Same shape as SehDispatch.CompareOrPanic — local to keep the
        // dependency cone narrow. Length mismatch and per-byte mismatch
        // both panic with full diagnostics on the serial console so any
        // drift is visible at boot, not as a wild jump after we hand off
        // to the stub.
        private static void CompareOrPanic(string name, byte* iced, byte* legacy, int icedLen, int legacyLen)
        {
            if (icedLen != legacyLen)
            {
                Console.Write("[stub] ");
                Console.Write(name);
                Console.Write(" length mismatch: iced=0x");
                Console.WriteHex((ulong)icedLen);
                Console.Write(" legacy=0x");
                Console.WriteHex((ulong)legacyLen);
                Console.WriteLine("");
                OS.Kernel.Panic.Fail("stub iced/legacy length mismatch");
            }
            for (int i = 0; i < icedLen; i++)
            {
                if (iced[i] != legacy[i])
                {
                    Console.Write("[stub] ");
                    Console.Write(name);
                    Console.Write(" byte mismatch at offset 0x");
                    Console.WriteHex((ulong)i);
                    Console.Write(": iced=0x");
                    Console.WriteHex(iced[i]);
                    Console.Write(" legacy=0x");
                    Console.WriteHex(legacy[i]);
                    Console.WriteLine("");
                    OS.Kernel.Panic.Fail("stub iced/legacy byte mismatch");
                }
            }
        }
    }
}
