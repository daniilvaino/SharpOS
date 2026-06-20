// step 115 follow-up: Iced-driven replacement for the EmitCapture /
// EmitRestore byte emitters in SehDispatch. The old hand-rolled byte
// streams still live next to these (see SehDispatch.cs) and EnsureShellcode
// emits both in parallel and byte-compares them via CompareOrPanic. This
// half is isolated in its own partial file so the `using static
// Iced.Intel.AssemblerRegisters` import (which dumps `rax`/`rcx`/…/
// `__qword_ptr`/… into the namespace) doesn't shadow anything in
// SehDispatch's hot path. After several green boots the legacy emitters +
// the compare gate get pulled out and only the Iced halves remain.

using OS.Boot.EH;
using OS.Hal;
using OS.Kernel;
using static Iced.Intel.AssemblerRegisters;

namespace OS.PAL.SharpOSHost
{
    internal static unsafe partial class SehDispatch
    {
        // Iced CodeWriter that pours bytes straight into the target shellcode
        // buffer. Identical shape to the BufWriter in NativeAotProbe — kept
        // local so each Iced consumer owns its own writer surface and we
        // don't have to plumb a public std-tier dependency on Iced.
        private sealed class CaptureBufWriter : Iced.Intel.CodeWriter
        {
            private readonly byte* _p;
            private readonly int _cap;
            private int _i;
            public CaptureBufWriter(byte* p, int capacity) { _p = p; _cap = capacity; _i = 0; }
            public int Count => _i;
            public override void WriteByte(byte value)
            {
                if (_i < _cap) _p[_i++] = value;
            }
        }

        // mov [rcx+0x30], 0x100003
        // mov [rcx+0x78..0xF0], <rax..r15 minus rsp>
        // mov rax, [rsp]            ; saved return addr -> Rip
        // mov [rcx+0xF8], rax
        // lea rax, [rsp+8]          ; caller's Rsp
        // mov [rcx+0x98], rax
        // pushfq; pop rax; mov [rcx+0x44], eax
        // ret
        private static int EmitCaptureIced(byte* p, int cap)
        {
            var a = new Iced.Intel.Assembler(64);

            // ContextFlags = CONTEXT_AMD64 (0x100000) | CONTEXT_CONTROL (1)
            //              | CONTEXT_INTEGER (2). Same constant as the
            // legacy byte stream (mov dword ptr [rcx+0x30], 0x100003).
            a.mov(__dword_ptr[rcx + 0x30], 0x100003);

            // GP regs into ctx (rsp omitted — computed via lea below).
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

            var w = new CaptureBufWriter(p, cap);
            a.Assemble(w, 0);
            return w.Count;
        }

        // mov rax, [rcx+0xF8]       ; target Rip
        // mov rdx, [rcx+0x98]       ; target Rsp
        // mov [rdx-8], rax          ; pre-place Rip below new SP for ret
        // mov eax, [rcx+0x44]       ; target EFlags
        // push rax; popfq
        // restore GP regs (rbx..r15, rdx, rax) — NOT rcx, NOT rsp, NOT rip
        // mov rsp, [rcx+0x98]
        // sub rsp, 8
        // mov rcx, [rcx+0x80]       ; finally restore Rcx (was our ctx ptr)
        // ret                       ; jumps to pre-placed Rip
        private static int EmitRestoreIced(byte* p, int cap)
        {
            var a = new Iced.Intel.Assembler(64);

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

            var w = new CaptureBufWriter(p, cap);
            a.Assemble(w, 0);
            return w.Count;
        }

        // Byte-by-byte compare of the Iced-emitted shellcode against the
        // legacy byte-emitter result. Length mismatch and per-byte mismatch
        // both panic with full diagnostics on the serial console so any
        // drift is visible at boot, not as a corrupted ctx later.
        private static void CompareOrPanic(string name, byte* iced, byte* legacy, int icedLen, int legacyLen)
        {
            if (icedLen != legacyLen)
            {
                Console.Write("[shellcode] ");
                Console.Write(name);
                Console.Write(" length mismatch: iced=0x");
                Console.WriteHex((ulong)icedLen);
                Console.Write(" legacy=0x");
                Console.WriteHex((ulong)legacyLen);
                Console.WriteLine("");
                Panic.Fail("shellcode iced/legacy length mismatch");
            }
            for (int i = 0; i < icedLen; i++)
            {
                if (iced[i] != legacy[i])
                {
                    Console.Write("[shellcode] ");
                    Console.Write(name);
                    Console.Write(" byte mismatch at offset 0x");
                    Console.WriteHex((ulong)i);
                    Console.Write(": iced=0x");
                    Console.WriteHex(iced[i]);
                    Console.Write(" legacy=0x");
                    Console.WriteHex(legacy[i]);
                    Console.WriteLine("");
                    Panic.Fail("shellcode iced/legacy byte mismatch");
                }
            }
        }
    }
}
