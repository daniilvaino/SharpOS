using OS.Hal;

namespace OS.Boot.EH
{
    // Phase 1 step 5.1 — patches ThrowExStub.RhpThrowEx with shellcode
    // that builds PAL_LIMITED_CONTEXT + ExInfo on the stack and calls
    // managed RhpTest_ThrowIngress(exception, &exInfo). XMM6-XMM15 are
    // NOT spilled (sage 2: optional in 5.1, mandatory in 5.5b/step 7
    // when funclets restore them).
    //
    // Frame layout after `sub rsp, 0x388` (FRAME_SIZE):
    //
    //   [rsp + 0x000 .. 0x01F]   shadow space for callee
    //   [rsp + 0x020 .. 0x11F]   PAL_LIMITED_CONTEXT (size 0x100)
    //   [rsp + 0x120 .. 0x37F]   ExInfo              (size 0x260)
    //   [rsp + 0x380 .. 0x387]   8 bytes alignment pad
    //
    // Stack on entry (after CALL pushed return address, before our pushes):
    //   [rsp+0]  = throw-site return address
    //   [rsp+8]  = throw-site RSP value
    //
    // After 8 nonvol pushes (rbp/rdi/rsi/rbx/r12/r13/r14/r15) + sub rsp,0x388
    // total displacement from entry = 0x40 + 0x388 = 0x3C8. Entry RSP was
    // 8 mod 16 (Microsoft x64 ABI), so RSP after subtraction is 0 mod 16
    // — OK for the call.
    //
    // Two placeholder absolute addresses are patched in:
    //   - &ExInfoHead.s_head             (10-byte mov r8, imm64)
    //   - &RhpTest_ThrowIngress          (10-byte mov r10, imm64)
    internal static unsafe class ThrowExPatcher
    {
        // Stack slot offsets. PAL fields are at OffsetCtx + PalLimitedContext.OffsetX.
        private const int OffsetCtx     = 0x020;
        private const int OffsetExInfo  = 0x120;
        private const int FrameSize     = 0x388;

        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)ThrowExStub.GetMethodAddress();
            if (target == null) return false;

            // Resolve patch targets.
            byte** headAddr = ExInfoHead.GetHeadAddress();
            void* ingressFn = GetIngressAddress();

            int p = 0;

            // Capture throw-site rsp/rip BEFORE we push anything.
            // lea rax, [rsp+8]  — caller's RSP (CALL pushed return addr)
            target[p++] = 0x48; target[p++] = 0x8D; target[p++] = 0x44; target[p++] = 0x24; target[p++] = 0x08;
            // mov rdx, [rsp]    — return address = throw-site IP
            target[p++] = 0x48; target[p++] = 0x8B; target[p++] = 0x14; target[p++] = 0x24;

            // Spill nonvolatile GPRs.
            target[p++] = 0x41; target[p++] = 0x57;          // push r15
            target[p++] = 0x41; target[p++] = 0x56;          // push r14
            target[p++] = 0x41; target[p++] = 0x55;          // push r13
            target[p++] = 0x41; target[p++] = 0x54;          // push r12
            target[p++] = 0x53;                              // push rbx
            target[p++] = 0x56;                              // push rsi
            target[p++] = 0x57;                              // push rdi
            target[p++] = 0x55;                              // push rbp

            // sub rsp, FrameSize
            target[p++] = 0x48; target[p++] = 0x81; target[p++] = 0xEC;
            WriteUInt32(target, ref p, (uint)FrameSize);

            // PAL.Rsp = throw-site rsp (rax)
            EmitMovMemReg(target, ref p, regSrc: 0, prefixR: false,
                disp: OffsetCtx + PalLimitedContext.OffsetRsp);
            // PAL.IP = throw-site IP (rdx)
            EmitMovMemReg(target, ref p, regSrc: 2, prefixR: false,
                disp: OffsetCtx + PalLimitedContext.OffsetIP);

            // GPR snapshots into PAL.
            EmitMovMemReg(target, ref p, regSrc: 5, prefixR: false,
                disp: OffsetCtx + PalLimitedContext.OffsetRbp);     // rbp
            EmitMovMemReg(target, ref p, regSrc: 7, prefixR: false,
                disp: OffsetCtx + PalLimitedContext.OffsetRdi);     // rdi
            EmitMovMemReg(target, ref p, regSrc: 6, prefixR: false,
                disp: OffsetCtx + PalLimitedContext.OffsetRsi);     // rsi
            EmitMovMemReg(target, ref p, regSrc: 3, prefixR: false,
                disp: OffsetCtx + PalLimitedContext.OffsetRbx);     // rbx
            EmitMovMemReg(target, ref p, regSrc: 4, prefixR: true,
                disp: OffsetCtx + PalLimitedContext.OffsetR12);     // r12
            EmitMovMemReg(target, ref p, regSrc: 5, prefixR: true,
                disp: OffsetCtx + PalLimitedContext.OffsetR13);     // r13
            EmitMovMemReg(target, ref p, regSrc: 6, prefixR: true,
                disp: OffsetCtx + PalLimitedContext.OffsetR14);     // r14
            EmitMovMemReg(target, ref p, regSrc: 7, prefixR: true,
                disp: OffsetCtx + PalLimitedContext.OffsetR15);     // r15

            // rdx = &ExInfo  (arg2 of RhpTest_ThrowIngress)
            // lea rdx, [rsp+0x120]
            target[p++] = 0x48; target[p++] = 0x8D; target[p++] = 0x94; target[p++] = 0x24;
            WriteUInt32(target, ref p, (uint)OffsetExInfo);

            // rax = &PAL
            // lea rax, [rsp+0x020]
            target[p++] = 0x48; target[p++] = 0x8D; target[p++] = 0x84; target[p++] = 0x24;
            WriteUInt32(target, ref p, (uint)OffsetCtx);

            // ExInfo.m_pExContext = rax
            // mov [rdx+0x08], rax   (ExInfo.OffsetExContext = 0x08)
            target[p++] = 0x48; target[p++] = 0x89; target[p++] = 0x42; target[p++] = (byte)ExInfo.OffsetExContext;

            // r8 = &s_head
            // mov r8, imm64
            target[p++] = 0x49; target[p++] = 0xB8;
            WriteUInt64(target, ref p, (ulong)(nuint)headAddr);

            // r9 = *r8  (current head)
            // mov r9, [r8]
            target[p++] = 0x4D; target[p++] = 0x8B; target[p++] = 0x08;

            // ExInfo.m_pPrevExInfo = r9
            // mov [rdx], r9
            target[p++] = 0x4C; target[p++] = 0x89; target[p++] = 0x0A;

            // ExInfo.m_exception = null  (managed seam will populate; raw qword at offset 0x10)
            target[p++] = 0x48; target[p++] = 0x31; target[p++] = 0xC0;            // xor rax, rax
            target[p++] = 0x48; target[p++] = 0x89; target[p++] = 0x42; target[p++] = (byte)ExInfo.OffsetException;

            // ExInfo.m_kind = 1 (Throw)
            // mov byte [rdx+0x18], 1
            target[p++] = 0xC6; target[p++] = 0x42; target[p++] = (byte)ExInfo.OffsetKind; target[p++] = ExInfo.KindThrow;

            // ExInfo.m_passNumber = 1
            target[p++] = 0xC6; target[p++] = 0x42; target[p++] = (byte)ExInfo.OffsetPassNumber; target[p++] = 0x01;

            // ExInfo.m_idxCurClause = 0xFFFFFFFF
            // mov dword [rdx+0x1C], 0xFFFFFFFF
            target[p++] = 0xC7; target[p++] = 0x42; target[p++] = (byte)ExInfo.OffsetIdxCurClause;
            target[p++] = 0xFF; target[p++] = 0xFF; target[p++] = 0xFF; target[p++] = 0xFF;

            // s_head = &ExInfo
            // mov [r8], rdx
            target[p++] = 0x49; target[p++] = 0x89; target[p++] = 0x10;

            // RCX still holds exception object (untouched).
            // RDX holds &ExInfo.

            // Save exception ref in PAL.Rax slot too — convenient for managed
            // ingress to verify (as a placeholder; real RhThrowEx will write
            // it into ExInfo.m_exception, but for 5.1 we just snapshot).

            // Call RhpTest_ThrowIngress(exception, &exInfo)
            // mov r10, imm64
            target[p++] = 0x49; target[p++] = 0xBA;
            WriteUInt64(target, ref p, (ulong)(nuint)ingressFn);
            // call r10
            target[p++] = 0x41; target[p++] = 0xFF; target[p++] = 0xD2;

            // Should not return (managed RhpTest_ThrowIngress halts).
            target[p++] = 0xCC;     // int3

            // Sanity check: first byte 0x48 (REX.W) and last byte 0xCC (int3).
            if (target[0] != 0x48 || target[p - 1] != 0xCC)
                return false;

            Log.Begin(LogLevel.Info);
            Console.Write("throw-ex shellcode: bytes=");
            Console.WriteUIntRaw((uint)p);
            Console.Write(" head=0x");
            Console.WriteHexRaw((ulong)(nuint)headAddr, 16);
            Console.Write(" ingress=0x");
            Console.WriteHexRaw((ulong)(nuint)ingressFn, 16);
            Log.EndLine();

            s_installed = true;
            return true;
        }

        // Resolve address of OS.Boot.ExceptionEngine.RhpTest_ThrowIngress
        // through the [UnmanagedCallersOnly] export.
        private static void* GetIngressAddress()
        {
            delegate* unmanaged<byte*, ExInfo*, void> fn =
                &OS.Boot.ExceptionEngine.RhpTest_ThrowIngress;
            return (void*)fn;
        }

        // Emits: mov [rsp+disp32], reg
        //   regSrc 0..7  (rax/rcx/rdx/rbx/rsp/rbp/rsi/rdi or r8..r15 with REX.R)
        //   prefixR: true → REX.R set (uses r8..r15 numbered the same low 3 bits)
        //   disp: 32-bit displacement from rsp
        // Encoding: REX 89 ModR/M SIB disp32 = 8 bytes total.
        private static void EmitMovMemReg(byte* dst, ref int p, int regSrc, bool prefixR, int disp)
        {
            // REX: 0x48 (W=1) base; OR 0x04 (R=1) when prefixR.
            byte rex = (byte)(0x48 | (prefixR ? 0x04 : 0x00));
            dst[p++] = rex;
            dst[p++] = 0x89;
            // ModR/M: mod=10 (disp32), reg=regSrc (low 3 bits), r/m=100 (SIB)
            byte modrm = (byte)((0x80) | ((regSrc & 0x07) << 3) | 0x04);
            dst[p++] = modrm;
            // SIB: scale=0, index=4 (none), base=4 (rsp) = 0x24
            dst[p++] = 0x24;
            WriteUInt32(dst, ref p, (uint)disp);
        }

        private static void WriteUInt32(byte* dst, ref int p, uint val)
        {
            dst[p++] = (byte)(val);
            dst[p++] = (byte)(val >> 8);
            dst[p++] = (byte)(val >> 16);
            dst[p++] = (byte)(val >> 24);
        }

        private static void WriteUInt64(byte* dst, ref int p, ulong val)
        {
            dst[p++] = (byte)(val);
            dst[p++] = (byte)(val >> 8);
            dst[p++] = (byte)(val >> 16);
            dst[p++] = (byte)(val >> 24);
            dst[p++] = (byte)(val >> 32);
            dst[p++] = (byte)(val >> 40);
            dst[p++] = (byte)(val >> 48);
            dst[p++] = (byte)(val >> 56);
        }
    }
}
