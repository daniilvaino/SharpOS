// Iced-driven JumpStub emitter. Isolated in its own partial file so the
// `using static Iced.Intel.AssemblerRegisters` import (which dumps
// `rax`/`rcx`/…/`cr3`/`__qword_ptr`/… into the namespace) doesn't shadow
// anything in JumpStub's hot path. The legacy hand-rolled byte emitter and
// its byte-compare gate were removed (step137) after many green boots with
// zero drift; the entry call now passes the startup block in rcx (Win64
// arg0) for the freestanding PE apps.

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

        // Windows x64 ABI entry: rcx = JumpContext* (see JumpStub.cs).
        //
        // Every register the kernel caller expects back — rbx, rbp, rsi, rdi,
        // r12-r15, xmm6-15 — plus RFLAGS, CR3 and RSP is saved INTO THE
        // CONTEXT, not just on the stack. The app returns them on a normal
        // exit by the ABI, but an app that dies mid-flight returns nothing:
        // the abort entry (EmitAbortIced) has only the context to rebuild the
        // kernel from. The stub used to keep r12/r13 alone and trust the app
        // for the rest, which made "end this app, keep the machine" impossible.
        //
        //   save rbx..r15, xmm6-15 -> ctx
        //   pushfq; pop rax; mov [ctx.Rflags], rax
        //   cli                           ; mask IRQs over the CR3/RSP switch
        //   mov [ctx.KernelCr3], cr3
        //   mov [ctx.KernelRsp], rsp      ; [rsp] = return address into Run
        //   mov r12, rcx                  ; ctx survives the app in r12
        //   mov cr3, [ctx.Cr3]
        //   mov rsp, [ctx.StackTop]; sub rsp, 0x20
        //   mov rax, [ctx.Entry]; mov rcx, [ctx.Startup]
        //   sti                           ; the app runs preemptible
        //   call rax
        //   mov rcx, r12                  ; rax = exit code
        //   <resume>                      ; shared with the abort entry
        //
        // Why sti around the call: cli protects the CR3/RSP switch, and the
        // stub used to leave it masked for the WHOLE app. Nothing could then
        // preempt the app, and nothing driven by the timer ran while it did —
        // no scheduler, no sampler. Three separate hangs on the rig came back
        // as total silence for exactly that reason: the machine was alive and
        // had no way to say where it was. Unmasking after the switch and
        // masking again before the restore keeps the dangerous window as small
        // as it was, and makes the app observable for the first time.
        private static int EmitStubIced(byte* p, int cap)
        {
            var a = new Iced.Intel.Assembler(64);

            a.mov(__qword_ptr[rcx + JumpContext.RbxOffset], rbx);
            a.mov(__qword_ptr[rcx + JumpContext.RbpOffset], rbp);
            a.mov(__qword_ptr[rcx + JumpContext.RsiOffset], rsi);
            a.mov(__qword_ptr[rcx + JumpContext.RdiOffset], rdi);
            a.mov(__qword_ptr[rcx + JumpContext.R12Offset], r12);
            a.mov(__qword_ptr[rcx + JumpContext.R13Offset], r13);
            a.mov(__qword_ptr[rcx + JumpContext.R14Offset], r14);
            a.mov(__qword_ptr[rcx + JumpContext.R15Offset], r15);
            a.movdqu(__xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x00)], xmm6);
            a.movdqu(__xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x10)], xmm7);
            a.movdqu(__xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x20)], xmm8);
            a.movdqu(__xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x30)], xmm9);
            a.movdqu(__xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x40)], xmm10);
            a.movdqu(__xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x50)], xmm11);
            a.movdqu(__xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x60)], xmm12);
            a.movdqu(__xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x70)], xmm13);
            a.movdqu(__xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x80)], xmm14);
            a.movdqu(__xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x90)], xmm15);

            a.pushfq();
            a.pop(rax);
            a.mov(__qword_ptr[rcx + JumpContext.RflagsOffset], rax);
            a.cli();
            a.mov(rax, cr3);
            a.mov(__qword_ptr[rcx + JumpContext.KernelCr3Offset], rax);
            a.mov(__qword_ptr[rcx + JumpContext.KernelRspOffset], rsp);
            a.mov(r12, rcx);

            a.mov(rax, __qword_ptr[rcx + JumpContext.Cr3Offset]);
            a.mov(cr3, rax);
            a.mov(rsp, __qword_ptr[rcx + JumpContext.StackTopOffset]);
            a.sub(rsp, 0x20);
            a.mov(rax, __qword_ptr[rcx + JumpContext.EntryOffset]);
            a.mov(rcx, __qword_ptr[rcx + JumpContext.StartupOffset]);
            a.sti();
            a.call(rax);
            a.mov(rcx, r12);
            EmitResume(a);

            var w = new StubBufWriter(p, cap);
            a.Assemble(w, 0);
            return w.Count;
        }

        // Abort entry: rcx = JumpContext* of the run being ended, edx = exit
        // code. Called from the kernel's unhandled-exception path on whatever
        // stack the app died on; never returns there — it lands where the
        // normal exit lands, in JumpStub.Run, with rax = the exit code.
        private static int EmitAbortIced(byte* p, int cap)
        {
            var a = new Iced.Intel.Assembler(64);
            a.movsxd(rax, edx);
            EmitResume(a);

            var w = new StubBufWriter(p, cap);
            a.Assemble(w, 0);
            return w.Count;
        }

        // rcx = JumpContext*, rax = exit code. Puts the kernel back exactly as
        // the stub found it and returns to JumpStub.Run.
        private static void EmitResume(Iced.Intel.Assembler a)
        {
            a.cli();                            // mask again before CR3/RSP are restored
            a.mov(rdx, __qword_ptr[rcx + JumpContext.KernelCr3Offset]);
            a.mov(cr3, rdx);
            a.mov(rsp, __qword_ptr[rcx + JumpContext.KernelRspOffset]);
            a.mov(rbx, __qword_ptr[rcx + JumpContext.RbxOffset]);
            a.mov(rbp, __qword_ptr[rcx + JumpContext.RbpOffset]);
            a.mov(rsi, __qword_ptr[rcx + JumpContext.RsiOffset]);
            a.mov(rdi, __qword_ptr[rcx + JumpContext.RdiOffset]);
            a.mov(r12, __qword_ptr[rcx + JumpContext.R12Offset]);
            a.mov(r13, __qword_ptr[rcx + JumpContext.R13Offset]);
            a.mov(r14, __qword_ptr[rcx + JumpContext.R14Offset]);
            a.mov(r15, __qword_ptr[rcx + JumpContext.R15Offset]);
            a.movdqu(xmm6, __xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x00)]);
            a.movdqu(xmm7, __xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x10)]);
            a.movdqu(xmm8, __xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x20)]);
            a.movdqu(xmm9, __xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x30)]);
            a.movdqu(xmm10, __xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x40)]);
            a.movdqu(xmm11, __xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x50)]);
            a.movdqu(xmm12, __xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x60)]);
            a.movdqu(xmm13, __xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x70)]);
            a.movdqu(xmm14, __xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x80)]);
            a.movdqu(xmm15, __xmmword_ptr[rcx + (JumpContext.XmmOffset + 0x90)]);
            a.push(__qword_ptr[rcx + JumpContext.RflagsOffset]);
            a.popfq();                          // restore RFLAGS (re-enables IRQs)
            a.ret();
        }
    }
}
