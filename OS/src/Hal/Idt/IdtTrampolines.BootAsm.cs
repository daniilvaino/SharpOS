// step 119 Wave 5 — compile-time codegen migration of
// IdtTrampolines.WriteCommonStub. 1 DataSlotHole: dispatcherSlot, 8-byte
// qword at end of stub holding the address of the managed dispatcher
// (IdtDispatcher [UnmanagedCallersOnly]). The `mov rax, [rip + slot]`
// load uses an Iced label pointing at the slot — Iced encodes the
// disp32 automatically.
//
// Stack on entry (post-CPU exception, AFTER per-vector stub pushed dummy/
// vec+errcode): [rip][cs][rflags][rsp][ss] on top, then [vector][errcode].
// We push 15 GPRs + cr2 = 16 qwords. Alignment: 5 (CPU) + 2 (vec+ec) +
// 16 (pushes) = 23 → caller-base + 8. `sub rsp, 0x28` → 28 = 0 mod 16.
//
// WriteVectorStub is NOT migrated — it's parametric on `vector` and on
// the runtime address of commonStub; both are per-call inputs, not stub
// holes, so the byte template can't be baked at compile time. Stays as
// the raw byte emitter in IdtTrampolines.cs.

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Hal.Idt
{
    internal static unsafe partial class IdtTrampolines
    {
        [CompileTimeAsm]
        private static partial int EmitCommonStub(byte* dst, void* dispatcherSlot);

        // The last two hand-written byte streams in the tree, four bytes each:
        // `0F 01 19 C3` and `0F 01 09 C3`. They sat in Idt.Install as literal
        // writes while the vector stubs one line above already came from here.
        //
        // Win64: the first argument is in rcx and points at a 10-byte
        // IdtRegister (limit + base), which is exactly what both instructions
        // take.
        [CompileTimeAsm]
        private static partial int EmitLoadIdtHelper(byte* dst);

        [CompileTimeAsmBody(nameof(EmitLoadIdtHelper))]
        private static void EmitLoadIdtHelper_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            a.lidt(__[rcx]);
            a.ret();
        }

        [CompileTimeAsm]
        private static partial int EmitStoreIdtHelper(byte* dst);

        [CompileTimeAsmBody(nameof(EmitStoreIdtHelper))]
        private static void EmitStoreIdtHelper_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            a.sidt(__[rcx]);
            a.ret();
        }

        [CompileTimeAsmBody(nameof(EmitCommonStub))]
        private static void EmitCommonStub_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            var dispData = a.CreateLabel();

            // Push 8 extended GPRs (r15..r8) then 7 legacy (rbp..rax).
            a.push(r15); a.push(r14); a.push(r13); a.push(r12);
            a.push(r11); a.push(r10); a.push(r9);  a.push(r8);
            a.push(rbp); a.push(rdi); a.push(rsi); a.push(rbx);
            a.push(rdx); a.push(rcx); a.push(rax);

            // mov rax, cr2 ; push rax  (top of frame matches InterruptFrame.Cr2)
            a.mov(rax, cr2);
            a.push(rax);

            // mov rcx, rsp  (arg1 = frame*)
            a.mov(rcx, rsp);

            // Save x87/SSE state below the frame.
            //
            // The dispatcher is C#, and C# codegen uses XMM registers freely —
            // it will happily pick xmm4 just to zero a stack slot. XMM0-5 are
            // volatile in Win64, i.e. they belong to whatever we interrupted.
            // Any handler that RETURNS (demand paging today, the timer tick
            // next) therefore corrupts the interrupted code's registers unless
            // they are saved here. Verified, not assumed: X64PageTable.MapKernel
            // and TryQueryForRoot both emit `xorps xmm4,xmm4` in the shipped
            // object file, and both sit on the demand-paging resume path.
            //
            // FXSAVE (not XSAVE) is exactly right: XCR0 is locked to x87|SSE,
            // so there is no AVX state to miss.
            //
            // Alignment, which FXSAVE requires to be 16: the CPU aligns RSP to
            // 16 on interrupt entry, then 23 qwords go on the stack (5 CPU +
            // 2 vector/errcode + 16 here), leaving RCX at base+8 mod 16.
            // 520 is 8 mod 16, so RCX-520 lands back on 0 mod 16.
            // Literal, not FpAreaBytes: the compile-time walker resolves only
            // literals and register names, and says so loudly if given a
            // constant. The named constant next door is the managed-side
            // documentation of this number; both emitters spell it out.
            a.sub(rsp, 520);
            a.fxsave(__zmmword_ptr[rsp]);

            // sub rsp, 0x20  (shadow space; 23 qwords + 520 + 32 = 0 mod 16,
            // which is what Win64 wants immediately before the call)
            a.sub(rsp, 0x20);

            // mov rax, [rip + dispData]  ; RIP-relative load of dispatcher
            a.mov(rax, __qword_ptr[dispData]);
            a.call(rax);

            // 1: hlt ; jmp 1b   (dispatcher is no-return; this guards against
            //                    accidental returns by halting forever).
            // Use Iced label so the rel8 backward jump encodes correctly.
            var haltLoop = a.CreateLabel();
            a.Label(ref haltLoop);
            a.hlt();
            a.jmp(haltLoop);

            // 8-byte align before data slot. h.DataSlotHole emits exactly 8
            // bytes; align manually with NOPs first.
            // Iced lets us pad via a.nop() or a.db(0x90) — we use db for
            // explicit 1-byte form (a.nop() may pick multi-byte forms).
            // The compile-time encoder gives us a stable byte offset for
            // the label, so the alignment is deterministic across builds.
            // (Walker emits db(byte[]) — params for a.db is byte[], so a
            // single padding nop goes one at a time via db(0x90).)
            // NOTE: at present the prior 3-byte tail (hlt+jmp rel8 = 1+2=3
            // bytes, the rest before is 0x35 bytes by the original layout
            // ⇒ next offset is 0x38) sits already 8-aligned. We still emit
            // an explicit alignment guard via db(0x90) padding if needed —
            // walker can't compute `(off & 7)` symbolically, so we omit
            // pad: if the natural layout drifts misaligned, the runtime
            // load is still legal (mov disp32 doesn't require alignment).

            h.DataSlotHole(a, ref dispData, "dispatcherSlot");
        }

        // ─────────────────────────────────────────────────────────────────
        // Per-vector entry stubs (16-byte slots).
        //
        // With dummy push (non-error vectors): 2 + 5 + 5 = 12 bytes,
        // padded to 16 with 4 nops.
        //   6A 00              push 0          ; dummy error code
        //   68 <vector-hole>   push <vec>      ; PushImm32Hole, runtime fills
        //   E9 <jmp-hole>      jmp commonStub  ; JmpRelHole, runtime fills
        //   90 × 4             nop padding
        //
        // Without dummy (error vectors): 5 + 5 = 10 bytes, padded to 16 with
        // 6 nops.
        //   68 <vector-hole>
        //   E9 <jmp-hole>
        //   90 × 6
        //
        // Vector # is pushed as imm32 instead of imm8 (5 bytes vs 2). The CPU
        // doesn't care — common stub reads the full qword from stack. Saves
        // us a runtime imm8 hole mechanism for one stub.
        // ─────────────────────────────────────────────────────────────────

        [CompileTimeAsm]
        private static partial int EmitVectorStubNoErr(byte* dst, uint vector, void* commonStub);

        [CompileTimeAsmBody(nameof(EmitVectorStubNoErr))]
        private static void EmitVectorStubNoErr_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            a.db(0x6A, 0x00);            // push 0 (dummy err code)
            h.PushImm32Hole(a, "vector");
            h.JmpRelHole(a, "commonStub");
            a.db(0x90, 0x90, 0x90, 0x90);
        }

        [CompileTimeAsm]
        private static partial int EmitVectorStubWithErr(byte* dst, uint vector, void* commonStub);

        [CompileTimeAsmBody(nameof(EmitVectorStubWithErr))]
        private static void EmitVectorStubWithErr_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            h.PushImm32Hole(a, "vector");
            h.JmpRelHole(a, "commonStub");
            a.db(0x90, 0x90, 0x90, 0x90, 0x90, 0x90);
        }
    }
}
