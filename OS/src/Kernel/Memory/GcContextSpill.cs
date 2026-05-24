using OS.PAL.SharpOSHost;

namespace OS.Kernel.Memory
{
    // Capture-CONTEXT shellcode for the precise GC walk (step 110 Part 7).
    //
    // Companion to GcStackSpill but instead of just pushing callee-saved
    // onto the stack, this writes the caller's GP register set into a
    // caller-provided Context struct, computes the caller's RSP/RIP, then
    // invokes a managed callback with Context* as argument. The callback
    // can use SehUnwind.VirtualUnwind to step the Context up the stack
    // frame by frame, doing precise GC root enumeration at each frame's
    // safepoint via CoffGcInfoDecoder + CoffGcInfoResolver.
    //
    // Calling convention (Win64):
    //   void Spill(Context* ctx, delegate* unmanaged<Context*, void> callback)
    //     rcx = Context*
    //     rdx = callback
    //
    // At entry:
    //   - rbx, rbp, rdi, rsi, r12..r15 = caller's callee-saved values
    //     (CALL doesn't touch them; we capture exactly what caller had)
    //   - rax, r8, r9, r10, r11 = caller-saved, may be clobbered by the
    //     caller's prologue / argument setup. Stored as 0 — GcInfo at a
    //     call-site PC never refers to caller-saved regs (refs cannot
    //     survive a call in a register that calls clobber, by definition).
    //   - rcx = Context*, rdx = callback: our args, NOT caller's rcx/rdx.
    //     Caller's rcx/rdx are caller-saved → stored as 0 above.
    //   - RSP = (caller's RSP at call site) - 8. The 8 bytes at [rsp] are
    //     the return address into caller. We use these to set ctx.Rip and
    //     ctx.Rsp accurately.
    //
    // Memory layout: installed at ExecStubBuffer offset 512 (we share the
    // exec buffer with Cr3Accessor/GcStackSpill/InterfaceDispatchBridge,
    // which together occupy 0..512 — see ExecStubBuffer layout comment in
    // UefiBootInfoBuilder). ExecStubBuffer total size bumped to 1024 in
    // step 110 Part 7 to make room.
    internal static unsafe class GcContextSpill
    {
        private const uint StubOffset = 512;
        private const uint StubSize   = 512;

        private static bool s_initialized;
        private static delegate* unmanaged<Context*, delegate* unmanaged<Context*, void>, void> s_invoke;

        public static bool IsInitialized => s_initialized;

        public static bool TryInitialize(void* execBuffer, uint execBufferSize)
        {
            if (s_initialized) return true;
            if (execBuffer == null || execBufferSize < StubOffset + StubSize) return false;

            byte* stub = (byte*)execBuffer + StubOffset;
            WriteShellcode(stub);
            s_invoke = (delegate* unmanaged<Context*, delegate* unmanaged<Context*, void>, void>)stub;
            s_initialized = true;
            return true;
        }

        public static void Invoke(Context* ctx, delegate* unmanaged<Context*, void> callback)
        {
            if (!s_initialized || s_invoke == null) return;
            s_invoke(ctx, callback);
        }

        // ---- AMD64 instruction encoding helpers ----

        // mov [rcx + disp32], reg64.  reg encoding follows AMD64 ModR/M
        // (Intel Vol 2A, Table 2-2): rax=0, rcx=1, rdx=2, rbx=3, rsp=4 (SIB),
        // rbp=5 (special), rsi=6, rdi=7. r8..r15 set REX.R and use 0..7.
        private static int EmitMovRcxDispReg(byte* p, int disp, int reg)
        {
            // REX.W (+ REX.R if reg >= 8)
            byte rex = (byte)(0x48 | ((reg >= 8) ? 0x04 : 0x00));
            p[0] = rex;
            p[1] = 0x89;                                 // MOV r/m64, r64
            int regLow = reg & 0x07;
            // ModR/M: mod=10 (disp32), reg=regLow, r/m=001 (rcx)
            p[2] = (byte)(0x80 | (regLow << 3) | 0x01);
            p[3] = (byte)(disp & 0xFF);
            p[4] = (byte)((disp >> 8) & 0xFF);
            p[5] = (byte)((disp >> 16) & 0xFF);
            p[6] = (byte)((disp >> 24) & 0xFF);
            return 7;
        }

        // xor eax, eax (zeroes rax via implicit zero-extend).
        private static int EmitXorEaxEax(byte* p) { p[0] = 0x31; p[1] = 0xC0; return 2; }

        // mov rax, [rsp]  — 48 8B 04 24
        private static int EmitMovRaxFromRsp0(byte* p)
        { p[0] = 0x48; p[1] = 0x8B; p[2] = 0x04; p[3] = 0x24; return 4; }

        // lea rax, [rsp + 8]  — 48 8D 44 24 08
        private static int EmitLeaRaxRspPlus8(byte* p)
        { p[0] = 0x48; p[1] = 0x8D; p[2] = 0x44; p[3] = 0x24; p[4] = 0x08; return 5; }

        // sub rsp, imm8  — 48 83 EC imm8
        private static int EmitSubRsp(byte* p, byte imm)
        { p[0] = 0x48; p[1] = 0x83; p[2] = 0xEC; p[3] = imm; return 4; }

        // add rsp, imm8  — 48 83 C4 imm8
        private static int EmitAddRsp(byte* p, byte imm)
        { p[0] = 0x48; p[1] = 0x83; p[2] = 0xC4; p[3] = imm; return 4; }

        // call rdx  — FF D2
        private static int EmitCallRdx(byte* p) { p[0] = 0xFF; p[1] = 0xD2; return 2; }

        // ret  — C3
        private static int EmitRet(byte* p) { p[0] = 0xC3; return 1; }

        // AMD64 GP register index → Context field offset.
        // Caller-saved regs (rax/rcx/rdx/r8..r11) get stored as 0 because
        // GcInfo at call-site PC never references them.
        // Callee-saved (rbx/rbp/rdi/rsi/r12..r15) carry caller's values.
        private const int OffRax = 0x78;
        private const int OffRcx = 0x80;
        private const int OffRdx = 0x88;
        private const int OffRbx = 0x90;
        private const int OffRsp = 0x98;
        private const int OffRbp = 0xA0;
        private const int OffRsi = 0xA8;
        private const int OffRdi = 0xB0;
        private const int OffR8  = 0xB8;
        private const int OffR9  = 0xC0;
        private const int OffR10 = 0xC8;
        private const int OffR11 = 0xD0;
        private const int OffR12 = 0xD8;
        private const int OffR13 = 0xE0;
        private const int OffR14 = 0xE8;
        private const int OffR15 = 0xF0;
        private const int OffRip = 0xF8;

        // Reg index in ModR/M: rax=0..rdi=7, r8=8..r15=15.
        private const int RegRax = 0;
        private const int RegRbx = 3;
        private const int RegRbp = 5;
        private const int RegRsi = 6;
        private const int RegRdi = 7;
        private const int RegR12 = 12;
        private const int RegR13 = 13;
        private const int RegR14 = 14;
        private const int RegR15 = 15;

        private static void WriteShellcode(byte* p)
        {
            int i = 0;
            // xor eax, eax  — common zero source for caller-saved fields.
            i += EmitXorEaxEax(p + i);

            // Caller-saved fields ← 0
            i += EmitMovRcxDispReg(p + i, OffRax, RegRax);   // [rcx+78] = rax (0)
            i += EmitMovRcxDispReg(p + i, OffRcx, RegRax);   // [rcx+80] = 0
            i += EmitMovRcxDispReg(p + i, OffRdx, RegRax);   // [rcx+88] = 0
            i += EmitMovRcxDispReg(p + i, OffR8,  RegRax);   // [rcx+B8] = 0
            i += EmitMovRcxDispReg(p + i, OffR9,  RegRax);   // [rcx+C0] = 0
            i += EmitMovRcxDispReg(p + i, OffR10, RegRax);   // [rcx+C8] = 0
            i += EmitMovRcxDispReg(p + i, OffR11, RegRax);   // [rcx+D0] = 0

            // Callee-saved fields ← caller's actual values
            i += EmitMovRcxDispReg(p + i, OffRbx, RegRbx);
            i += EmitMovRcxDispReg(p + i, OffRbp, RegRbp);
            i += EmitMovRcxDispReg(p + i, OffRsi, RegRsi);
            i += EmitMovRcxDispReg(p + i, OffRdi, RegRdi);
            i += EmitMovRcxDispReg(p + i, OffR12, RegR12);
            i += EmitMovRcxDispReg(p + i, OffR13, RegR13);
            i += EmitMovRcxDispReg(p + i, OffR14, RegR14);
            i += EmitMovRcxDispReg(p + i, OffR15, RegR15);

            // ctx.Rip = [rsp] (return address)
            i += EmitMovRaxFromRsp0(p + i);
            i += EmitMovRcxDispReg(p + i, OffRip, RegRax);

            // ctx.Rsp = rsp + 8 (caller's RSP at call site)
            i += EmitLeaRaxRspPlus8(p + i);
            i += EmitMovRcxDispReg(p + i, OffRsp, RegRax);

            // call rdx (callback). 32 bytes shadow + 8 align = 0x28.
            i += EmitSubRsp(p + i, 0x28);
            i += EmitCallRdx(p + i);
            i += EmitAddRsp(p + i, 0x28);
            i += EmitRet(p + i);
        }
    }
}
